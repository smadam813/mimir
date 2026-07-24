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
/// The §7 query ranking as a reusable service against a real Postgres: hybrid-search rank fused
/// with the record factors, the affinity context as caller input, and no threshold of its own —
/// consumers own their gates. The Candidate Universe is not theirs to own: each method names the
/// universe it searches, so the ambient ranking restricts inside the §3 search while the
/// everything ranking reaches the whole tier under narrowings the caller states.
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

        var ranked = await RankEverythingAsync(project.Id);

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

        var ranked = await RankEverythingAsync(other.Id);

        // Same rows, different affinity context: neither matches, so the nearer row leads.
        ranked.Select(r => r.WisdomId).ShouldBe([global.Id, scoped.Id]);
    }

    [Fact]
    public async Task TheAmbientUniverse_HoldsGlobalAndTheSessionsOwn_NotAnotherProjects()
    {
        await Context.ResetWisdomAsync(Token);
        var (project, other) = (await AddProjectAsync(), await AddProjectAsync());
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
        await Context.ResetWisdomAsync(Token);
        var (project, other) = (await AddProjectAsync(), await AddProjectAsync());
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
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var plain = await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.91);
        var remembered = await AddWisdomAsync(Project.GlobalId, "unrelated filler two", cosine: 0.90);
        await AddSalientProvenanceAsync(remembered.Id, project.Id);

        var ranked = await RankEverythingAsync(project.Id);

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
        var ranked = await RankEverythingAsync(project.Id, new SearchOptions { PerLegTopN = 2 });

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

        var row = (await RankEverythingAsync(project.Id)).ShouldHaveSingleItem();

        row.Kind.ShouldBe(wisdom.Kind);
        row.ScopeProjectId.ShouldBe(project.Id);
        row.Text.ShouldBe(wisdom.Text);
        row.LastConfirmedAt.ShouldBe(wisdom.LastConfirmedAt);
        row.Score.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// The name is the universe in both directions: a caller cannot forget the ambient filter, and
    /// cannot smuggle one into the method that says it ranks everything. Rejected before any
    /// embedding or SQL, so this runs over a context that never connects.
    /// </summary>
    [Fact]
    public async Task RankingEverything_RejectsAnAmbientUniverseSmuggledInThroughTheFilter()
    {
        await using var db = new MimirDbContext(
            new DbContextOptionsBuilder<MimirDbContext>()
                .UseNpgsql("Host=guard-checks-never-connect")
                .Options);
        var ranking = new QueryRanking(
            db,
            _embeddings,
            new WisdomSearch(db, Options.Create(new SearchOptions())),
            Options.Create(new RecallOptions()),
            new FakeTimeProvider(Now));
        var project = Guid.CreateVersion7();

        await Should.ThrowAsync<ArgumentException>(
            () => ranking.RankEverythingAsync(
                Query, project, new WisdomSearchFilter { AmbientProjectId = project }, Token));
    }

    private async Task<IReadOnlyList<RankedWisdom>> RankAmbientAsync(
        Guid sessionProjectId, SearchOptions? searchOptions = null)
        => await Ranking(searchOptions).RankAmbientAsync(Query, sessionProjectId, Token);

    private async Task<IReadOnlyList<RankedWisdom>> RankEverythingAsync(
        Guid affinityProjectId, SearchOptions? searchOptions = null)
        => await Ranking(searchOptions)
            .RankEverythingAsync(Query, affinityProjectId, WisdomSearchFilter.None, Token);

    private QueryRanking Ranking(SearchOptions? searchOptions)
        => new(
            Context,
            _embeddings,
            new WisdomSearch(Context, Options.Create(searchOptions ?? new SearchOptions())),
            Options.Create(new RecallOptions()),
            new FakeTimeProvider(Now));

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
