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
/// The §7 query ranking as a reusable service against a real Postgres: hybrid-search rank fused
/// with the record factors, the affinity context as caller input, and no thresholds or scope
/// filters of its own — the Prompt lane gates and filters, <c>mimir_search</c> and the golden
/// runner deliberately see everything.
/// </summary>
public sealed class QueryRankingTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A query with no word overlap with any test Wisdom, so only the vector leg ranks.</summary>
    private const string Query = "how do I deploy the pipeline?";

    private readonly FakeEmbeddings _embeddings = new();

    private MimirDbContext? _context;

    public ValueTask InitializeAsync()
    {
        _embeddings.Map(Query, TestVectors.Basis);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task AffinityContext_LiftsOwnProjectWisdomAboveANearerGlobalRow()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var global = await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.91);
        var scoped = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.90);

        var ranked = await RankAsync(project.Id);

        // Vector ranks 1 and 2 fuse to 1/61 vs 1/62 — a 1.6% edge the 1.5× affinity dwarfs.
        ranked.Select(r => r.WisdomId).ShouldBe([scoped.Id, global.Id]);
    }

    [Fact]
    public async Task AffinityIsCallerInput_AnotherProjectsContextLeavesTheRowUnboosted()
    {
        await Context.ResetWisdomAsync(Token);
        var (project, other) = (await AddProjectAsync(), await AddProjectAsync());
        var global = await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.91);
        var scoped = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.90);

        var ranked = await RankAsync(other.Id);

        // Same rows, different affinity context: neither matches, so the nearer row leads —
        // and the foreign-Project row is flagged outside the context's ambient universe.
        ranked.Select(r => r.WisdomId).ShouldBe([global.Id, scoped.Id]);
        ranked.Single(r => r.WisdomId == scoped.Id).AmbientEligible.ShouldBeFalse();
        ranked.Single(r => r.WisdomId == global.Id).AmbientEligible.ShouldBeTrue();
    }

    [Fact]
    public async Task SalientProvenance_OutranksANearerPlainRow()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var plain = await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.91);
        var remembered = await AddWisdomAsync(Project.GlobalId, "unrelated filler two", cosine: 0.90);
        await AddSalientProvenanceAsync(remembered.Id, project.Id);

        var ranked = await RankAsync(project.Id);

        ranked.Select(r => r.WisdomId).ShouldBe([remembered.Id, plain.Id]);
    }

    [Fact]
    public async Task Unthresholded_EveryHitRanks_WithTheVectorLegsCosineRidingAlong()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var near = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);
        var far = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.2);
        var ftsOnly = await AddWisdomAsync(project.Id, "deploy the pipeline notes", cosine: 0.0);

        // Per-leg top-N of 2: the vector leg holds the two nearest rows, so the FTS-matched row
        // rides in on its leg alone and carries no cosine.
        var ranked = await RankAsync(project.Id, new SearchOptions { PerLegTopN = 2 });

        ranked.Select(r => r.WisdomId).ShouldBe([near.Id, far.Id, ftsOnly.Id], ignoreOrder: true);
        ranked.Single(r => r.WisdomId == near.Id).Cosine.ShouldNotBeNull().ShouldBe(0.9, tolerance: 1e-3);
        ranked.Single(r => r.WisdomId == far.Id).Cosine.ShouldNotBeNull().ShouldBe(0.2, tolerance: 1e-3);
        ranked.Single(r => r.WisdomId == ftsOnly.Id).Cosine.ShouldBeNull();
    }

    [Fact]
    public async Task RankedRows_CarryWhatConsumersRender()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var wisdom = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);

        var row = (await RankAsync(project.Id)).ShouldHaveSingleItem();

        row.Kind.ShouldBe(wisdom.Kind);
        row.ScopeProjectId.ShouldBe(project.Id);
        row.Text.ShouldBe(wisdom.Text);
        row.LastConfirmedAt.ShouldBe(wisdom.LastConfirmedAt);
        row.Score.ShouldBeGreaterThan(0);
        row.AmbientEligible.ShouldBeTrue();
    }

    private async Task<IReadOnlyList<RankedWisdom>> RankAsync(
        Guid affinityProjectId, SearchOptions? searchOptions = null)
    {
        var ranking = new QueryRanking(
            Context,
            _embeddings,
            new WisdomSearch(Context, Options.Create(searchOptions ?? new SearchOptions())),
            Options.Create(new RecallOptions()),
            new FakeTimeProvider(Now));
        return await ranking.RankAsync(Query, affinityProjectId, Token);
    }

    private async Task<Project> AddProjectAsync()
    {
        var project = TestData.NewProject("rank");
        Context.Projects.Add(project);
        await Context.SaveChangesAsync(Token);
        return project;
    }

    private async Task<Wisdom> AddWisdomAsync(Guid scopeProjectId, string text, double cosine)
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Fact,
            ScopeProjectId = scopeProjectId,
            Text = text,
            Embedding = new Vector(TestVectors.WithCosine(cosine)),
            Reinforcement = 1,
            LastConfirmedAt = Now,
        };
        Context.Wisdom.Add(wisdom);
        await Context.SaveChangesAsync(Token);
        return wisdom;
    }

    private async Task AddSalientProvenanceAsync(Guid wisdomId, Guid projectId)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{suffix}",
            ProjectId = projectId,
            StartedAt = Now,
            Cwd = $@"C:\git\rank-{suffix}",
        };
        var evt = new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episode.Id,
            Seq = 1,
            Type = EventType.Remember,
            At = Now,
            Payload = """{"content":"remember this"}""",
            PayloadFullSize = 30,
            Salient = true,
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
