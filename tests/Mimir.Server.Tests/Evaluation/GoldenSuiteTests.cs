using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Evaluation;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Npgsql;
using OllamaSharp;

namespace Mimir.Server.Tests.Evaluation;

/// <summary>
/// The §9 dev-time golden suite: every GoldenCase in the development database — promoted from
/// the injection log and hand-inserted alike — replayed through the shared §7 query ranking with
/// the real embedding model, against the real Wisdom tier. The pass rate is reported either way;
/// one failing case fails the test. Needs the compose stack up (postgres and ollama), and skips
/// without it like every other integration test.
/// </summary>
public sealed class GoldenSuiteTests(ITestOutputHelper output)
{
    /// <summary>Ollama from the host, like <see cref="TestPostgres"/> reaches postgres.</summary>
    private static readonly Uri OllamaEndpoint = new(
        Environment.GetEnvironmentVariable("MIMIR_TEST_OLLAMA") ?? "http://localhost:11434");

    [Fact]
    public async Task EveryGoldenCase_RanksItsExpectedWisdomWithinTheGoldenSetK()
    {
        var token = TestContext.Current.CancellationToken;
        await using var db = new MimirDbContext(new DbContextOptionsBuilder<MimirDbContext>()
            .UseNpgsql(TestPostgres.AdminConnectionString, npgsql => npgsql.UseVector())
            .Options);
        if (!await CanConnectAsync(db, token))
        {
            Assert.Skip(TestPostgres.SkipMessage("development database unreachable"));
        }

        var modelOptions = new ModelOptions();
        var ollama = new OllamaApiClient(OllamaEndpoint, modelOptions.Embedding);
        if (!await IsOllamaUpAsync(ollama, token))
        {
            Assert.Skip($"No Ollama reachable at {OllamaEndpoint} for the golden suite. "
                + "Run `docker compose up -d ollama`, or set MIMIR_TEST_OLLAMA.");
        }

        var search = Options.Create(new SearchOptions());
        var runner = new GoldenRunner(
            db,
            new QueryRanking(
                db,
                (IEmbeddingGenerator<string, Embedding<float>>)ollama,
                new WisdomSearch(db, search),
                Options.Create(new RecallOptions()),
                TimeProvider.System),
            search);

        GoldenReport report;
        try
        {
            report = await runner.RunAsync(token);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            Assert.Skip("The development database predates the GoldenSchema migration — "
                + "restart the compose stack to migrate, then rerun.");
            return;
        }

        output.WriteLine($"Golden suite: {report.PassedCount}/{report.Results.Count} passed "
            + $"(pass rate {report.PassRate:P0}).");
        foreach (var result in report.Results)
        {
            output.WriteLine($"  {(result.Passed ? "pass" : "FAIL")} · "
                + $"rank {(result.Rank?.ToString() ?? "none")} · "
                + $"\"{result.QueryContext}\" · {result.Note}");
        }

        report.Results.Where(r => !r.Passed).ShouldBeEmpty(
            "every GoldenCase must rank its expected Wisdom within the golden-set k (§9)");
    }

    /// <summary>Reachability probe that reports instead of throwing, so absence skips.</summary>
    private static async Task<bool> CanConnectAsync(MimirDbContext db, CancellationToken token)
    {
        try
        {
            return await db.Database.CanConnectAsync(token);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Same for Ollama: a refused connection means "not up", not a test error.</summary>
    private static async Task<bool> IsOllamaUpAsync(OllamaApiClient ollama, CancellationToken token)
    {
        try
        {
            return await ollama.IsRunningAsync(token);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
