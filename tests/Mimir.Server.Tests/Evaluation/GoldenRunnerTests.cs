using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Evaluation;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Evaluation;

/// <summary>
/// The §9 golden runner against a real Postgres: every GoldenCase replays through the shared §7
/// query ranking — unthresholded, under the case's own affinity context — and passes only when
/// its expected Wisdom ranks within the golden-set k. The report carries each case's actual rank
/// and the pass rate over the suite.
/// </summary>
public sealed class GoldenRunnerTests(CaptureDatabaseFixture fixture)
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
    public async Task ExpectedWisdomInTopK_Passes_WithItsRank()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var expected = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);
        var goldenCase = await AddCaseAsync(project.Id, expected.Id);

        var report = await RunAsync();

        var result = report.Results.ShouldHaveSingleItem();
        result.CaseId.ShouldBe(goldenCase.Id);
        result.ExpectedWisdomId.ShouldBe(expected.Id);
        result.Rank.ShouldBe(1);
        result.Passed.ShouldBeTrue();
        report.PassRate.ShouldBe(1.0);
    }

    [Fact]
    public async Task ExpectedWisdomBelowK_Fails_WithItsActualRank()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        for (var i = 0; i < 5; i++)
        {
            await AddWisdomAsync(project.Id, $"unrelated filler {i}", cosine: 0.9 - (i * 0.01));
        }

        var expected = await AddWisdomAsync(project.Id, "unrelated filler last", cosine: 0.5);
        await AddCaseAsync(project.Id, expected.Id);

        var report = await RunAsync();

        var result = report.Results.ShouldHaveSingleItem();
        result.Rank.ShouldBe(6);
        result.Passed.ShouldBeFalse();
        report.PassRate.ShouldBe(0.0);
    }

    [Fact]
    public async Task ExpectedWisdomOffBothLegs_Fails_WithNoRank()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);
        var expected = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.1);
        await AddCaseAsync(project.Id, expected.Id);

        // A per-leg top-N of 1 crowds the expected row out of the vector leg, and its text
        // shares no word with the query — it never ranks at all.
        var report = await RunAsync(new SearchOptions { PerLegTopN = 1 });

        var result = report.Results.ShouldHaveSingleItem();
        result.Rank.ShouldBeNull();
        result.Passed.ShouldBeFalse();
    }

    [Fact]
    public async Task CasesRankUnderTheirOwnAffinityContext()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.91);
        var expected = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.90);
        await AddCaseAsync(project.Id, expected.Id);

        // At k=1 the case passes only if the runner ranks under the case's Project: the 1.5×
        // affinity boost is what lifts the expected row past the nearer Global one.
        var report = await RunAsync(new SearchOptions { GoldenSetK = 1 });

        report.Results.ShouldHaveSingleItem().Passed.ShouldBeTrue();
    }

    [Fact]
    public async Task MixedSuite_ReportsThePassRate()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var near = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);
        var far = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.5);
        await AddCaseAsync(project.Id, near.Id);
        await AddCaseAsync(project.Id, far.Id);

        var report = await RunAsync(new SearchOptions { GoldenSetK = 1 });

        report.Results.Count.ShouldBe(2);
        report.PassedCount.ShouldBe(1);
        report.PassRate.ShouldBe(0.5);
    }

    [Fact]
    public async Task CasesSharingAQueryAndProject_ReplayOneRanking()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var near = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);
        var far = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.5);
        await AddCaseAsync(project.Id, near.Id);
        await AddCaseAsync(project.Id, far.Id);

        var report = await RunAsync();

        report.Results.Count.ShouldBe(2);
        _embeddings.Batches.ShouldBe(1);
    }

    [Fact]
    public async Task EmptySuite_PassesVacuously()
    {
        await Context.ResetWisdomAsync(Token);

        var report = await RunAsync();

        report.Results.ShouldBeEmpty();
        report.PassRate.ShouldBe(1.0);
    }

    private async Task<GoldenReport> RunAsync(SearchOptions? searchOptions = null)
    {
        var search = Options.Create(searchOptions ?? new SearchOptions());
        var runner = new GoldenRunner(
            Context,
            new QueryRanking(
                Context,
                _embeddings,
                new WisdomSearch(Context, search),
                Options.Create(new RecallOptions()),
                new FakeTimeProvider(Now)),
            search);
        return await runner.RunAsync(Token);
    }

    private async Task<Project> AddProjectAsync()
    {
        var project = TestData.NewProject("golden");
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

    private async Task<GoldenCase> AddCaseAsync(Guid projectId, Guid expectedWisdomId)
    {
        var goldenCase = new GoldenCase
        {
            Id = Guid.CreateVersion7(),
            QueryContext = Query,
            ProjectId = projectId,
            ExpectedWisdomId = expectedWisdomId,
            Note = "test case",
        };
        Context.GoldenCases.Add(goldenCase);
        await Context.SaveChangesAsync(Token);
        return goldenCase;
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
