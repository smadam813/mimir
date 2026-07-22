using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;
using Mimir.Server.Ui;
using Pgvector;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// Spec §8.1 against a real Postgres: the queries behind the Wisdom browser — search plus the
/// five filters (kind/scope/project/contested/retired), the orphaned-provenance flag, the detail
/// with its version chain and Provenance drill-down — and the curation actions: edit (new
/// version, <c>cause=edited</c>, re-embed), Retire/unretire, and the confirmed Delete.
/// </summary>
public sealed class WisdomBrowserTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeEmbeddings _embeddings = new();

    private readonly FakeTimeProvider _clock = new(Now);

    private FixtureContextFactory Contexts => new(fixture);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task TheDefaultListing_ShowsActiveWisdomNewestFirst_AndExcludesRetired()
    {
        await ResetWisdomAsync();
        var old = await SeedWisdomAsync(text: "old fact", lastConfirmedAt: Now.AddDays(-2));
        var fresh = await SeedWisdomAsync(text: "fresh fact", lastConfirmedAt: Now);
        await SeedWisdomAsync(text: "retired fact", retiredAt: Now);

        var listing = await Browser().ListAsync(new WisdomQuery(), Token);

        listing.Select(w => w.Id).ShouldBe([fresh.Id, old.Id]);
    }

    [Fact]
    public async Task TheKindFilter_NarrowsToTheKind()
    {
        await ResetWisdomAsync();
        var lesson = await SeedWisdomAsync(kind: WisdomKind.Lesson);
        await SeedWisdomAsync(kind: WisdomKind.Fact);

        var listing = await Browser().ListAsync(new WisdomQuery(Kind: WisdomKind.Lesson), Token);

        listing.Select(w => w.Id).ShouldBe([lesson.Id]);
        listing[0].Kind.ShouldBe(WisdomKind.Lesson);
    }

    [Fact]
    public async Task TheScopeFilter_SeparatesGlobalFromProjectScoped()
    {
        await ResetWisdomAsync();
        var project = await SeedProjectAsync("scope");
        var global = await SeedWisdomAsync(scopeProjectId: Project.GlobalId);
        var scoped = await SeedWisdomAsync(scopeProjectId: project.Id);

        var globals = await Browser().ListAsync(new WisdomQuery(Scope: WisdomScopeFilter.Global), Token);
        var projectScoped = await Browser().ListAsync(
            new WisdomQuery(Scope: WisdomScopeFilter.ProjectScoped), Token);

        globals.Select(w => w.Id).ShouldBe([global.Id]);
        globals[0].ScopeName.ShouldBe("Global");
        projectScoped.Select(w => w.Id).ShouldBe([scoped.Id]);
        projectScoped[0].ScopeName.ShouldBe(project.DisplayName);
    }

    [Fact]
    public async Task TheProjectFilter_NarrowsToOneProjectsScope()
    {
        await ResetWisdomAsync();
        var mine = await SeedProjectAsync("mine");
        var other = await SeedProjectAsync("other");
        var kept = await SeedWisdomAsync(scopeProjectId: mine.Id);
        await SeedWisdomAsync(scopeProjectId: other.Id);
        await SeedWisdomAsync(scopeProjectId: Project.GlobalId);

        var listing = await Browser().ListAsync(new WisdomQuery(ProjectId: mine.Id), Token);

        listing.Select(w => w.Id).ShouldBe([kept.Id]);
    }

    [Fact]
    public async Task TheContestedFilter_SurfacesOnlyAdjudicationSurvivors()
    {
        await ResetWisdomAsync();
        var contested = await SeedWisdomAsync(contestedAt: Now.AddDays(-1));
        await SeedWisdomAsync();

        var listing = await Browser().ListAsync(new WisdomQuery(ContestedOnly: true), Token);

        listing.Select(w => w.Id).ShouldBe([contested.Id]);
        listing[0].ContestedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task TheRetirementFilter_ShowsRetiredAlone_OrEverything()
    {
        await ResetWisdomAsync();
        var active = await SeedWisdomAsync(lastConfirmedAt: Now);
        var retired = await SeedWisdomAsync(retiredAt: Now, lastConfirmedAt: Now.AddDays(-1));

        var retiredOnly = await Browser().ListAsync(
            new WisdomQuery(Retirement: WisdomRetirementFilter.Retired), Token);
        var all = await Browser().ListAsync(
            new WisdomQuery(Retirement: WisdomRetirementFilter.All), Token);

        retiredOnly.Select(w => w.Id).ShouldBe([retired.Id]);
        all.Select(w => w.Id).ShouldBe([active.Id, retired.Id]);
    }

    [Fact]
    public async Task Search_MatchesWordsAndSubstrings_ButNeverRetiredByDefault()
    {
        await ResetWisdomAsync();
        var worded = await SeedWisdomAsync(text: "zebras graze at dawn");
        var substring = await SeedWisdomAsync(text: "the quagga-zebrafish overlap");
        await SeedWisdomAsync(text: "unrelated filler");
        await SeedWisdomAsync(text: "retired zebra lore", retiredAt: Now);

        var listing = await Browser().ListAsync(new WisdomQuery(Search: "zebra"), Token);

        listing.Select(w => w.Id).ShouldBe([worded.Id, substring.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task TheListing_FlagsWisdomWhoseProvenanceEmptiedThroughHardDeletes()
    {
        await ResetWisdomAsync();
        var episode = await SeedEpisodeAsync();
        var orphan = await SeedWisdomAsync(text: "orphaned soon", lastConfirmedAt: Now);
        await SeedProvenanceAsync(orphan.Id, episodeId: episode.Id);
        var sourced = await SeedWisdomAsync(text: "still sourced", lastConfirmedAt: Now.AddHours(-1));
        await SeedProvenanceAsync(sourced.Id, episodeId: episode.Id);
        await SeedProvenanceAsync(sourced.Id, harvestedItemId: (await SeedHarvestedItemAsync()).Id);

        await IntoDb(db => db.Episodes.Where(e => e.Id == episode.Id).ExecuteDeleteAsync(Token));
        var listing = await Browser().ListAsync(new WisdomQuery(), Token);

        listing.Select(w => (w.Id, w.OrphanedProvenance))
            .ShouldBe([(orphan.Id, true), (sourced.Id, false)]);
    }

    [Fact]
    public async Task TheDetail_CarriesTheChainNewestFirst_AndTheProvenanceDrillDown()
    {
        var project = await SeedProjectAsync("detail");
        var episode = await SeedEpisodeAsync(project.Id);
        var evt = await SeedEventAsync(episode.Id, seq: 3);
        var item = await SeedHarvestedItemAsync(project.Id);
        var wisdom = await SeedWisdomAsync(scopeProjectId: project.Id, text: "current text");
        await IntoDb(db =>
        {
            db.WisdomVersions.Add(new WisdomVersion
            {
                WisdomId = wisdom.Id,
                Version = 2,
                Text = "current text",
                CreatedAt = Now,
                Cause = WisdomVersionCause.Merged,
            });
            return db.SaveChangesAsync(Token);
        });
        await SeedProvenanceAsync(wisdom.Id, episodeId: episode.Id, eventId: evt.Id);
        await SeedProvenanceAsync(wisdom.Id, harvestedItemId: item.Id);

        var detail = await Browser().GetAsync(wisdom.Id, Token);

        detail.ShouldNotBeNull();
        detail.Entry.Id.ShouldBe(wisdom.Id);
        detail.Entry.ScopeName.ShouldBe(project.DisplayName);
        detail.Entry.OrphanedProvenance.ShouldBeFalse();
        detail.Versions.Select(v => v.Version).ShouldBe([2, 1]);
        detail.Provenance.Count.ShouldBe(2);
        var fromEvent = detail.Provenance.Single(s => s.EventId == evt.Id);
        fromEvent.EpisodeId.ShouldBe(episode.Id);
        fromEvent.EpisodeProjectId.ShouldBe(project.Id);
        fromEvent.SessionId.ShouldBe(episode.SessionId);
        fromEvent.EventSeq.ShouldBe(3);
        fromEvent.EventType.ShouldBe(EventType.PostToolUse);
        var fromHarvest = detail.Provenance.Single(s => s.HarvestedItemId == item.Id);
        fromHarvest.HarvestedPath.ShouldBe(item.Path);
    }

    [Fact]
    public async Task TheDetail_OfDeletedWisdom_AnswersNothing()
    {
        (await Browser().GetAsync(Guid.NewGuid(), Token)).ShouldBeNull();
    }

    [Fact]
    public async Task Editing_AppendsAnEditedVersion_AndReEmbedsTheNewText()
    {
        var wisdom = await SeedWisdomAsync(text: "the old wording");
        _embeddings.Map("the new wording", TestVectors.WithCosine(0.42));

        await Browser().EditAsync(wisdom.Id, "  the new wording  ", Token);

        var stored = await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == wisdom.Id, Token));
        stored.Text.ShouldBe("the new wording");
        stored.Embedding.ToArray()[0].ShouldBe(0.42f, 0.0001f, "the edit re-embeds (§8.1)");
        stored.Reinforcement.ShouldBe(wisdom.Reinforcement, "an edit is not a confirmation");
        stored.LastConfirmedAt.ShouldBe(wisdom.LastConfirmedAt);
        var versions = await FromDb(db => db.WisdomVersions
            .Where(v => v.WisdomId == wisdom.Id).OrderBy(v => v.Version).ToListAsync(Token));
        versions.Select(v => (v.Version, v.Cause)).ShouldBe(
            [(1, WisdomVersionCause.Distilled), (2, WisdomVersionCause.Edited)]);
        versions[1].Text.ShouldBe("the new wording");
        versions[1].CreatedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task Editing_WithoutChangingTheText_AddsNoVersion()
    {
        var wisdom = await SeedWisdomAsync(text: "already right");

        await Browser().EditAsync(wisdom.Id, "already right ", Token);

        (await FromDb(db => db.WisdomVersions.CountAsync(v => v.WisdomId == wisdom.Id, Token)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task EditingDeletedWisdom_StaysQuiet()
    {
        await Browser().EditAsync(Guid.NewGuid(), "into the void", Token);
    }

    [Fact]
    public async Task Retiring_IsReversible_AndTimestamped()
    {
        var wisdom = await SeedWisdomAsync();
        var browser = Browser();

        await browser.RetireAsync(wisdom.Id, Token);
        (await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == wisdom.Id, Token)))
            .RetiredAt.ShouldBe(Now);

        await browser.UnretireAsync(wisdom.Id, Token);
        (await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == wisdom.Id, Token)))
            .RetiredAt.ShouldBeNull();
    }

    [Fact]
    public async Task Deleting_RemovesTheWisdomWithItsChain_ReferencedRecordsSurvive()
    {
        var episode = await SeedEpisodeAsync();
        var wisdom = await SeedWisdomAsync();
        await SeedProvenanceAsync(wisdom.Id, episodeId: episode.Id);

        await Browser().DeleteAsync(wisdom.Id, Token);

        (await FromDb(db => db.Wisdom.CountAsync(w => w.Id == wisdom.Id, Token))).ShouldBe(0);
        (await FromDb(db => db.WisdomVersions.CountAsync(v => v.WisdomId == wisdom.Id, Token))).ShouldBe(0);
        (await FromDb(db => db.Provenance.CountAsync(p => p.WisdomId == wisdom.Id, Token))).ShouldBe(0);
        (await FromDb(db => db.Episodes.CountAsync(e => e.Id == episode.Id, Token))).ShouldBe(1);
    }

    private WisdomBrowser Browser() => new(Contexts, _embeddings, _clock);

    private async Task<Wisdom> SeedWisdomAsync(
        WisdomKind kind = WisdomKind.Fact,
        Guid? scopeProjectId = null,
        string? text = null,
        DateTimeOffset? lastConfirmedAt = null,
        DateTimeOffset? contestedAt = null,
        DateTimeOffset? retiredAt = null)
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            ScopeProjectId = scopeProjectId ?? Project.GlobalId,
            Text = text ?? $"a durable fact {Guid.NewGuid():N}",
            Embedding = new Vector(TestVectors.Basis),
            Reinforcement = 1,
            LastConfirmedAt = lastConfirmedAt ?? Now,
            ContestedAt = contestedAt,
            RetiredAt = retiredAt,
        };
        await IntoDb(db =>
        {
            db.Wisdom.Add(wisdom);
            db.WisdomVersions.Add(new WisdomVersion
            {
                WisdomId = wisdom.Id,
                Version = 1,
                Text = wisdom.Text,
                CreatedAt = wisdom.LastConfirmedAt,
                Cause = WisdomVersionCause.Distilled,
            });
            return db.SaveChangesAsync(Token);
        });
        return wisdom;
    }

    private async Task<Project> SeedProjectAsync(string name)
    {
        var project = TestData.NewProject(name);
        project.DisplayName = name;
        await IntoDb(db =>
        {
            db.Projects.Add(project);
            return db.SaveChangesAsync(Token);
        });
        return project;
    }

    private async Task<Episode> SeedEpisodeAsync(Guid? projectId = null)
    {
        projectId ??= (await SeedProjectAsync("wisdom-episodes")).Id;
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{Guid.NewGuid():N}",
            ProjectId = projectId.Value,
            StartedAt = Now,
            Cwd = @"C:\git\wisdom-tests",
        };
        await IntoDb(db =>
        {
            db.Episodes.Add(episode);
            return db.SaveChangesAsync(Token);
        });
        return episode;
    }

    private async Task<Event> SeedEventAsync(Guid episodeId, int seq)
    {
        var evt = new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episodeId,
            Seq = seq,
            Type = EventType.PostToolUse,
            At = Now,
            Payload = """{"tool_name":"Bash"}""",
            PayloadFullSize = 20,
        };
        await IntoDb(db =>
        {
            db.Events.Add(evt);
            return db.SaveChangesAsync(Token);
        });
        return evt;
    }

    private async Task<HarvestedItem> SeedHarvestedItemAsync(Guid? projectId = null)
    {
        projectId ??= (await SeedProjectAsync("wisdom-harvest")).Id;
        var item = new HarvestedItem
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId.Value,
            Path = $"wisdom-{Guid.NewGuid():N}/memory/MEMORY.md",
            ContentHash = Guid.NewGuid().ToString("N"),
            Content = "a memory",
            FirstSeen = Now,
            LastChanged = Now,
        };
        await IntoDb(db =>
        {
            db.HarvestedItems.Add(item);
            return db.SaveChangesAsync(Token);
        });
        return item;
    }

    private async Task SeedProvenanceAsync(
        Guid wisdomId, Guid? episodeId = null, Guid? eventId = null, Guid? harvestedItemId = null)
        => await IntoDb(db =>
        {
            db.Provenance.Add(new Provenance
            {
                Id = Guid.CreateVersion7(),
                WisdomId = wisdomId,
                EpisodeId = episodeId,
                EventId = eventId,
                HarvestedItemId = harvestedItemId,
            });
            return db.SaveChangesAsync(Token);
        });

    private async Task ResetWisdomAsync()
        => await IntoDb(db => db.ResetWisdomAsync(Token));

    private async Task IntoDb(Func<MimirDbContext, Task> mutate)
    {
        await using var db = CreateContext();
        await mutate(db);
    }

    private async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var db = CreateContext();
        return await query(db);
    }

    private MimirDbContext CreateContext() => Contexts.CreateDbContext();

    private static CancellationToken Token => TestContext.Current.CancellationToken;
}
