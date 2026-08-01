using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Evaluation;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Evaluation;

public sealed class GoldenRunnerTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
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

    private Task<GoldenCase> AddCaseAsync(Guid projectId, Guid expectedWisdomId)
        => AddGoldenCaseAsync(projectId, expectedWisdomId, Query);
}
