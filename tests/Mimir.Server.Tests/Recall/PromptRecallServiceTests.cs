using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Recall;

public sealed class PromptRecallServiceTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
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
        await AddWisdomAsync(project.Id, "deploy the pipeline notes", cosine: 0.6);

        var injection = await ComposeAsync(project.Id);

        injection.ShouldBeEmpty();
    }

    [Fact]
    public async Task ZeroNormEmbeddingsNaNCosine_NeverOpensTheGate()
    {
        var project = await AddProjectAsync("prompt");
        // pgvector answers a literal NaN cosine for a zero-magnitude embedding: its distance
        // function carries no zero-norm guard.
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

        var injection = await ComposeAsync(
            project.Id, options: new RecallOptions { PromptBudgetChars = 450 });

        injection.Length.ShouldBeLessThanOrEqualTo(450);
        var logged = await FromDb(db => db.Injections.SingleAsync(Token));
        logged.Items.Select(i => i.WisdomId).ShouldBe([injected.Id]);
    }

    private async Task<string> ComposeAsync(
        Guid projectId, string? sessionId = null, RecallOptions? options = null)
    {
        var recall = options ?? new RecallOptions();
        var service = new PromptRecallService(
            CreateQueryRanking(recall: recall),
            new InjectionLog(Context, Clock),
            Options.Create(recall));
        return await service.ComposeInjectionAsync(
            sessionId ?? NewSessionId(), projectId, Prompt, Token);
    }

    private static string NewSessionId() => $"sess-{Guid.NewGuid():N}";
}
