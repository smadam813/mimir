using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Storage;

/// <summary>
/// The §3 hybrid search against a real Postgres: top-N per leg, RRF fusion for ordering only,
/// the vector leg's cosine riding along for thresholds, and the non-Retired filter. A tiny
/// per-leg top-N (2) makes leg membership itself observable with a handful of rows.
/// </summary>
public sealed class WisdomSearchTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private const int RrfK = 60;

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
    public async Task RrfFusion_RanksADualLegRowAboveEitherSingleLegRow()
    {
        await ResetWisdomAsync();
        var vectorOnly = await AddWisdomAsync(TestVectors.WithCosine(0.95), "unrelated filler one");
        var dualLeg = await AddWisdomAsync(TestVectors.WithCosine(0.9), "zebra stripes pattern");
        var outOfBothLegs = await AddWisdomAsync(TestVectors.WithCosine(0.5), "unrelated filler two");
        var ftsOnly = await AddWisdomAsync(TestVectors.WithCosine(0.0), "zebra zebra zebra");

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
        await ResetWisdomAsync();
        await AddWisdomAsync(TestVectors.WithCosine(0.99), "zebra herd zebra");

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
        await ResetWisdomAsync();
        var near = await AddWisdomAsync(TestVectors.WithCosine(0.6), "quagga sighting");
        var nearer = await AddWisdomAsync(TestVectors.WithCosine(0.8), "unrelated filler");
        var offLeg = await AddWisdomAsync(TestVectors.WithCosine(-0.5), "quagga quagga quagga");

        var hits = await Search().SearchAsync(new Vector(TestVectors.Basis), "quagga", Token);

        hits.Single(h => h.WisdomId == near.Id).Cosine.ShouldNotBeNull().ShouldBe(0.6, 1e-3);
        hits.Single(h => h.WisdomId == nearer.Id).Cosine.ShouldNotBeNull().ShouldBe(0.8, 1e-3);
        hits.Single(h => h.WisdomId == offLeg.Id).Cosine.ShouldBeNull(
            "a row the FTS leg alone surfaced carries no cosine, so it can never pass a threshold");
    }

    [Fact]
    public async Task RetiredWisdom_IsInvisibleToBothLegs()
    {
        await ResetWisdomAsync();
        var live = await AddWisdomAsync(TestVectors.WithCosine(0.7), "okapi facts");
        await AddWisdomAsync(TestVectors.WithCosine(0.99), "okapi okapi okapi", retiredAt: Now);

        var hits = await Search().SearchAsync(new Vector(TestVectors.Basis), "okapi", Token);

        hits.ShouldHaveSingleItem().WisdomId.ShouldBe(live.Id);
    }

    private WisdomSearch Search()
        => new(Context, Options.Create(new SearchOptions { RrfK = RrfK, PerLegTopN = 2 }));

    private async Task<Wisdom> AddWisdomAsync(
        float[] embedding, string text, DateTimeOffset? retiredAt = null)
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Fact,
            ScopeProjectId = Project.GlobalId,
            Text = text,
            Embedding = new Vector(embedding),
            Reinforcement = 1,
            LastConfirmedAt = Now,
            RetiredAt = retiredAt,
        };
        Context.Wisdom.Add(wisdom);
        await Context.SaveChangesAsync(Token);
        return wisdom;
    }

    /// <summary>Leg membership is global to the table, so each test starts from clean Wisdom.</summary>
    private async Task ResetWisdomAsync()
    {
        await Context.Provenance.ExecuteDeleteAsync(Token);
        await Context.WisdomVersions.ExecuteDeleteAsync(Token);
        await Context.Wisdom.ExecuteDeleteAsync(Token);
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
