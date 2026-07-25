using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The §7 query ranking as a reusable service against a real Postgres: hybrid-search rank fused
/// with the record factors, the affinity context as caller input, and no threshold of its own —
/// consumers own their gates. The Candidate Universe is not theirs to own: each method names the
/// universe it searches, so the ambient ranking restricts inside the §3 search while the
/// everything ranking reaches the whole tier under narrowings the caller states.
/// </summary>
public sealed class QueryRankingTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    /// <summary>A query with no word overlap with any test Wisdom, so only the vector leg ranks.</summary>
    private const string Query = "how do I deploy the pipeline?";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Embeddings.Map(Query, TestVectors.Basis);
    }

    [Fact]
    public async Task AffinityContext_LiftsOwnProjectWisdomAboveANearerGlobalRow()
    {
        var project = await AddProjectAsync("rank");
        var global = await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.91);
        var scoped = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.90);

        var ranked = await RankEverythingAsync(project.Id);

        // Vector ranks 1 and 2 fuse to 1/61 vs 1/62 — a 1.6% edge the 1.5× affinity dwarfs.
        ranked.Select(r => r.WisdomId).ShouldBe([scoped.Id, global.Id]);
    }

    [Fact]
    public async Task AffinityIsCallerInput_AnotherProjectsContextLeavesTheRowUnboosted()
    {
        var (project, other) = (await AddProjectAsync("rank"), await AddProjectAsync("rank"));
        var global = await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.91);
        var scoped = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.90);

        var ranked = await RankEverythingAsync(other.Id);

        // Same rows, different affinity context: neither matches, so the nearer row leads.
        ranked.Select(r => r.WisdomId).ShouldBe([global.Id, scoped.Id]);
    }

    [Fact]
    public async Task TheAmbientUniverse_HoldsGlobalAndTheSessionsOwn_NotAnotherProjects()
    {
        var (project, other) = (await AddProjectAsync("rank"), await AddProjectAsync("rank"));
        var global = await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.91);
        var scoped = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.90);

        // The same two rows the everything ranking returns above — membership, not an annotation:
        // ranking the ambient universe of a Project that owns neither row returns only the Global.
        (await RankAmbientAsync(other.Id)).Select(r => r.WisdomId).ShouldBe([global.Id]);
        (await RankAmbientAsync(project.Id)).Select(r => r.WisdomId)
            .ShouldBe([scoped.Id, global.Id], ignoreOrder: true);
    }

    /// <summary>
    /// The crowd-out bug's tombstone (#58): the §3 search bounds each leg to the per-leg top-N, so
    /// a foreign Project's nearer corpus used to fill both legs and leave ambient recall empty
    /// while an eligible match sat one row deeper. The universe now restricts inside the search,
    /// before the truncation, so the eligible match competes only against its own universe.
    /// </summary>
    [Fact]
    public async Task TheAmbientUniverse_SurvivesANearerForeignCorpus_FillingThePerLegTopN()
    {
        var (project, other) = (await AddProjectAsync("rank"), await AddProjectAsync("rank"));
        var nearest = await AddWisdomAsync(other.Id, "unrelated filler one", cosine: 0.99);
        var nextNearest = await AddWisdomAsync(other.Id, "unrelated filler two", cosine: 0.98);
        var eligible = await AddWisdomAsync(project.Id, "unrelated filler three", cosine: 0.90);
        var options = new SearchOptions { PerLegTopN = 2 };

        (await RankAmbientAsync(project.Id, options)).Select(r => r.WisdomId)
            .ShouldBe([eligible.Id]);

        // The crowd-out itself, still real one method over: the everything ranking's top-2 holds
        // the two foreign rows and the eligible match never reaches a consumer that must filter.
        (await RankEverythingAsync(project.Id, options)).Select(r => r.WisdomId)
            .ShouldBe([nearest.Id, nextNearest.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task SalientProvenance_OutranksANearerPlainRow()
    {
        var project = await AddProjectAsync("rank");
        var plain = await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.91);
        var remembered = await AddWisdomAsync(Project.GlobalId, "unrelated filler two", cosine: 0.90);
        var episode = await AddEpisodeAsync(project.Id);
        var evt = await AddEventAsync(
            episode.Id, seq: 1, EventType.Remember, payload: """{"content":"remember this"}""");
        await AddProvenanceAsync(remembered.Id, episode.Id, evt.Id);

        var ranked = await RankEverythingAsync(project.Id);

        ranked.Select(r => r.WisdomId).ShouldBe([remembered.Id, plain.Id]);
    }

    [Fact]
    public async Task Unthresholded_EveryHitRanks_WithTheVectorLegsCosineRidingAlong()
    {
        var project = await AddProjectAsync("rank");
        var near = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);
        var far = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.2);
        var ftsOnly = await AddWisdomAsync(project.Id, "deploy the pipeline notes", cosine: 0.0);

        // Per-leg top-N of 2: the vector leg holds the two nearest rows, so the FTS-matched row
        // rides in on its leg alone and carries no cosine.
        var ranked = await RankEverythingAsync(project.Id, new SearchOptions { PerLegTopN = 2 });

        ranked.Select(r => r.WisdomId).ShouldBe([near.Id, far.Id, ftsOnly.Id], ignoreOrder: true);
        ranked.Single(r => r.WisdomId == near.Id).Cosine.ShouldNotBeNull().ShouldBe(0.9, tolerance: 1e-3);
        ranked.Single(r => r.WisdomId == far.Id).Cosine.ShouldNotBeNull().ShouldBe(0.2, tolerance: 1e-3);
        ranked.Single(r => r.WisdomId == ftsOnly.Id).Cosine.ShouldBeNull();
    }

    [Fact]
    public async Task RankedRows_CarryWhatConsumersRender()
    {
        var project = await AddProjectAsync("rank");
        var wisdom = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);

        var row = (await RankEverythingAsync(project.Id)).ShouldHaveSingleItem();

        row.Kind.ShouldBe(wisdom.Kind);
        row.ScopeProjectId.ShouldBe(project.Id);
        row.Text.ShouldBe(wisdom.Text);
        row.LastConfirmedAt.ShouldBe(wisdom.LastConfirmedAt);
        row.Score.ShouldBeGreaterThan(0);
    }

    private async Task<IReadOnlyList<RankedWisdom>> RankAmbientAsync(
        Guid sessionProjectId, SearchOptions? searchOptions = null)
        => await CreateQueryRanking(searchOptions).RankAmbientAsync(Query, sessionProjectId, Token);

    private async Task<IReadOnlyList<RankedWisdom>> RankEverythingAsync(
        Guid affinityProjectId, SearchOptions? searchOptions = null)
        => await CreateQueryRanking(searchOptions)
            .RankEverythingAsync(Query, affinityProjectId, WisdomSearchFilter.None, Token);
}
