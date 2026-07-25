using Microsoft.EntityFrameworkCore;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The full Merge Gate (§6) against a real Postgres: no match inserts new Wisdom at
/// reinforcement 1 / version 1 with Provenance; a cosine at or above 0.80 goes to the arbiter,
/// whose ruling merges the rewrite, supersedes, or scope-splits. Thresholds read the vector
/// leg's cosine, never the fused score (§3).
/// </summary>
public sealed class MergeGateTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task NoMatch_InsertsNewWisdom_AtReinforcementOneVersionOne()
    {
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);
        const string text = "Prefers tabs over spaces";

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Preference, project.Id, text, HarvestedItemId: item.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.ScopeProjectId.ShouldBe(project.Id);
        wisdom.Kind.ShouldBe(WisdomKind.Preference);
        wisdom.Text.ShouldBe(text);
        wisdom.Reinforcement.ShouldBe(1);
        wisdom.LastConfirmedAt.ShouldBe(Now);
        wisdom.RetiredAt.ShouldBeNull();

        var version = await FromDb(db => db.WisdomVersions.SingleAsync(Token));
        version.WisdomId.ShouldBe(wisdom.Id);
        version.Version.ShouldBe(1);
        version.Text.ShouldBe(text);
        version.Cause.ShouldBe(WisdomVersionCause.Distilled);

        var provenance = await FromDb(db => db.Provenance.SingleAsync(Token));
        provenance.WisdomId.ShouldBe(wisdom.Id);
        provenance.HarvestedItemId.ShouldBe(item.Id);
        provenance.EpisodeId.ShouldBeNull();
        provenance.EventId.ShouldBeNull();
    }

    [Fact]
    public async Task ANearDuplicate_Reinforces_KeepingTheExistingText()
    {
        var project = await AddProjectAsync();
        var first = await AddHarvestedItemAsync(project.Id);
        var second = await AddHarvestedItemAsync(project.Id);
        const string originalText = "Original wording";
        const string nearDuplicate = "Equivalent wording";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(nearDuplicate, TestVectors.WithCosine(0.85));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, originalText, HarvestedItemId: first.Id));
        Clock.Advance(TimeSpan.FromHours(1));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, nearDuplicate, HarvestedItemId: second.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(originalText, "an agreement whose rewrite keeps the wording changes nothing");
        wisdom.Reinforcement.ShouldBe(2);
        wisdom.LastConfirmedAt.ShouldBe(Now.AddHours(1));

        (await FromDb(db => db.WisdomVersions.CountAsync(Token)))
            .ShouldBe(1, "unchanged text means no new version");
        var provenance = await FromDb(db => db.Provenance
            .Select(p => p.HarvestedItemId)
            .ToListAsync(Token));
        provenance.ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task JustBelowTheThreshold_InsertsASecondWisdom()
    {
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);
        const string originalText = "First fact";
        const string similarText = "Nearly related fact";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(similarText, TestVectors.WithCosine(0.79));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, originalText, HarvestedItemId: item.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, similarText, HarvestedItemId: item.Id));

        var texts = await FromDb(db => db.Wisdom.Select(w => w.Text).ToListAsync(Token));
        texts.ShouldBe([originalText, similarText], ignoreOrder: true);
        Arbiter.Calls.ShouldBeEmpty("below the threshold there is no match to rule on");
    }

    [Fact]
    public async Task AWordForWordFtsMatch_WithADistantEmbedding_DoesNotReinforce()
    {
        // The §3 score-scale rule at the gate: identical wording makes the FTS leg rank the pair
        // as hard as it can, but the threshold reads cosine — a distant embedding means no match.
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);
        const string originalText = "the deploy pipeline needs manual approval";
        const string sameWords = "needs the manual deploy approval pipeline";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(sameWords, TestVectors.WithCosine(0.0));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, originalText, HarvestedItemId: item.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, sameWords, HarvestedItemId: item.Id));

        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(2);
    }

    [Fact]
    public async Task ReinforcingFromTheSameHarvestedItem_DoesNotDuplicateProvenance()
    {
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);
        const string originalText = "One fact";
        const string nearDuplicate = "Same fact again";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(nearDuplicate, TestVectors.WithCosine(0.9));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, originalText, HarvestedItemId: item.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, nearDuplicate, HarvestedItemId: item.Id));

        (await FromDb(db => db.Wisdom.SingleAsync(Token))).Reinforcement.ShouldBe(2);
        (await FromDb(db => db.Provenance.CountAsync(Token)))
            .ShouldBe(1, "Provenance is unioned (§6): the same link is recorded once");
    }

    [Fact]
    public async Task ADistillerShapedCandidate_RecordsOneProvenanceRowPerEvent_Unioned()
    {
        // The §6 Distiller output shape: a candidate carries its Episode and plural provenance
        // event ids. Each Event gets its own row; a reinforcing admission unions, not appends.
        var project = await AddProjectAsync();
        var episode = await AddEpisodeAsync(project.Id);
        var first = await AddEventAsync(episode.Id, seq: 1);
        var second = await AddEventAsync(episode.Id, seq: 2);
        var third = await AddEventAsync(episode.Id, seq: 3);
        const string originalText = "Sessions produce wisdom";
        const string nearDuplicate = "Wisdom comes from sessions";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(nearDuplicate, TestVectors.WithCosine(0.9));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, project.Id, originalText,
            EpisodeId: episode.Id, EventIds: [first.Id, second.Id]));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, project.Id, nearDuplicate,
            EpisodeId: episode.Id, EventIds: [second.Id, third.Id]));

        (await FromDb(db => db.Wisdom.SingleAsync(Token))).Reinforcement.ShouldBe(2);
        var provenance = await FromDb(db => db.Provenance.ToListAsync(Token));
        provenance.Select(p => p.EventId).ShouldBe(
            [first.Id, second.Id, third.Id], ignoreOrder: true);
        provenance.ShouldAllBe(p => p.EpisodeId == episode.Id);
    }

    [Fact]
    public async Task AnAgreementRewrite_BecomesTheCurrentText_WithTheChainIntact()
    {
        var project = await AddProjectAsync();
        var first = await AddHarvestedItemAsync(project.Id);
        var second = await AddHarvestedItemAsync(project.Id);
        const string originalText = "Deploys need approval";
        const string confirmingText = "Approval gates deploys";
        const string mergedText = "Every deploy needs a manual approval gate";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(confirmingText, TestVectors.WithCosine(0.9));
        Embeddings.Map(mergedText, TestVectors.WithCosine(0.97));
        Arbiter.Enqueue(new MergeRuling.Agreement(mergedText));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, originalText, HarvestedItemId: first.Id));
        Clock.Advance(TimeSpan.FromHours(1));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, confirmingText, HarvestedItemId: second.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(mergedText);
        wisdom.Reinforcement.ShouldBe(2);
        wisdom.LastConfirmedAt.ShouldBe(Now.AddHours(1));
        wisdom.Embedding.ToArray()[0].ShouldBe(0.97f, 0.0001f, "the rewrite re-embeds");

        var versions = await FromDb(db => db.WisdomVersions
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
        var project = await AddProjectAsync();
        var elsewhere = await AddProjectAsync();
        var first = await AddHarvestedItemAsync(project.Id);
        var second = await AddHarvestedItemAsync(elsewhere.Id);
        const string originalText = "Pin the SDK version";
        const string confirmingText = "SDK versions get pinned";
        const string mergedText = "Always pin the SDK version";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(confirmingText, TestVectors.WithCosine(0.9));
        Arbiter.Enqueue(new MergeRuling.Agreement(mergedText));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, project.Id, originalText, HarvestedItemId: first.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, elsewhere.Id, confirmingText, HarvestedItemId: second.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == mergedText, Token));
        wisdom.ScopeProjectId.ShouldBe(
            Project.GlobalId, "a Project-scoped Wisdom confirmed from a different Project goes Global (§6)");
        wisdom.Reinforcement.ShouldBe(2);
    }

    [Fact]
    public async Task AnAgreementProposedAsGlobal_IsNotCrossProjectConfirmation_AndDoesNotPromote()
    {
        // §6.3 promotes on confirmation from a *different Project*. A Global-scoped candidate
        // carries no origin Project, so it cannot vouch for recurrence elsewhere.
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);
        const string originalText = "Tests need the daemon up";
        const string confirmingText = "The daemon must run for tests";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(confirmingText, TestVectors.WithCosine(0.9));
        Arbiter.Enqueue(new MergeRuling.Agreement(originalText));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Procedure, project.Id, originalText, HarvestedItemId: item.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Procedure, Project.GlobalId, confirmingText, HarvestedItemId: item.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(originalText);
        wisdom.ScopeProjectId.ShouldBe(project.Id);
        wisdom.Reinforcement.ShouldBe(2);
    }

    [Fact]
    public async Task ASupersedeRuling_RetiresTheOldWisdom_AndInsertsTheCandidate()
    {
        var project = await AddProjectAsync();
        var first = await AddHarvestedItemAsync(project.Id);
        var second = await AddHarvestedItemAsync(project.Id);
        const string oldText = "The service listens on 6464";
        const string newText = "The service moved to 7575";
        Embeddings.Map(oldText, TestVectors.Basis);
        Embeddings.Map(newText, TestVectors.WithCosine(0.9));
        Arbiter.Enqueue(new MergeRuling.Supersede());

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, oldText, HarvestedItemId: first.Id));
        Clock.Advance(TimeSpan.FromHours(1));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, newText, HarvestedItemId: second.Id));

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
        var project = await AddProjectAsync();
        var first = await AddHarvestedItemAsync(project.Id);
        var second = await AddHarvestedItemAsync(project.Id);
        const string originalText = "Builds run on Windows";
        const string disputingText = "Builds run on Linux";
        const string globalText = "Builds run on Linux by default";
        const string projectText = "This repo builds on Windows";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(disputingText, TestVectors.WithCosine(0.9));
        Arbiter.Enqueue(new MergeRuling.ScopeSplit(globalText, projectText));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, originalText, HarvestedItemId: first.Id));
        Clock.Advance(TimeSpan.FromHours(1));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, disputingText, HarvestedItemId: second.Id));

        var kept = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == project.Id, Token));
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
        var project = await AddProjectAsync();
        var elsewhere = await AddProjectAsync();
        var first = await AddHarvestedItemAsync(project.Id);
        var second = await AddHarvestedItemAsync(elsewhere.Id);
        const string originalText = "Use conventional commits";
        const string disputingText = "Commits here are freeform";
        const string globalText = "Use conventional commits by default";
        const string projectText = "This repo takes freeform commits";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(disputingText, TestVectors.WithCosine(0.9));
        Arbiter.Enqueue(new MergeRuling.ScopeSplit(globalText, projectText));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Preference, Project.GlobalId, originalText, HarvestedItemId: first.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Preference, elsewhere.Id, disputingText, HarvestedItemId: second.Id));

        var kept = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == Project.GlobalId, Token));
        kept.Text.ShouldBe(globalText, "the Global row keeps the Global side of the split");
        var sibling = await FromDb(db => db.Wisdom.SingleAsync(w => w.Text == projectText, Token));
        sibling.ScopeProjectId.ShouldBe(elsewhere.Id, "the sibling lands in the disputing candidate's Project");
    }

    [Fact]
    public async Task AScopeSplit_WithNoProjectInPlay_DegradesToSupersede()
    {
        // Two Global positions cannot split into "one Global and one Project-scoped" (§6.4) —
        // there is no Project to scope to — so the adjudication falls back to Supersede.
        var project = await AddProjectAsync();
        var first = await AddHarvestedItemAsync(project.Id);
        var second = await AddHarvestedItemAsync(project.Id);
        const string oldText = "Global stance";
        const string newText = "Contrary global stance";
        Embeddings.Map(oldText, TestVectors.Basis);
        Embeddings.Map(newText, TestVectors.WithCosine(0.9));
        Arbiter.Enqueue(new MergeRuling.ScopeSplit("a global side", "a project side"));

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
        // No silent mechanical fallback: a failed ruling must fail the admission, so the §5
        // conversion marker stays pending and the item retries once the model is back.
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);
        const string originalText = "A settled lesson";
        const string matchingText = "A disputed take";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(matchingText, TestVectors.WithCosine(0.9));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, project.Id, originalText, HarvestedItemId: item.Id));
        Arbiter.Failure = new MergeArbiterException("the model returned nothing usable");

        await Should.ThrowAsync<MergeArbiterException>(async () => await AdmitAsync(new WisdomCandidate(
            WisdomKind.Lesson, project.Id, matchingText, HarvestedItemId: item.Id)));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(originalText);
        wisdom.Reinforcement.ShouldBe(1);
    }

    [Fact]
    public async Task AnAdmissionBatch_CommitsTheMarkerAndTheWisdomTogether_EmbeddingOnce()
    {
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);
        const string firstText = "Batches are atomic";
        const string secondText = "The gate owns the transaction";

        await CreateMergeGate().AdmitAllAsync(
            [
                new WisdomCandidate(WisdomKind.Fact, project.Id, firstText, HarvestedItemId: item.Id),
                new WisdomCandidate(WisdomKind.Fact, project.Id, secondText, HarvestedItemId: item.Id),
            ],
            MarkConverted(item.Id),
            Token);

        Embeddings.Batches.ShouldBe(1, "the gate batches the whole Admission's embeddings in one round-trip");
        var texts = await FromDb(db => db.Wisdom.Select(w => w.Text).ToListAsync(Token));
        texts.ShouldBe([firstText, secondText], ignoreOrder: true);
        (await FromDb(db => db.HarvestedItems.SingleAsync(Token)))
            .ConvertedAt.ShouldBe(Now, "the finalizer's staged marker commits with the admissions");
    }

    [Fact]
    public async Task AFailingAdmission_RollsBackTheWholeBatch_LeavingTheMarkerUnset()
    {
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);
        const string firstText = "A settled fact";
        const string matchingText = "The same fact restated";
        Embeddings.Map(firstText, TestVectors.Basis);
        Embeddings.Map(matchingText, TestVectors.WithCosine(0.9));
        Arbiter.Failure = new MergeArbiterException("the model returned nothing usable");

        // The first candidate admits cleanly; the second matches it and the arbiter throws —
        // so the rollback must take back an already-saved admission, not just the failed one.
        await Should.ThrowAsync<MergeArbiterException>(async () => await CreateMergeGate().AdmitAllAsync(
            [
                new WisdomCandidate(WisdomKind.Fact, project.Id, firstText, HarvestedItemId: item.Id),
                new WisdomCandidate(WisdomKind.Fact, project.Id, matchingText, HarvestedItemId: item.Id),
            ],
            MarkConverted(item.Id),
            Token));

        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.WisdomVersions.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.Provenance.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.HarvestedItems.SingleAsync(Token)))
            .ConvertedAt.ShouldBeNull("a failed batch leaves the marker unset for the caller's retry");
    }

    [Fact]
    public async Task AFinalizerFailure_RollsBackTheWrittenMarker_WithTheAdmissions()
    {
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);
        const string text = "A fact the marker must not outlive";

        // The finalizer writes the marker to the database inside the transaction and then
        // fails — so the rollback has a genuinely written marker to take back, not one that
        // was never staged.
        await Should.ThrowAsync<InvalidOperationException>(async () => await CreateMergeGate().AdmitAllAsync(
            [new WisdomCandidate(WisdomKind.Fact, project.Id, text, HarvestedItemId: item.Id)],
            async (batch, ct) =>
            {
                await MarkConverted(item.Id)(batch, ct);
                throw new InvalidOperationException("the finalizer failed after writing the marker");
            },
            Token));

        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.HarvestedItems.SingleAsync(Token)))
            .ConvertedAt.ShouldBeNull("the written marker rolls back with the admissions");
    }

    [Fact]
    public async Task APoisonedRewriteEmbedding_FailsTheBatch_LeavingTheCallersOwnWorkIntact()
    {
        var project = await AddProjectAsync();
        var first = await AddHarvestedItemAsync(project.Id);
        var second = await AddHarvestedItemAsync(project.Id);
        const string originalText = "A settled position";
        const string matchingText = "The position restated";
        const string mergedText = "The unembeddable rewrite";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(matchingText, TestVectors.WithCosine(0.9));
        Embeddings.Poison(mergedText);
        Arbiter.Enqueue(new MergeRuling.Agreement(mergedText));

        // Work of the caller's own, staged and not yet saved when the batch fails. On a gate
        // that borrowed this context, the failure's ChangeTracker.Clear() detached this row as
        // collateral and the save below silently lost it.
        var staged = new HarvestedItem
        {
            Id = Guid.CreateVersion7(),
            ProjectId = project.Id,
            Path = "C--git-staged/memory/MEMORY.md",
            ContentHash = "staged",
            Content = "unused by the gate",
            FirstSeen = Now,
            LastChanged = Now,
        };
        Context.HarvestedItems.Add(staged);

        await Should.ThrowAsync<InvalidOperationException>(async () => await CreateMergeGate().AdmitAllAsync(
            [
                new WisdomCandidate(WisdomKind.Fact, project.Id, originalText, HarvestedItemId: first.Id),
                new WisdomCandidate(WisdomKind.Fact, project.Id, matchingText, HarvestedItemId: second.Id),
            ],
            finalizer: null,
            Token));

        Context.Entry(staged).State.ShouldBe(
            EntityState.Added, "a failed batch has no business touching the caller's change tracker");
        await Context.SaveChangesAsync(Token);
        (await FromDb(db => db.HarvestedItems.CountAsync(i => i.Id == staged.Id, Token)))
            .ShouldBe(1, "the caller's own staged row still saves after the batch failed");

        // The failure struck mid-merge, with staged-but-unsaved rows on the batch's context.
        // Disposing it is the rollback: nothing of the batch survives to be re-inserted.
        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.WisdomVersions.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.Provenance.CountAsync(Token))).ShouldBe(0);
    }

    [Fact]
    public async Task AnEmptyBatch_CommitsItsFinalizer_WithoutQueueingBehindTheGateLock()
    {
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);

        // An empty or frontmatter-only file still reaches the gate, marker and all, with nothing
        // to admit — and nothing to admit is nothing to serialize. Another batch holds the lock
        // throughout: if the empty one queued for it, a Backfill's worth of sparse files would
        // each cycle the gate-wide lock, contending with real batches for zero Wisdom rows.
        await using var holder = CreateContext();
        await using var held = await holder.Database.BeginTransactionAsync(Token);
        await holder.Database.ExecuteSqlAsync(
            $"SELECT pg_advisory_xact_lock({MergeGate.AdmissionLockKey})", Token);

        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(Token);
        giveUp.CancelAfter(TimeSpan.FromSeconds(10));
        await CreateMergeGate().AdmitAllAsync([], MarkConverted(item.Id), giveUp.Token);

        await held.RollbackAsync(Token);
        (await FromDb(db => db.HarvestedItems.SingleAsync(i => i.Id == item.Id, Token)))
            .ConvertedAt.ShouldBe(Now, "the marker commits though another batch holds the lock");
    }

    [Fact]
    public async Task ParallelNearDuplicateBatches_ConvergeOnOneWisdom_ReinforcedTwice()
    {
        var project = await AddProjectAsync();
        var first = await AddHarvestedItemAsync(project.Id);
        var second = await AddHarvestedItemAsync(project.Id);
        const string firstText = "Serialize admissions at the gate";
        const string secondText = "The gate serializes admissions";
        Embeddings.Map(firstText, TestVectors.Basis);
        Embeddings.Map(secondText, TestVectors.Basis);

        // Stage the exact race the advisory lock exists to close: batch A holds its transaction
        // open until batch B is observed *waiting* on the lock. Unserialized, B's search would
        // run before A commits, see nothing on its own connection, and insert a duplicate.
        var admittedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batchA = CreateMergeGate().AdmitAllAsync(
            [new WisdomCandidate(WisdomKind.Lesson, project.Id, firstText, HarvestedItemId: first.Id)],
            async (_, ct) =>
            {
                admittedA.SetResult();
                await WaitForAnAdvisoryLockWaiterAsync(ct);
            },
            Token);
        var batchB = RunBatchBAsync();
        await Task.WhenAll(batchA, batchB);

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Reinforcement.ShouldBe(2, "near-simultaneous duplicates converge (§6) in either completion order");
        var provenance = await FromDb(db => db.Provenance
            .Select(p => p.HarvestedItemId)
            .ToListAsync(Token));
        provenance.ShouldBe([first.Id, second.Id], ignoreOrder: true);

        async Task RunBatchBAsync()
        {
            await admittedA.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await CreateMergeGate().AdmitAllAsync(
                [new WisdomCandidate(WisdomKind.Lesson, project.Id, secondText, HarvestedItemId: second.Id)],
                finalizer: null,
                Token);
        }
    }

    [Fact]
    public async Task AnEditRacingABatchRewrite_SerializesBehindIt_AndTheChainKeepsGrowing()
    {
        // §8.1's edit and §6's rewrite both append to the same (wisdom_id, version) chain. Run
        // unserialized they read the same max version and insert the same number: a unique
        // violation on whichever loses. The gate's lock is what makes them queue instead.
        var project = await AddProjectAsync();
        var first = await AddHarvestedItemAsync(project.Id);
        var second = await AddHarvestedItemAsync(project.Id);
        const string originalText = "The chain has one writer";
        const string confirmingText = "One writer per chain";
        const string mergedText = "A version chain has exactly one writer";
        const string editedText = "A version chain has one writer, by hand";
        Embeddings.Map(originalText, TestVectors.Basis);
        Embeddings.Map(confirmingText, TestVectors.WithCosine(0.9));
        Embeddings.Map(mergedText, TestVectors.WithCosine(0.95));
        Embeddings.Map(editedText, TestVectors.WithCosine(0.5));
        Arbiter.Enqueue(new MergeRuling.Agreement(mergedText));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, project.Id, originalText, HarvestedItemId: first.Id));
        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));

        // The rewriting batch holds the lock until the edit is observed waiting on it.
        var rewriting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batch = CreateMergeGate().AdmitAllAsync(
            [new WisdomCandidate(WisdomKind.Fact, project.Id, confirmingText, HarvestedItemId: second.Id)],
            async (_, ct) =>
            {
                rewriting.SetResult();
                await WaitForABlockedSessionAsync(ct);
            },
            Token);
        var edit = EditWhileTheBatchHoldsTheLockAsync();
        await Task.WhenAll(batch, edit);

        var versions = await FromDb(db => db.WisdomVersions
            .Where(v => v.WisdomId == wisdom.Id)
            .OrderBy(v => v.Version)
            .ToListAsync(Token));
        versions.Select(v => (v.Version, v.Cause)).ShouldBe(
            [
                (1, WisdomVersionCause.Distilled),
                (2, WisdomVersionCause.Merged),
                (3, WisdomVersionCause.Edited),
            ],
            "the edit numbers its version off the chain the batch left behind");
        versions[2].Text.ShouldBe(editedText);
        var edited = await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == wisdom.Id, Token));
        edited.Text.ShouldBe(editedText);
        edited.Reinforcement.ShouldBe(2, "the batch confirmed; the edit only reworded (§8.1)");

        async Task EditWhileTheBatchHoldsTheLockAsync()
        {
            await rewriting.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await CreateMergeGate().EditAsync(wisdom.Id, editedText, Token);
        }
    }

    [Fact]
    public async Task AnEdit_LeavesConfirmationAloneAndSkipsAnUnchangedOrMissingWisdom()
    {
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);
        const string text = "An edit is not a confirmation";
        await AdmitAsync(new WisdomCandidate(WisdomKind.Fact, project.Id, text, HarvestedItemId: item.Id));
        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));

        var embeddedBefore = Embeddings.Batches;
        await CreateMergeGate().EditAsync(wisdom.Id, $"  {text}  ", Token);
        await CreateMergeGate().EditAsync(Guid.NewGuid(), "into the void", Token);

        Embeddings.Batches.ShouldBe(
            embeddedBefore, "the unlocked pre-check settles a no-op before the model is asked");
        (await FromDb(db => db.WisdomVersions.CountAsync(Token)))
            .ShouldBe(1, "an unchanged or missing edit writes no version");
        var unchanged = await FromDb(db => db.Wisdom.SingleAsync(Token));
        unchanged.Reinforcement.ShouldBe(1);
        unchanged.LastConfirmedAt.ShouldBe(Now);
    }

    [Fact]
    public async Task AnEdit_RewordsARetiredWisdom_AndLeavesItRetired()
    {
        // Retire and edit are independent axes (#71): Retire governs a row's standing, an edit
        // its words, and the gate never consults the one to decide the other. So a curator can
        // repair something shelved without unretire → edit → retire, which would expose the bad
        // text to live recall on the way past.
        var project = await AddProjectAsync();
        var item = await AddHarvestedItemAsync(project.Id);
        const string text = "Retired but badly worded";
        const string editedText = "Retired and now worded well";
        await AdmitAsync(new WisdomCandidate(WisdomKind.Fact, project.Id, text, HarvestedItemId: item.Id));
        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));

        // Retired the way §10 retires (WisdomBrowser.RetireAsync), at a moment of its own and
        // with the clock moved on after it — so an edit that re-stamped RetiredAt reads as red
        // as one that cleared it.
        var retiredAt = Now.AddMinutes(30);
        await Context.Wisdom.Where(w => w.Id == wisdom.Id)
            .ExecuteUpdateAsync(w => w.SetProperty(x => x.RetiredAt, retiredAt), Token);
        Clock.Advance(TimeSpan.FromHours(1));

        await CreateMergeGate().EditAsync(wisdom.Id, editedText, Token);

        var edited = await FromDb(db => db.Wisdom.SingleAsync(Token));
        edited.Text.ShouldBe(editedText, "an edit rewords regardless of standing");
        edited.RetiredAt.ShouldBe(retiredAt, "an edit neither unretires nor re-stamps the retirement");

        var versions = await FromDb(db => db.WisdomVersions.OrderBy(v => v.Version).ToListAsync(Token));
        versions.Select(v => (v.Version, v.Cause)).ShouldBe(
            [(1, WisdomVersionCause.Distilled), (2, WisdomVersionCause.Edited)],
            "a Retired row grows the same cause=edited chain a live one does");
        versions[1].Text.ShouldBe(editedText);
    }

    /// <summary>Polls pg_locks until some session waits on an advisory lock in this database.</summary>
    private Task WaitForAnAdvisoryLockWaiterAsync(CancellationToken cancellationToken)
        => PollUntilAnyAsync(
            """
            SELECT count(*)::int AS "Value"
            FROM pg_locks l
            JOIN pg_database d ON d.oid = l.database
            WHERE l.locktype = 'advisory' AND NOT l.granted AND d.datname = current_database()
            """,
            "no session ever waited on the gate's advisory lock",
            cancellationToken);

    /// <summary>
    /// Polls until some other session on this database is blocked on one of the two locks an edit
    /// can collide on. Wider than the advisory-only probe on purpose, and only where an edit is
    /// the racer: an edit that skipped the gate's lock would block on the version chain's unique
    /// index instead, so the mutation check goes red on that collision rather than timing out here
    /// waiting for a lock the mutant never takes. Naming both rather than accepting any
    /// <c>Lock</c> wait keeps an unrelated waiter — autovacuum, a stray backend — from releasing
    /// the test early and leaving the serialization it is named for unexercised.
    /// <c>pg_stat_activity</c>, not <c>pg_locks</c>, because the <c>transactionid</c> lock carries
    /// no database oid to filter this class's throwaway database by — and an unfiltered pg_locks
    /// would see other classes' databases.
    /// </summary>
    private Task WaitForABlockedSessionAsync(CancellationToken cancellationToken)
        => PollUntilAnyAsync(
            """
            SELECT count(*)::int AS "Value"
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND wait_event_type = 'Lock'
              AND wait_event IN ('advisory', 'transactionid')
              AND pid <> pg_backend_pid()
            """,
            "no session ever blocked behind the batch holding the gate's lock",
            cancellationToken);

    /// <summary>Runs <paramref name="countingSql"/> every 25 ms until it counts something.</summary>
    private async Task PollUntilAnyAsync(
        string countingSql, string timeoutMessage, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        for (var attempt = 0; attempt < 400; attempt++)
        {
            var found = await context.Database.SqlQueryRaw<int>(countingSql).SingleAsync(cancellationToken);
            if (found > 0)
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException(timeoutMessage);
    }

    /// <summary>
    /// One candidate as its own Admission batch — the gate owns the embedding, the transaction,
    /// and the commit, so the helper only builds a gate and calls it.
    /// </summary>
    private async Task AdmitAsync(WisdomCandidate candidate)
        => await CreateMergeGate().AdmitAllAsync([candidate], finalizer: null, Token);

    /// <summary>
    /// A §5-shaped finalizer: the conversion marker written on the gate's own batch context, the
    /// way <see cref="Mimir.Server.Harvest.HarvestConverter"/> writes it.
    /// </summary>
    private static Func<MimirDbContext, CancellationToken, Task> MarkConverted(Guid itemId)
        => async (batch, ct) => await batch.HarvestedItems
            .Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(update => update.SetProperty(i => i.ConvertedAt, Now), ct);
}
