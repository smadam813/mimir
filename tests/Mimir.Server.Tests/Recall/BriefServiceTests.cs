using Microsoft.EntityFrameworkCore;
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

    private async Task<string> Compose(Guid projectId, string? sessionId = null, RecallOptions? options = null)
    {
        var service = new BriefService(
            Context,
            Options.Create(options ?? new RecallOptions()),
            new FakeTimeProvider(Now));
        return await service.ComposeBriefAsync(sessionId ?? NewSessionId(), projectId, Token);
    }

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
