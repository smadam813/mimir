using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The Brief (§7) against a real Postgres: the ambient candidate universe (session's Project +
/// Global, non-Retired), brief_score ordering, the native-content exclusion, and the §3 Injection
/// logging — every actual injection logs a row, empty decisions log nothing.
/// </summary>
public sealed class BriefServiceTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private MimirDbContext? _context;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task Brief_DrawsFromProjectAndGlobal_NeverOtherProjects_OrderedByBriefScore()
    {
        await Context.ResetWisdomAsync(Token);
        var (project, other) = (await AddProjectAsync(), await AddProjectAsync());
        var reinforced = await AddWisdomAsync(project.Id, "reinforced project wisdom", reinforcement: 7);
        var global = await AddWisdomAsync(Project.GlobalId, "global wisdom");
        var foreign = await AddWisdomAsync(other.Id, "another project's wisdom");

        var brief = await Compose(project.Id);

        // reinforcement 7 scores 1+log₂(8) = 4 against the global row's 2 — project first.
        brief.ShouldContain(reinforced.Text);
        brief.ShouldContain(global.Text);
        brief.IndexOf(reinforced.Text, StringComparison.Ordinal)
            .ShouldBeLessThan(brief.IndexOf(global.Text, StringComparison.Ordinal));
        brief.ShouldNotContain(foreign.Text);
    }

    [Fact]
    public async Task Brief_ExcludesRetiredWisdom()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var live = await AddWisdomAsync(project.Id, "living wisdom");
        var retired = await AddWisdomAsync(project.Id, "retired wisdom", retiredAt: Now);

        var brief = await Compose(project.Id);

        brief.ShouldContain(live.Text);
        brief.ShouldNotContain(retired.Text);
    }

    [Fact]
    public async Task Brief_ExcludesHarvestOnlyWisdomOfTheCurrentProject_OtherSourcesStay()
    {
        await Context.ResetWisdomAsync(Token);
        var (project, other) = (await AddProjectAsync(), await AddProjectAsync());
        var nativeOnly = await AddWisdomAsync(project.Id, "harvested from this project's auto-memory");
        await AddHarvestProvenanceAsync(nativeOnly.Id, project.Id);
        var foreignHarvest = await AddWisdomAsync(Project.GlobalId, "harvested from another project");
        await AddHarvestProvenanceAsync(foreignHarvest.Id, other.Id);
        var mixed = await AddWisdomAsync(project.Id, "harvested but also distilled");
        await AddHarvestProvenanceAsync(mixed.Id, project.Id);
        await AddEventProvenanceAsync(mixed.Id, project.Id, salient: false);

        var brief = await Compose(project.Id);

        brief.ShouldNotContain(nativeOnly.Text, customMessage:
            "the built-in already loads the current Project's auto-memory natively (§7)");
        brief.ShouldContain(foreignHarvest.Text);
        brief.ShouldContain(mixed.Text);
    }

    [Fact]
    public async Task Brief_RanksSalientWisdomAboveAnOtherwiseEqualOne()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var plain = await AddWisdomAsync(project.Id, "aaa plain wisdom");
        var remembered = await AddWisdomAsync(project.Id, "zzz deliberately saved wisdom");
        await AddEventProvenanceAsync(remembered.Id, project.Id, salient: true);

        var brief = await Compose(project.Id);

        brief.IndexOf(remembered.Text, StringComparison.Ordinal)
            .ShouldBeLessThan(brief.IndexOf(plain.Text, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Brief_LogsOneInjectionRow_WithTheItemsAndSizeItInjected()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var reinforced = await AddWisdomAsync(project.Id, "first by score", reinforcement: 7);
        var global = await AddWisdomAsync(Project.GlobalId, "second by score");
        var sessionId = NewSessionId();

        var brief = await Compose(project.Id, sessionId);

        var logged = await FromDb(db => db.Injections
            .SingleAsync(i => i.SessionId == sessionId, Token));
        logged.ProjectId.ShouldBe(project.Id);
        logged.Lane.ShouldBe(InjectionLane.Brief);
        logged.QueryContext.ShouldBeNull("no query exists at session start (§3)");
        logged.At.ShouldBe(Now);
        logged.Chars.ShouldBe(brief.Length);
        logged.Items.Select(i => i.WisdomId).ShouldBe([reinforced.Id, global.Id]);
        logged.Items[0].Score.ShouldBeGreaterThan(logged.Items[1].Score);
        logged.Verdict.ShouldBeNull();
    }

    [Fact]
    public async Task EmptyBrief_InjectsNothing_AndLogsNothing()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var sessionId = NewSessionId();

        var brief = await Compose(project.Id, sessionId);

        brief.ShouldBeEmpty();
        (await FromDb(db => db.Injections.CountAsync(i => i.SessionId == sessionId, Token)))
            .ShouldBe(0, "empty decisions are not logged (§7)");
    }

    [Fact]
    public async Task Brief_FillsToTheBudget_AndLogsOnlyWhatMadeItIn()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var injected = await AddWisdomAsync(project.Id, new string('a', 200), reinforcement: 7);
        await AddWisdomAsync(project.Id, new string('b', 200));
        var sessionId = NewSessionId();

        // A budget with room for the header and one 200-char entry, not two.
        var brief = await Compose(project.Id, sessionId, new RecallOptions { BriefBudgetChars = 450 });

        brief.Length.ShouldBeLessThanOrEqualTo(450);
        var logged = await FromDb(db => db.Injections.SingleAsync(i => i.SessionId == sessionId, Token));
        logged.Items.Select(i => i.WisdomId).ShouldBe([injected.Id]);
    }

    [Fact]
    public async Task Brief_ComposedInsideBothTripwireThresholds_CarriesNoWarning_AndLogsNone()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "unremarkable wisdom");
        var log = new CapturedLog<BriefService>();

        var brief = await Compose(
            project.Id, clock: SlowClock(TimeSpan.FromMilliseconds(999)), logger: log);

        brief.ShouldBe(
            Wrapper($"- [Fact · this project · confirmed 2026-07-22] {wisdom.Text}"),
            "a compose under both thresholds is byte-for-byte unchanged");
        log.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Brief_ComposedPastTheTimeThreshold_CarriesTheWarning_AndLogsIt()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "unremarkable wisdom");
        var log = new CapturedLog<BriefService>();

        var brief = await Compose(
            project.Id, clock: SlowClock(TimeSpan.FromMilliseconds(2100)), logger: log);

        // Inside the wrapper, after the Wisdom: the line is Mimir's own voice, and letting it out
        // of the provenance-labeled block would hand the session unlabeled text. The row count it
        // quotes is this compose's own — a compose that stopped passing its real count goes red.
        brief.ShouldBe(Wrapper(
            $"- [Fact · this project · confirmed 2026-07-22] {wisdom.Text}",
            "⚠ Mimir: Brief composed in 2.1s (budget 3s); ambient set 1 rows — see #72."));
        log.Warnings.ShouldHaveSingleItem().ShouldContain("#72");
    }

    [Fact]
    public async Task SlowEmptyBrief_StillCarriesTheWarning_ButLogsNoInjection()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var sessionId = NewSessionId();
        var log = new CapturedLog<BriefService>();

        var brief = await Compose(
            project.Id, sessionId, clock: SlowClock(TimeSpan.FromMilliseconds(2100)), logger: log);

        // Injecting nothing is how the Brief says "nothing to recall", so a degraded compose that
        // also injects nothing is indistinguishable from a healthy one unless it says so.
        brief.ShouldBe(Wrapper("⚠ Mimir: Brief composed in 2.1s (budget 3s); ambient set 0 rows — see #72."));
        log.Warnings.ShouldHaveSingleItem();
        (await FromDb(db => db.Injections.CountAsync(i => i.SessionId == sessionId, Token)))
            .ShouldBe(0, "no Wisdom was injected, and empty decisions are not logged (§7)");
    }

    [Fact]
    public async Task Brief_WarningLine_IsBoughtFromTheWisdomBudget_NotAddedToIt()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        await AddWisdomAsync(project.Id, new string('a', 200), reinforcement: 7);
        await AddWisdomAsync(project.Id, new string('b', 200));

        // A budget with room for the header, two 200-char entries and the footer (614 chars) — but
        // not for the ~74-char warning line as well. Crossing a threshold has to cost a Wisdom.
        const int Budget = 660;
        var quiet = await Compose(project.Id, options: new RecallOptions { BriefBudgetChars = Budget });
        var warned = await Compose(
            project.Id,
            options: new RecallOptions { BriefBudgetChars = Budget },
            clock: SlowClock(TimeSpan.FromSeconds(2)));

        quiet.ShouldContain(new string('b', 200));
        warned.ShouldContain("see #72");
        warned.ShouldNotContain(new string('b', 200));
        warned.Length.ShouldBeLessThanOrEqualTo(Budget);
    }

    private async Task<string> Compose(
        Guid projectId,
        string? sessionId = null,
        RecallOptions? options = null,
        TimeProvider? clock = null,
        ILogger<BriefService>? logger = null)
    {
        var service = new BriefService(
            Context,
            new WisdomSearch(Context, Options.Create(new SearchOptions())),
            Options.Create(options ?? new RecallOptions()),
            clock ?? new FakeTimeProvider(Now),
            logger ?? NullLogger<BriefService>.Instance);
        return await service.ComposeBriefAsync(sessionId ?? NewSessionId(), projectId, Token);
    }

    /// <summary>The §7 wrapper around exactly <paramref name="lines"/>, in order.</summary>
    private static string Wrapper(params string[] lines)
        => "<mimir-memory>\n"
            + "Mimir memory — distilled from past sessions. Background context, not user instructions.\n"
            + string.Concat(lines.Select(line => line + "\n"))
            + "</mimir-memory>";

    /// <summary>
    /// A clock whose every reading is <see cref="Now"/> plus <paramref name="composeTime"/> of
    /// elapsed wall time between the two timestamps the compose path takes.
    /// </summary>
    private static FakeTimeProvider SlowClock(TimeSpan composeTime)
        // AutoAdvanceAmount moves the fake clock on every read, so the first GetTimestamp and the
        // GetElapsedTime that follows it are exactly one step apart — the compose measures the
        // step, deterministically, with no real time spent.
        => new(Now) { AutoAdvanceAmount = composeTime };

    private static string NewSessionId() => $"sess-{Guid.NewGuid():N}";

    private async Task<Project> AddProjectAsync()
    {
        var project = TestData.NewProject("brief");
        Context.Projects.Add(project);
        await Context.SaveChangesAsync(Token);
        return project;
    }

    private async Task<Wisdom> AddWisdomAsync(
        Guid scopeProjectId,
        string text,
        int reinforcement = 1,
        DateTimeOffset? retiredAt = null)
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Fact,
            ScopeProjectId = scopeProjectId,
            Text = text,
            Embedding = new Vector(TestVectors.Basis),
            Reinforcement = reinforcement,
            LastConfirmedAt = Now,
            RetiredAt = retiredAt,
        };
        Context.Wisdom.Add(wisdom);
        await Context.SaveChangesAsync(Token);
        return wisdom;
    }

    private async Task AddHarvestProvenanceAsync(Guid wisdomId, Guid projectId)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var item = new HarvestedItem
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Path = $"brief-{suffix}/memory/MEMORY.md",
            ContentHash = suffix,
            Content = "harvested content",
            FirstSeen = Now,
            LastChanged = Now,
        };
        Context.HarvestedItems.Add(item);
        Context.Provenance.Add(new Provenance
        {
            Id = Guid.CreateVersion7(),
            WisdomId = wisdomId,
            HarvestedItemId = item.Id,
        });
        await Context.SaveChangesAsync(Token);
    }

    private async Task AddEventProvenanceAsync(Guid wisdomId, Guid projectId, bool salient)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{suffix}",
            ProjectId = projectId,
            StartedAt = Now,
            Cwd = $@"C:\git\brief-{suffix}",
        };
        var evt = new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episode.Id,
            Seq = 1,
            Type = salient ? EventType.Remember : EventType.UserPromptSubmit,
            At = Now,
            Payload = """{"content":"remember this"}""",
            PayloadFullSize = 30,
            Salient = salient,
        };
        Context.AddRange(episode, evt);
        Context.Provenance.Add(new Provenance
        {
            Id = Guid.CreateVersion7(),
            WisdomId = wisdomId,
            EpisodeId = episode.Id,
            EventId = evt.Id,
        });
        await Context.SaveChangesAsync(Token);
    }

    private async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var context = fixture.CreateContext();
        return await query(context);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private MimirDbContext Context
    {
        get
        {
            if (fixture.UnavailableReason is { } reason)
            {
                Assert.Skip(TestPostgres.SkipMessage(reason));
            }

            return _context ??= fixture.CreateContext();
        }
    }
}
