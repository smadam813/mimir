using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Storage;

/// <summary>
/// The §3 hybrid search against a real Postgres: top-N per leg, RRF fusion for ordering only,
/// the vector leg's cosine riding along for thresholds, and the non-Retired filter. A tiny
/// per-leg top-N (2) makes leg membership itself observable with a handful of rows.
/// </summary>
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

        // Vector leg (top 2): vectorOnly, dualLeg. FTS leg (top 2): dualLeg, ftsOnly. The row
        // both legs surfaced fuses two reciprocal ranks and must lead; the third-nearest,
        // non-matching row is in neither leg and must be absent entirely.
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

        // A near-perfect match on both legs still fuses to ≈ 0.033 — the §3 score-scale rule:
        // fused values order candidates and are never comparable to a cosine threshold.
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

    [Fact]
    public async Task RowsTiedOnTheirFusedScore_AreOrderedById()
    {
        // Seeded highest-id-first so the heap hands them back in the wrong order: without the id
        // tie-break the query would be right only by accident.
        var higher = await AddIdentifiedWisdomAsync(
            new Guid("ffffffff-0000-0000-0000-000000000002"), "pangolin pangolin", cosine: 0.0);
        var lower = await AddIdentifiedWisdomAsync(
            new Guid("00000000-0000-0000-0000-0000000000f1"), "an unrelated note", cosine: 0.9);

        // One leg each at rank 1, so both contribute exactly 1/(k+1) and the fused scores tie.
        var hits = await Search(perLegTopN: 1)
            .SearchAsync(new Vector(TestVectors.Basis), "pangolin", Token);

        hits.Select(h => h.FusedScore).Distinct().Count().ShouldBe(1, "the two rows are tied");
        hits.Select(h => h.WisdomId).ShouldBe([lower.Id, higher.Id]);
    }

    private async Task<Wisdom> AddIdentifiedWisdomAsync(Guid id, string text, double cosine)
    {
        var wisdom = new Wisdom
        {
            Id = id,
            Kind = WisdomKind.Fact,
            ScopeProjectId = Project.GlobalId,
            Text = text,
            Embedding = new Vector(TestVectors.WithCosine(cosine)),
            Reinforcement = 1,
            LastConfirmedAt = Now,
        };
        Context.Wisdom.Add(wisdom);
        await Context.SaveChangesAsync(Token);
        return wisdom;
    }

    private WisdomSearch Search(int perLegTopN = 2)
        => new(Context, Options.Create(new SearchOptions { RrfK = RrfK, PerLegTopN = perLegTopN }));

    private async Task<Wisdom> AddGlobalWisdomAsync(
        string text, double cosine, DateTimeOffset? retiredAt = null)
        => await AddWisdomAsync(Project.GlobalId, text, cosine, retiredAt: retiredAt);
}
