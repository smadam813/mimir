using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mimir.Contracts.Hooks;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// The single §4 UserPromptSubmit round-trip end to end: one call records the prompt Event and
/// answers with the Prompt-lane injection — and recall failing (a dead embedder) still leaves a
/// successful capture answering with an empty injection, because everything fails open (§7).
/// </summary>
public sealed class UserPromptEndpointTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    /// <summary>A prompt with no word overlap with the test Wisdom, so only the vector leg ranks.</summary>
    private const string Prompt = "how do I deploy the pipeline?";

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();
        Embeddings.Map(Prompt, TestVectors.Basis);
    }

    [Fact]
    public async Task OnTopicPrompt_RecordsTheEventAndAnswersWithTheInjection()
    {
        var request = Request(Prompt);
        var wisdom = await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.9);

        var reply = await InvokeAsync(request);

        reply.Injection.ShouldContain(wisdom.Text);
        (await PromptEventCountAsync(request.SessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task RecallFailure_StillCapturesTheEvent_AndAnswersEmpty()
    {
        var request = Request(Prompt);
        await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.9);
        Embeddings.Poison(Prompt);

        var reply = await InvokeAsync(request);

        reply.Injection.ShouldBeEmpty("recall fails open; capture must survive it (§7)");
        (await PromptEventCountAsync(request.SessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task PayloadWithoutAPrompt_CapturesTheEvent_AndAnswersEmpty()
    {
        var request = Request(prompt: null);
        await AddWisdomAsync(Project.GlobalId, "unrelated filler one", cosine: 0.9);

        var reply = await InvokeAsync(request);

        reply.Injection.ShouldBeEmpty();
        (await PromptEventCountAsync(request.SessionId)).ShouldBe(1);
    }

    private async Task<UserPromptReply> InvokeAsync(HookEventRequest request)
    {
        var recallOptions = Options.Create(new RecallOptions());
        var capture = new CaptureService(
            Context,
            new ProjectResolver(Context),
            Options.Create(new CaptureOptions()),
            Clock,
            new EpisodeFeed());
        var promptRecall = new PromptRecallService(
            Context,
            new QueryRanking(
                Context,
                Embeddings,
                new WisdomSearch(Context, Options.Create(new SearchOptions())),
                recallOptions,
                Clock),
            recallOptions,
            Clock);
        return await CaptureEndpoints.UserPromptAsync(
            request, capture, promptRecall, NullLoggerFactory.Instance, Token);
    }

    private static HookEventRequest Request(string? prompt)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(prompt is null ? new { } : (object)new { prompt }));
        var suffix = Guid.NewGuid().ToString("N");
        return new HookEventRequest
        {
            SessionId = $"sess-{suffix}",
            Cwd = $@"C:\git\prompt-hook-{suffix}",
            ProjectIdentity = $"github.com/test/prompt-hook-{suffix}",
            ProjectRoot = $@"C:\git\prompt-hook-{suffix}",
            HookEvent = HookEvents.UserPromptSubmit,
            Payload = document.RootElement.Clone(),
        };
    }

    private async Task<int> PromptEventCountAsync(string sessionId)
        => await FromDb(db => db.Events.CountAsync(
            e => e.Type == EventType.UserPromptSubmit
                && db.Episodes.Any(ep => ep.Id == e.EpisodeId && ep.SessionId == sessionId),
            Token));
}
