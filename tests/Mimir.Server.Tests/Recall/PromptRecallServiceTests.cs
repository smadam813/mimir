using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The Prompt lane (§7) against a real Postgres: the cosine gate over the ambient candidate
/// universe, query-ranked injection within the 1,500-char budget, and the §3 logging rule — every
/// actual injection logs a row with the prompt as <c>query_context</c>, an empty decision leaves
/// no trace.
/// </summary>
public sealed class PromptRecallServiceTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    /// <summary>A prompt with no word overlap with any test Wisdom, so only the vector leg ranks.</summary>
    private const string Prompt = "how do I deploy the pipeline?";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Embeddings.Map(Prompt, TestVectors.Basis);
    }

    [Fact]
    public async Task OnTopicPrompt_InjectsLabeledWisdomWithinBudget_AndLogsTheInjection()
    {
        var project = await AddProjectAsync("prompt");
        var scoped = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.90);
        var global = await AddWisdomAsync(Project.GlobalId, "unrelated filler two", cosine: 0.91);
        var sessionId = NewSessionId();

        var injection = await ComposeAsync(project.Id, sessionId);

        injection.ShouldStartWith("<mimir-memory>");
        injection.Length.ShouldBeLessThanOrEqualTo(1500);
        injection.ShouldContain(scoped.Text);
        injection.ShouldContain(global.Text);
        // Affinity (1.5×) dwarfs the nearer global row's fused-rank edge — project Wisdom leads.
        injection.IndexOf(scoped.Text, StringComparison.Ordinal)
            .ShouldBeLessThan(injection.IndexOf(global.Text, StringComparison.Ordinal));

        var logged = await FromDb(db => db.Injections.SingleAsync(Token));
        logged.SessionId.ShouldBe(sessionId);
        logged.ProjectId.ShouldBe(project.Id);
        logged.Lane.ShouldBe(InjectionLane.Prompt);
        logged.QueryContext.ShouldBe(Prompt, customMessage: "injection rows carry the prompt (§3)");
        logged.At.ShouldBe(Now);
        logged.Chars.ShouldBe(injection.Length);
        logged.Items.Select(i => i.WisdomId).ShouldBe([scoped.Id, global.Id]);
        logged.Items[0].Score.ShouldBeGreaterThan(logged.Items[1].Score);
    }

    [Fact]
    public async Task OffTopicPrompt_InjectsNothing_AndLogsNothing()
    {
        var project = await AddProjectAsync("prompt");
        await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.5);

        var injection = await ComposeAsync(project.Id);

        injection.ShouldBeEmpty();
        (await FromDb(db => db.Injections.CountAsync(Token)))
            .ShouldBe(0, "empty Prompt-lane decisions are not logged (§7)");
    }

    [Fact]
    public async Task TheGateReadsCosine_ATopFusedRankBelowTheGateStaysShut()
    {
        var project = await AddProjectAsync("prompt");
        // Both legs surface this row — the best possible fused rank (≈ 2/61, far above any
        // single-leg fusion) — yet its cosine sits below 0.75, so nothing injects (§3).
        await AddWisdomAsync(project.Id, "deploy the pipeline notes", cosine: 0.6);

        var injection = await ComposeAsync(project.Id);

        injection.ShouldBeEmpty();
    }

    [Fact]
    public async Task ZeroNormEmbeddingsNaNCosine_NeverOpensTheGate()
    {
        var project = await AddProjectAsync("prompt");
        // pgvector computes a literal NaN cosine for a zero-magnitude embedding (no zero-norm
        // guard in its distance function). The gate's affirmative >= must hold shut for NaN —
        // a `< threshold` reading would let the degenerate row slip through.
        await AddWisdomAsync(
            project.Id, "unrelated filler one", embedding: new float[TestVectors.Dimensions]);

        var injection = await ComposeAsync(project.Id);

        injection.ShouldBeEmpty();
        (await FromDb(db => db.Injections.CountAsync(Token))).ShouldBe(0);
    }

    [Fact]
    public async Task AnotherProjectsWisdom_NeverOpensTheGate_NorInjects()
    {
        var (project, other) = (await AddProjectAsync("prompt"), await AddProjectAsync("prompt"));
        await AddWisdomAsync(other.Id, "unrelated filler one", cosine: 0.95);

        var injection = await ComposeAsync(project.Id);

        injection.ShouldBeEmpty();
        (await FromDb(db => db.Injections.CountAsync(Token))).ShouldBe(0);
    }

    [Fact]
    public async Task NativeHarvestOnlyWisdom_NeverOpensTheGate_NorInjects()
    {
        var project = await AddProjectAsync("prompt");
        var native = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.95);
        var item = await AddHarvestedItemAsync(project.Id, content: "harvested content");
        await AddProvenanceAsync(native.Id, harvestedItemId: item.Id);

        var injection = await ComposeAsync(project.Id);

        injection.ShouldBeEmpty(
            "the built-in already loads the current Project's auto-memory natively (§7)");
    }

    [Fact]
    public async Task Injection_FillsToTheBudget_AndLogsOnlyWhatMadeItIn()
    {
        var project = await AddProjectAsync("prompt");
        var injected = await AddWisdomAsync(project.Id, new string('a', 200), cosine: 0.9);
        await AddWisdomAsync(project.Id, new string('b', 200), cosine: 0.8);

        // A budget with room for the header and one 200-char entry, not two.
        var injection = await ComposeAsync(
            project.Id, options: new RecallOptions { PromptBudgetChars = 450 });

        injection.Length.ShouldBeLessThanOrEqualTo(450);
        var logged = await FromDb(db => db.Injections.SingleAsync(Token));
        logged.Items.Select(i => i.WisdomId).ShouldBe([injected.Id]);
    }

    private async Task<string> ComposeAsync(
        Guid projectId, string? sessionId = null, RecallOptions? options = null)
    {
        // One RecallOptions for both: the ranking scores with the same knobs the service budgets
        // against, so an override here reaches the whole path rather than half of it.
        var recall = options ?? new RecallOptions();
        var service = new PromptRecallService(
            Context, CreateQueryRanking(recall: recall), Options.Create(recall), Clock);
        return await service.ComposeInjectionAsync(
            sessionId ?? NewSessionId(), projectId, Prompt, Token);
    }

    private static string NewSessionId() => $"sess-{Guid.NewGuid():N}";
}
