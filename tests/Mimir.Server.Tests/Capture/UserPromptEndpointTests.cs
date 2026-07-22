using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Contracts.Hooks;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// The single §4 UserPromptSubmit round-trip end to end: one call records the prompt Event and
/// answers with the Prompt-lane injection — and recall failing (a dead embedder) still leaves a
/// successful capture answering with an empty injection, because everything fails open (§7).
/// </summary>
public sealed class UserPromptEndpointTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A prompt with no word overlap with the test Wisdom, so only the vector leg ranks.</summary>
    private const string Prompt = "how do I deploy the pipeline?";

    private readonly FakeEmbeddings _embeddings = new();

    private MimirDbContext? _context;

    public ValueTask InitializeAsync()
    {
        _embeddings.Map(Prompt, TestVectors.Basis);
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
    public async Task OnTopicPrompt_RecordsTheEventAndAnswersWithTheInjection()
    {
        await Context.ResetWisdomAsync(Token);
        var request = Request(Prompt);
        var wisdom = await AddWisdomAsync("unrelated filler one", cosine: 0.9);

        var reply = await InvokeAsync(request);

        reply.Injection.ShouldContain(wisdom.Text);
        (await PromptEventCountAsync(request.SessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task RecallFailure_StillCapturesTheEvent_AndAnswersEmpty()
    {
        await Context.ResetWisdomAsync(Token);
        var request = Request(Prompt);
        await AddWisdomAsync("unrelated filler one", cosine: 0.9);
        _embeddings.Poison(Prompt);

        var reply = await InvokeAsync(request);

        reply.Injection.ShouldBeEmpty("recall fails open; capture must survive it (§7)");
        (await PromptEventCountAsync(request.SessionId)).ShouldBe(1);
    }

    [Fact]
    public async Task PayloadWithoutAPrompt_CapturesTheEvent_AndAnswersEmpty()
    {
        await Context.ResetWisdomAsync(Token);
        var request = Request(prompt: null);
        await AddWisdomAsync("unrelated filler one", cosine: 0.9);

        var reply = await InvokeAsync(request);

        reply.Injection.ShouldBeEmpty();
        (await PromptEventCountAsync(request.SessionId)).ShouldBe(1);
    }

    private async Task<UserPromptReply> InvokeAsync(HookEventRequest request)
    {
        var recallOptions = Options.Create(new RecallOptions());
        var clock = new FakeTimeProvider(Now);
        var capture = new CaptureService(
            Context,
            new ProjectResolver(Context),
            Options.Create(new CaptureOptions()),
            clock,
            new EpisodeFeed());
        var promptRecall = new PromptRecallService(
            Context,
            new QueryRanking(
                Context,
                _embeddings,
                new WisdomSearch(Context, Options.Create(new SearchOptions())),
                recallOptions,
                clock),
            recallOptions,
            clock);
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

    private async Task<Wisdom> AddWisdomAsync(string text, double cosine)
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Fact,
            ScopeProjectId = Project.GlobalId,
            Text = text,
            Embedding = new Vector(TestVectors.WithCosine(cosine)),
            Reinforcement = 1,
            LastConfirmedAt = Now,
        };
        Context.Wisdom.Add(wisdom);
        await Context.SaveChangesAsync(Token);
        return wisdom;
    }

    private async Task<int> PromptEventCountAsync(string sessionId)
    {
        await using var context = fixture.CreateContext();
        return await context.Events.CountAsync(
            e => e.Type == EventType.UserPromptSubmit
                && context.Episodes.Any(ep => ep.Id == e.EpisodeId && ep.SessionId == sessionId),
            Token);
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
