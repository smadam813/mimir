using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Evaluation;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Evaluation;

/// <summary>
/// The §9 golden runner against a real Postgres: every GoldenCase replays through the shared §7
/// query ranking — unthresholded, under the case's own affinity context — and passes only when
/// its expected Wisdom ranks within the golden-set k. The report carries each case's actual rank
/// and the pass rate over the suite.
/// </summary>
public sealed class GoldenRunnerTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    /// <summary>A query with no word overlap with any test Wisdom, so only the vector leg ranks.</summary>
    private const string Query = "how do I deploy the pipeline?";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Embeddings.Map(Query, TestVectors.Basis);
    }

    [Fact]
    public async Task ExpectedWisdomInTopK_Passes_WithItsRank()
    {
        var project = await AddProjectAsync("golden");
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
        var project = await AddProjectAsync("golden");
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
        var project = await AddProjectAsync("golden");
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
        var project = await AddProjectAsync("golden");
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
        var project = await AddProjectAsync("golden");
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
        var project = await AddProjectAsync("golden");
        var near = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.9);
        var far = await AddWisdomAsync(project.Id, "unrelated filler two", cosine: 0.5);
        await AddCaseAsync(project.Id, near.Id);
        await AddCaseAsync(project.Id, far.Id);

        var report = await RunAsync();

        report.Results.Count.ShouldBe(2);
        Embeddings.Batches.ShouldBe(1);
    }

    [Fact]
    public async Task EmptySuite_PassesVacuously()
    {
        var report = await RunAsync();

        report.Results.ShouldBeEmpty();
        report.PassRate.ShouldBe(1.0);
    }

    private async Task<GoldenReport> RunAsync(SearchOptions? searchOptions = null)
    {
        var search = searchOptions ?? new SearchOptions();
        var runner = new GoldenRunner(Context, CreateQueryRanking(search), Options.Create(search));
        return await runner.RunAsync(Token);
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
}
