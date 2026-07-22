using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The full Merge Gate (§6) against a real Postgres: no match inserts new Wisdom at
/// reinforcement 1 / version 1 with Provenance; a cosine at or above 0.80 goes to the arbiter,
/// whose ruling merges the rewrite, supersedes, or scope-splits. Thresholds read the vector
/// leg's cosine, never the fused score (§3).
/// </summary>
public sealed class MergeGateTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeEmbeddings _embeddings = new();

    private readonly FakeArbiter _arbiter = new();

    private readonly FakeTimeProvider _clock = new(Now);

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
    public async Task NoMatch_InsertsNewWisdom_AtReinforcementOneVersionOne()
    {
        await Context.ResetWisdomAsync(Token);
        var item = await AddHarvestedItemAsync();
        var text = $"Prefers tabs over spaces {Guid.NewGuid():N}";

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Preference, item.ProjectId, text, HarvestedItemId: item.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == item.ProjectId, Token));
        wisdom.Kind.ShouldBe(WisdomKind.Preference);
        wisdom.Text.ShouldBe(text);
        wisdom.Reinforcement.ShouldBe(1);
        wisdom.LastConfirmedAt.ShouldBe(Now);
        wisdom.RetiredAt.ShouldBeNull();

        var version = await FromDb(db => db.WisdomVersions.SingleAsync(v => v.WisdomId == wisdom.Id, Token));
        version.Version.ShouldBe(1);
        version.Text.ShouldBe(text);
        version.Cause.ShouldBe(WisdomVersionCause.Distilled);

        var provenance = await FromDb(db => db.Provenance.SingleAsync(p => p.WisdomId == wisdom.Id, Token));
        provenance.HarvestedItemId.ShouldBe(item.Id);
        provenance.EpisodeId.ShouldBeNull();
        provenance.EventId.ShouldBeNull();
    }

    [Fact]
    public async Task ANearDuplicate_Reinforces_KeepingTheExistingText()
    {
        await Context.ResetWisdomAsync(Token);
        var first = await AddHarvestedItemAsync();
        var second = await AddHarvestedItemAsync(first.ProjectId);
        var originalText = $"Original wording {Guid.NewGuid():N}";
        var nearDuplicate = $"Equivalent wording {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(nearDuplicate, TestVectors.WithCosine(0.85));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, first.ProjectId, originalText, HarvestedItemId: first.Id));
        _clock.Advance(TimeSpan.FromHours(1));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, second.ProjectId, nearDuplicate, HarvestedItemId: second.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == first.ProjectId, Token));
        wisdom.Text.ShouldBe(originalText, "an agreement whose rewrite keeps the wording changes nothing");
        wisdom.Reinforcement.ShouldBe(2);
        wisdom.LastConfirmedAt.ShouldBe(Now.AddHours(1));

        (await FromDb(db => db.WisdomVersions.CountAsync(v => v.WisdomId == wisdom.Id, Token)))
            .ShouldBe(1, "unchanged text means no new version");
        var provenance = await FromDb(db => db.Provenance
            .Where(p => p.WisdomId == wisdom.Id)
            .Select(p => p.HarvestedItemId)
            .ToListAsync(Token));
        provenance.ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task JustBelowTheThreshold_InsertsASecondWisdom()
    {
        await Context.ResetWisdomAsync(Token);
        var item = await AddHarvestedItemAsync();
        var originalText = $"First fact {Guid.NewGuid():N}";
        var similarText = $"Nearly related fact {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(similarText, TestVectors.WithCosine(0.79));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, originalText, HarvestedItemId: item.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, similarText, HarvestedItemId: item.Id));

        var texts = await FromDb(db => db.Wisdom
            .Where(w => w.ScopeProjectId == item.ProjectId)
            .Select(w => w.Text)
            .ToListAsync(Token));
        texts.ShouldBe([originalText, similarText], ignoreOrder: true);
        _arbiter.Calls.ShouldBeEmpty("below the threshold there is no match to rule on");
    }

    [Fact]
    public async Task AWordForWordFtsMatch_WithADistantEmbedding_DoesNotReinforce()
    {
        await Context.ResetWisdomAsync(Token);
        // The §3 score-scale rule at the gate: identical wording makes the FTS leg rank the pair
        // as hard as it can, but the threshold reads cosine — a distant embedding means no match.
        var item = await AddHarvestedItemAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var originalText = $"the deploy pipeline needs manual approval {suffix}";
        var sameWords = $"needs the manual deploy approval pipeline {suffix}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(sameWords, TestVectors.WithCosine(0.0));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, originalText, HarvestedItemId: item.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, sameWords, HarvestedItemId: item.Id));

        (await FromDb(db => db.Wisdom.CountAsync(w => w.ScopeProjectId == item.ProjectId, Token)))
            .ShouldBe(2);
    }

    [Fact]
    public async Task ReinforcingFromTheSameHarvestedItem_DoesNotDuplicateProvenance()
    {
        await Context.ResetWisdomAsync(Token);
        var item = await AddHarvestedItemAsync();
        var originalText = $"One fact {Guid.NewGuid():N}";
        var nearDuplicate = $"Same fact again {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(nearDuplicate, TestVectors.WithCosine(0.9));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, originalText, HarvestedItemId: item.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, nearDuplicate, HarvestedItemId: item.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == item.ProjectId, Token));
        wisdom.Reinforcement.ShouldBe(2);
        (await FromDb(db => db.Provenance.CountAsync(p => p.WisdomId == wisdom.Id, Token)))
            .ShouldBe(1, "Provenance is unioned (§6): the same link is recorded once");
    }

    [Fact]
    public async Task ADistillerShapedCandidate_RecordsOneProvenanceRowPerEvent_Unioned()
    {
        await Context.ResetWisdomAsync(Token);
        // The §6 Distiller output shape: a candidate carries its Episode and plural provenance
        // event ids. Each Event gets its own row; a reinforcing admission unions, not appends.
        var (projectId, episodeId, eventIds) = await AddEpisodeWithEventsAsync(3);
        var originalText = $"Sessions produce wisdom {Guid.NewGuid():N}";
        var nearDuplicate = $"Wisdom comes from sessions {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(nearDuplicate, TestVectors.WithCosine(0.9));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, projectId, originalText,
            EpisodeId: episodeId, EventIds: [eventIds[0], eventIds[1]]));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, projectId, nearDuplicate,
            EpisodeId: episodeId, EventIds: [eventIds[1], eventIds[2]]));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == projectId, Token));
        wisdom.Reinforcement.ShouldBe(2);
        var provenance = await FromDb(db => db.Provenance
            .Where(p => p.WisdomId == wisdom.Id)
            .ToListAsync(Token));
        provenance.Select(p => p.EventId).ShouldBe(
            [eventIds[0], eventIds[1], eventIds[2]], ignoreOrder: true);
        provenance.ShouldAllBe(p => p.EpisodeId == episodeId);
    }

    [Fact]
    public async Task AnAgreementRewrite_BecomesTheCurrentText_WithTheChainIntact()
    {
        await Context.ResetWisdomAsync(Token);
        var first = await AddHarvestedItemAsync();
        var second = await AddHarvestedItemAsync(first.ProjectId);
        var originalText = $"Deploys need approval {Guid.NewGuid():N}";
        var confirmingText = $"Approval gates deploys {Guid.NewGuid():N}";
        var mergedText = $"Every deploy needs a manual approval gate {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(confirmingText, TestVectors.WithCosine(0.9));
        _embeddings.Map(mergedText, TestVectors.WithCosine(0.97));
        _arbiter.Enqueue(new MergeRuling.Agreement(mergedText));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, first.ProjectId, originalText, HarvestedItemId: first.Id));
        _clock.Advance(TimeSpan.FromHours(1));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, second.ProjectId, confirmingText, HarvestedItemId: second.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == first.ProjectId, Token));
        wisdom.Text.ShouldBe(mergedText);
        wisdom.Reinforcement.ShouldBe(2);
        wisdom.LastConfirmedAt.ShouldBe(Now.AddHours(1));
        wisdom.Embedding.ToArray()[0].ShouldBe(0.97f, 0.0001f, "the rewrite re-embeds");

        var versions = await FromDb(db => db.WisdomVersions
            .Where(v => v.WisdomId == wisdom.Id)
            .OrderBy(v => v.Version)
            .ToListAsync(Token));
        versions.Count.ShouldBe(2);
        versions[0].Text.ShouldBe(originalText, "the prior text stays in the chain");
        versions[0].Cause.ShouldBe(WisdomVersionCause.Distilled);
        versions[1].Text.ShouldBe(mergedText);
        versions[1].Cause.ShouldBe(WisdomVersionCause.Merged);
    }

    [Fact]
    public async Task AnAgreementFromAnotherProject_PromotesTheWisdomToGlobal()
    {
        await Context.ResetWisdomAsync(Token);
        var first = await AddHarvestedItemAsync();
        var elsewhere = await AddHarvestedItemAsync();
        var originalText = $"Pin the SDK version {Guid.NewGuid():N}";
        var confirmingText = $"SDK versions get pinned {Guid.NewGuid():N}";
        var mergedText = $"Always pin the SDK version {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(confirmingText, TestVectors.WithCosine(0.9));
        _arbiter.Enqueue(new MergeRuling.Agreement(mergedText));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, first.ProjectId, originalText, HarvestedItemId: first.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, elsewhere.ProjectId, confirmingText, HarvestedItemId: elsewhere.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == mergedText, Token));
        wisdom.ScopeProjectId.ShouldBe(
            Project.GlobalId, "a Project-scoped Wisdom confirmed from a different Project goes Global (§6)");
        wisdom.Reinforcement.ShouldBe(2);
    }

    [Fact]
    public async Task AnAgreementProposedAsGlobal_IsNotCrossProjectConfirmation_AndDoesNotPromote()
    {
        await Context.ResetWisdomAsync(Token);
        // §6.3 promotes on confirmation from a *different Project*. A Global-scoped candidate
        // carries no origin Project, so it cannot vouch for recurrence elsewhere.
        var item = await AddHarvestedItemAsync();
        var originalText = $"Tests need the daemon up {Guid.NewGuid():N}";
        var confirmingText = $"The daemon must run for tests {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(confirmingText, TestVectors.WithCosine(0.9));
        _arbiter.Enqueue(new MergeRuling.Agreement(originalText));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Procedure, item.ProjectId, originalText, HarvestedItemId: item.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Procedure, Project.GlobalId, confirmingText, HarvestedItemId: item.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == originalText, Token));
        wisdom.ScopeProjectId.ShouldBe(item.ProjectId);
        wisdom.Reinforcement.ShouldBe(2);
    }

    [Fact]
    public async Task ASupersedeRuling_RetiresTheOldWisdom_AndInsertsTheCandidate()
    {
        await Context.ResetWisdomAsync(Token);
        var first = await AddHarvestedItemAsync();
        var second = await AddHarvestedItemAsync(first.ProjectId);
        var oldText = $"The service listens on 6464 {Guid.NewGuid():N}";
        var newText = $"The service moved to 7575 {Guid.NewGuid():N}";
        _embeddings.Map(oldText, TestVectors.Basis);
        _embeddings.Map(newText, TestVectors.WithCosine(0.9));
        _arbiter.Enqueue(new MergeRuling.Supersede());

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, first.ProjectId, oldText, HarvestedItemId: first.Id));
        _clock.Advance(TimeSpan.FromHours(1));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, second.ProjectId, newText, HarvestedItemId: second.Id));

        var old = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == oldText, Token));
        var successor = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == newText, Token));
        old.RetiredAt.ShouldBe(Now.AddHours(1), "superseded Wisdom is Retired automatically (§6)");
        old.SupersededBy.ShouldBe(successor.Id);
        old.ContestedAt.ShouldBeNull("the retired loser is out of recall; the survivor is the contested one");
        successor.Reinforcement.ShouldBe(1);
        successor.ContestedAt.ShouldBe(Now.AddHours(1));
        successor.RetiredAt.ShouldBeNull();

        var version = await FromDb(db => db.WisdomVersions.SingleAsync(v => v.WisdomId == successor.Id, Token));
        version.Version.ShouldBe(1);
        version.Cause.ShouldBe(WisdomVersionCause.Adjudicated);
        (await FromDb(db => db.Provenance.SingleAsync(p => p.WisdomId == successor.Id, Token)))
            .HarvestedItemId.ShouldBe(second.Id);
    }

    [Fact]
    public async Task AScopeSplit_OnProjectScopedWisdom_AddsAGlobalSibling()
    {
        await Context.ResetWisdomAsync(Token);
        var first = await AddHarvestedItemAsync();
        var second = await AddHarvestedItemAsync(first.ProjectId);
        var originalText = $"Builds run on Windows {Guid.NewGuid():N}";
        var disputingText = $"Builds run on Linux {Guid.NewGuid():N}";
        var globalText = $"Builds run on Linux by default {Guid.NewGuid():N}";
        var projectText = $"This repo builds on Windows {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(disputingText, TestVectors.WithCosine(0.9));
        _arbiter.Enqueue(new MergeRuling.ScopeSplit(globalText, projectText));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, first.ProjectId, originalText, HarvestedItemId: first.Id));
        _clock.Advance(TimeSpan.FromHours(1));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, second.ProjectId, disputingText, HarvestedItemId: second.Id));

        var kept = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == first.ProjectId, Token));
        kept.Text.ShouldBe(projectText, "the project-scoped row keeps its Project side of the split");
        kept.ContestedAt.ShouldBe(Now.AddHours(1));
        kept.RetiredAt.ShouldBeNull();
        kept.Reinforcement.ShouldBe(1, "a contradiction is not a confirmation");

        var keptVersions = await FromDb(db => db.WisdomVersions
            .Where(v => v.WisdomId == kept.Id).OrderBy(v => v.Version).ToListAsync(Token));
        keptVersions.Select(v => v.Cause).ShouldBe(
            [WisdomVersionCause.Distilled, WisdomVersionCause.Adjudicated]);
        keptVersions[1].Text.ShouldBe(projectText);

        var sibling = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == globalText, Token));
        sibling.ScopeProjectId.ShouldBe(Project.GlobalId);
        sibling.ContestedAt.ShouldBe(Now.AddHours(1));
        sibling.Reinforcement.ShouldBe(1);
        (await FromDb(db => db.WisdomVersions.SingleAsync(v => v.WisdomId == sibling.Id, Token)))
            .Cause.ShouldBe(WisdomVersionCause.Adjudicated);

        // Both rows descend from both sources, so both carry the full provenance union.
        foreach (var wisdomId in new[] { kept.Id, sibling.Id })
        {
            var items = await FromDb(db => db.Provenance
                .Where(p => p.WisdomId == wisdomId).Select(p => p.HarvestedItemId).ToListAsync(Token));
            items.ShouldBe([first.Id, second.Id], ignoreOrder: true);
        }
    }

    [Fact]
    public async Task AScopeSplit_OnGlobalWisdom_AddsAProjectScopedSibling()
    {
        await Context.ResetWisdomAsync(Token);
        var first = await AddHarvestedItemAsync();
        var second = await AddHarvestedItemAsync();
        var originalText = $"Use conventional commits {Guid.NewGuid():N}";
        var disputingText = $"Commits here are freeform {Guid.NewGuid():N}";
        var globalText = $"Use conventional commits by default {Guid.NewGuid():N}";
        var projectText = $"This repo takes freeform commits {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(disputingText, TestVectors.WithCosine(0.9));
        _arbiter.Enqueue(new MergeRuling.ScopeSplit(globalText, projectText));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Preference, Project.GlobalId, originalText, HarvestedItemId: first.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Preference, second.ProjectId, disputingText, HarvestedItemId: second.Id));

        var kept = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == Project.GlobalId, Token));
        kept.Text.ShouldBe(globalText, "the Global row keeps the Global side of the split");
        var sibling = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == projectText, Token));
        sibling.ScopeProjectId.ShouldBe(second.ProjectId, "the sibling lands in the disputing candidate's Project");
    }

    [Fact]
    public async Task AScopeSplit_WithNoProjectInPlay_DegradesToSupersede()
    {
        await Context.ResetWisdomAsync(Token);
        // Two Global positions cannot split into "one Global and one Project-scoped" (§6.4) —
        // there is no Project to scope to — so the adjudication falls back to Supersede.
        var first = await AddHarvestedItemAsync();
        var second = await AddHarvestedItemAsync();
        var oldText = $"Global stance {Guid.NewGuid():N}";
        var newText = $"Contrary global stance {Guid.NewGuid():N}";
        _embeddings.Map(oldText, TestVectors.Basis);
        _embeddings.Map(newText, TestVectors.WithCosine(0.9));
        _arbiter.Enqueue(new MergeRuling.ScopeSplit("a global side", "a project side"));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Preference, Project.GlobalId, oldText, HarvestedItemId: first.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Preference, Project.GlobalId, newText, HarvestedItemId: second.Id));

        var old = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == oldText, Token));
        var successor = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == newText, Token));
        old.RetiredAt.ShouldNotBeNull();
        old.SupersededBy.ShouldBe(successor.Id);
        successor.ContestedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task AnArbiterFailure_Propagates_LeavingTheMatchUntouched()
    {
        await Context.ResetWisdomAsync(Token);
        // No silent mechanical fallback: a failed ruling must fail the admission, so the §5
        // conversion marker stays pending and the item retries once the model is back.
        var item = await AddHarvestedItemAsync();
        var originalText = $"A settled lesson {Guid.NewGuid():N}";
        var matchingText = $"A disputed take {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(matchingText, TestVectors.WithCosine(0.9));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, item.ProjectId, originalText, HarvestedItemId: item.Id));
        _arbiter.Failure = new MergeArbiterException("the model returned nothing usable");

        await Should.ThrowAsync<MergeArbiterException>(async () => await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, item.ProjectId, matchingText, HarvestedItemId: item.Id)));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == item.ProjectId, Token));
        wisdom.Text.ShouldBe(originalText);
        wisdom.Reinforcement.ShouldBe(1);
    }

    /// <summary>The gate saves per admission itself, so the helper only builds and calls it.</summary>
    private async Task AdmitAsync(WisdomCandidate candidate)
    {
        var gate = new MergeGate(
            Context,
            _embeddings,
            new WisdomSearch(Context, Options.Create(new SearchOptions())),
            _arbiter,
            Options.Create(new DistillationOptions()),
            _clock);
        await gate.AdmitAsync(candidate, Token);
    }

    /// <summary>A fresh Project with one Episode carrying <paramref name="eventCount"/> Events.</summary>
    private async Task<(Guid ProjectId, Guid EpisodeId, IReadOnlyList<Guid> EventIds)> AddEpisodeWithEventsAsync(
        int eventCount)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var project = TestData.NewProject("gate");
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{suffix}",
            ProjectId = project.Id,
            StartedAt = Now,
            Cwd = $@"C:\git\gate-{suffix}",
        };
        var events = Enumerable.Range(1, eventCount).Select(seq => new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episode.Id,
            Seq = seq,
            Type = EventType.UserPromptSubmit,
            At = Now,
            Payload = """{"prompt":"remember this"}""",
            PayloadFullSize = 28,
        }).ToList();
        Context.AddRange(project, episode);
        Context.AddRange(events);
        await Context.SaveChangesAsync(Token);
        return (project.Id, episode.Id, events.Select(e => e.Id).ToList());
    }

    /// <summary>An item on its own fresh Project, so per-scope assertions see only this test.</summary>
    private async Task<HarvestedItem> AddHarvestedItemAsync(Guid? projectId = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        if (projectId is null)
        {
            var project = TestData.NewProject("gate");
            Context.Projects.Add(project);
            projectId = project.Id;
        }

        var item = new HarvestedItem
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId.Value,
            Path = $"slug-{suffix}/memory/MEMORY.md",
            ContentHash = suffix,
            Content = "unused by the gate",
            FirstSeen = Now,
            LastChanged = Now,
        };
        Context.HarvestedItems.Add(item);
        await Context.SaveChangesAsync(Token);
        return item;
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
