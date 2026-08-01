using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Storage;

public sealed class WisdomSearchTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    private const int RrfK = 60;

    [Fact]
    public async Task RrfFusion_RanksADualLegRowAboveEitherSingleLegRow()
    {
        var vectorOnly = await AddGlobalWisdomAsync("unrelated filler one", cosine: 0.95);
        var dualLeg = await AddGlobalWisdomAsync("zebra stripes pattern", cosine: 0.9);
        var outOfBothLegs = await AddGlobalWisdomAsync("unrelated filler two", cosine: 0.5);
        var ftsOnly = await AddGlobalWisdomAsync("zebra zebra zebra", cosine: 0.0);

        var hits = await Search().SearchAsync(new Vector(TestVectors.Basis), "zebra", Token);

        hits.Select(h => h.WisdomId).ShouldBe(
            [dualLeg.Id, vectorOnly.Id, ftsOnly.Id], ignoreOrder: true);
        hits[0].WisdomId.ShouldBe(dualLeg.Id);
        hits[0].FusedScore.ShouldBeGreaterThan(hits[1].FusedScore);
        hits.ShouldAllBe(h => h.WisdomId != outOfBothLegs.Id);
    }

    [Fact]
    public async Task FusedScores_AreRankFusionValues_NeverACosineScale()
    {
        await AddGlobalWisdomAsync("zebra herd zebra", cosine: 0.99);

        var hits = await Search().SearchAsync(new Vector(TestVectors.Basis), "zebra", Token);

        var best = hits.ShouldHaveSingleItem();
        best.FusedScore.ShouldBe(2.0 / (RrfK + 1), tolerance: 1e-9);
        best.Cosine.ShouldNotBeNull();
        best.Cosine.Value.ShouldBe(0.99, tolerance: 1e-3);
    }

    [Fact]
    public async Task Cosine_IsTheVectorLegsSimilarity_AndNullOffTheVectorLeg()
    {
        var near = await AddGlobalWisdomAsync("quagga sighting", cosine: 0.6);
        var nearer = await AddGlobalWisdomAsync("unrelated filler", cosine: 0.8);
        var offLeg = await AddGlobalWisdomAsync("quagga quagga quagga", cosine: -0.5);

        var hits = await Search().SearchAsync(new Vector(TestVectors.Basis), "quagga", Token);

        hits.Single(h => h.WisdomId == near.Id).Cosine.ShouldNotBeNull().ShouldBe(0.6, 1e-3);
        hits.Single(h => h.WisdomId == nearer.Id).Cosine.ShouldNotBeNull().ShouldBe(0.8, 1e-3);
        hits.Single(h => h.WisdomId == offLeg.Id).Cosine.ShouldBeNull(
            "a row the FTS leg alone surfaced carries no cosine, so it can never pass a threshold");
    }

    [Fact]
    public async Task RetiredWisdom_IsInvisibleToBothLegs()
    {
        var live = await AddGlobalWisdomAsync("okapi facts", cosine: 0.7);
        await AddGlobalWisdomAsync("okapi okapi okapi", cosine: 0.99, retiredAt: Now);

        var hits = await Search().SearchAsync(new Vector(TestVectors.Basis), "okapi", Token);

        hits.ShouldHaveSingleItem().WisdomId.ShouldBe(live.Id);
    }

    private WisdomSearch Search()
        => new(Context, Options.Create(new SearchOptions { RrfK = RrfK, PerLegTopN = 2 }));

    private async Task<Wisdom> AddGlobalWisdomAsync(
        string text, double cosine, DateTimeOffset? retiredAt = null)
        => await AddWisdomAsync(Project.GlobalId, text, cosine, retiredAt: retiredAt);
}
