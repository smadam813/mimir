using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Recall;

public sealed class QueryRankingTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
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

        ranked.Select(r => r.WisdomId).ShouldBe([scoped.Id, global.Id]);
    }

    [Fact]
    public async Task AffinityIsCallerInput_AnotherProjectsContextLeavesTheRowUnboosted()
    {
        var (project, other) = (await AddProjectAsync("rank"), await AddProjectAsync("rank"));
        var global = await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.91);
        var scoped = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.90);

        var ranked = await RankEverythingAsync(other.Id);

        ranked.Select(r => r.WisdomId).ShouldBe([global.Id, scoped.Id]);
    }

    [Fact]
    public async Task TheAmbientUniverse_HoldsGlobalAndTheSessionsOwn_NotAnotherProjects()
    {
        var (project, other) = (await AddProjectAsync("rank"), await AddProjectAsync("rank"));
        var global = await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.91);
        var scoped = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.90);

        (await RankAmbientAsync(other.Id)).Select(r => r.WisdomId).ShouldBe([global.Id]);
        (await RankAmbientAsync(project.Id)).Select(r => r.WisdomId)
            .ShouldBe([scoped.Id, global.Id], ignoreOrder: true);
    }

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
            episode.Id, seq: 1, EventType.Remember,
            payload: """{"content":"remember this"}""", salient: true);
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
