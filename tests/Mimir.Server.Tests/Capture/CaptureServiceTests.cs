using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Mimir.Contracts.Hooks;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// Spec §4 against a real Postgres: a session's hooks become one Episode with ordered Events,
/// truncated payloads, and a Seal carrying the SessionEnd reason (ADR-0003 — the session is the
/// Episode; session end is not an Event).
/// </summary>
public sealed class CaptureServiceTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly EpisodeFeed _feed = new();

    private readonly List<EpisodeChange> _announced = [];

    private MimirDbContext? _context;

    public ValueTask InitializeAsync()
    {
        _feed.Subscribe(_announced.Add);
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
    public async Task SessionStart_CreatesALiveEpisodeOnItsProject()
    {
        var request = Request();

        var episode = await Service().ResumeEpisodeAsync(request, Token);

        episode.SessionId.ShouldBe(request.SessionId);
        episode.Cwd.ShouldBe(request.Cwd);
        episode.StartedAt.ShouldBe(Now);
        episode.SealedAt.ShouldBeNull();
        episode.SealReason.ShouldBeNull();
        episode.Distillation.ShouldBe(DistillationState.Pending);

        var project = await FromDb(db => db.Projects.SingleAsync(p => p.Id == episode.ProjectId, Token));
        project.Identity.ShouldBe(request.ProjectIdentity);
    }

    [Fact]
    public async Task SessionStartTwice_ResumesTheSameEpisode()
    {
        var request = Request();

        var first = await Service().ResumeEpisodeAsync(request, Token);
        var second = await Service().ResumeEpisodeAsync(request, Token);

        second.Id.ShouldBe(first.Id);
        (await FromDb(db => db.Episodes.CountAsync(e => e.SessionId == request.SessionId, Token))).ShouldBe(1);
    }

    [Fact]
    public async Task Events_ArriveInSequenceOnTheSessionsEpisode()
    {
        var request = Request(payload: new { prompt = "how do I deploy?" });

        var prompt = await Service().AppendEventAsync(request, EventType.UserPromptSubmit, Token);
        var stop = await Service().AppendEventAsync(request, EventType.Stop, Token);

        prompt.Seq.ShouldBe(1);
        stop.Seq.ShouldBe(2);
        stop.EpisodeId.ShouldBe(prompt.EpisodeId);
        prompt.Type.ShouldBe(EventType.UserPromptSubmit);
        prompt.At.ShouldBe(Now);
        prompt.Salient.ShouldBeFalse("only Remember Events are explicitly salient (§3)");
    }

    [Fact]
    public async Task AnEventForAnUnseenSession_CreatesTheEpisodeOnDemand()
    {
        // Async hooks can outrun SessionStart; capture never drops what it can attach (§4).
        var request = Request();

        var evt = await Service().AppendEventAsync(request, EventType.PostToolUse, Token);

        var episode = await FromDb(db => db.Episodes.SingleAsync(e => e.SessionId == request.SessionId, Token));
        evt.EpisodeId.ShouldBe(episode.Id);
    }

    [Fact]
    public async Task OversizedPayloads_AreStoredTruncatedWithTheirOriginalSize()
    {
        var request = Request(payload: new { tool_response = new string('a', 5000) });

        var evt = await Service().AppendEventAsync(request, EventType.PostToolUse, Token);

        var stored = await FromDb(db => db.Events.SingleAsync(e => e.Id == evt.Id, Token));
        stored.Payload.ShouldContain("…[truncated 904 bytes]…");
        stored.PayloadFullSize.ShouldBe(JsonSerializer.Serialize(
            new { tool_response = new string('a', 5000) }).Length);
    }

    [Fact]
    public async Task PostgresGeneratesTheSearchVector_OverThePayloadText()
    {
        var request = Request(payload: new { prompt = "the kubernetes ingress misroutes traffic" });

        var evt = await Service().AppendEventAsync(request, EventType.UserPromptSubmit, Token);

        var matches = await FromDb(db => db.Events
            .Where(e => e.Id == evt.Id && e.Tsv!.Matches(EF.Functions.ToTsQuery("english", "kubernetes")))
            .CountAsync(Token));
        matches.ShouldBe(1, "the tsv generated column is the Episode FTS leg of mimir_search (§3)");
    }

    [Fact]
    public async Task SessionEnd_SealsWithTheHookReportedReason()
    {
        var request = Request();
        await Service().ResumeEpisodeAsync(request, Token);

        await Service().SealEpisodeAsync(Request(request.SessionId, new { reason = "exit" }), Token);

        var episode = await FromDb(db => db.Episodes.SingleAsync(e => e.SessionId == request.SessionId, Token));
        episode.SealedAt.ShouldBe(Now);
        episode.SealReason.ShouldBe("exit");
    }

    [Fact]
    public async Task SessionEndForAnUnseenSession_StillLeavesASealedEpisode()
    {
        var request = Request(payload: new { reason = "other" });

        await Service().SealEpisodeAsync(request, Token);

        var episode = await FromDb(db => db.Episodes.SingleAsync(e => e.SessionId == request.SessionId, Token));
        episode.SealedAt.ShouldBe(Now);
        episode.SealReason.ShouldBe("other");
    }

    [Fact]
    public async Task TheFirstSealWins_ALateDuplicateChangesNothing()
    {
        var request = Request(payload: new { reason = "exit" });
        await Service().SealEpisodeAsync(request, Token);

        await Service().SealEpisodeAsync(Request(request.SessionId, new { reason = "late" }), Token);

        var episode = await FromDb(db => db.Episodes.SingleAsync(e => e.SessionId == request.SessionId, Token));
        episode.SealReason.ShouldBe("exit");
    }

    [Fact]
    public async Task ASealFromAnotherContext_BeatsAStaleDuplicate()
    {
        // First-seal-wins under real concurrency: this service still tracks the Episode as
        // unsealed while another request seals it. The stale duplicate must update nothing.
        var request = Request();
        var service = Service();
        await service.ResumeEpisodeAsync(request, Token);

        await using (var other = fixture.CreateContext())
        {
            var raced = await other.Episodes.SingleAsync(e => e.SessionId == request.SessionId, Token);
            raced.SealedAt = Now.AddMinutes(-1);
            raced.SealReason = "exit";
            await other.SaveChangesAsync(Token);
        }

        await service.SealEpisodeAsync(Request(request.SessionId, new { reason = "late" }), Token);

        var persisted = await FromDb(db => db.Episodes.SingleAsync(e => e.SessionId == request.SessionId, Token));
        persisted.SealReason.ShouldBe("exit");
        persisted.SealedAt.ShouldBe(Now.AddMinutes(-1));
    }

    [Fact]
    public async Task AStragglerEventAfterTheSeal_IsStillCaptured()
    {
        // PostToolUse is fire-and-forget (§4): it can land after SessionEnd. Losing it would
        // contradict capture-is-dumb; it belongs to the Episode it names.
        var request = Request();
        await Service().SealEpisodeAsync(Request(request.SessionId, new { reason = "exit" }), Token);

        var evt = await Service().AppendEventAsync(request, EventType.PostToolUse, Token);

        evt.Seq.ShouldBe(1);
    }

    // Spec §8.2's timeline is live because every committed capture write is announced on the
    // feed; a write that changed nothing stays quiet so circuits never re-query for no reason.

    [Fact]
    public async Task ANewSessionsEpisode_IsAnnouncedOnTheFeed()
    {
        var request = Request();

        var episode = await Service().ResumeEpisodeAsync(request, Token);

        _announced.ShouldBe([new EpisodeChange(episode.ProjectId, episode.Id)]);
    }

    [Fact]
    public async Task ResumingAnExistingSession_StaysQuietOnTheFeed()
    {
        var request = Request();
        await Service().ResumeEpisodeAsync(request, Token);
        _announced.Clear();

        await Service().ResumeEpisodeAsync(request, Token);

        _announced.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnAppendedEvent_IsAnnouncedOnTheFeed()
    {
        var request = Request();
        await Service().ResumeEpisodeAsync(request, Token);
        _announced.Clear();

        var evt = await Service().AppendEventAsync(request, EventType.Stop, Token);

        var episode = await FromDb(db => db.Episodes.SingleAsync(e => e.Id == evt.EpisodeId, Token));
        _announced.ShouldBe([new EpisodeChange(episode.ProjectId, episode.Id)]);
    }

    [Fact]
    public async Task ASeal_IsAnnouncedOnce_AndALateDuplicateStaysQuiet()
    {
        var request = Request(payload: new { reason = "exit" });
        var episode = await Service().ResumeEpisodeAsync(request, Token);
        _announced.Clear();

        await Service().SealEpisodeAsync(request, Token);
        await Service().SealEpisodeAsync(Request(request.SessionId, new { reason = "late" }), Token);

        _announced.ShouldBe([new EpisodeChange(episode.ProjectId, episode.Id)]);
    }

    [Fact]
    public async Task SessionsInTwoClonesOfOneRepo_EndUpUnderOneProjectWithBothRoots()
    {
        // The #17 demo: a session in clone A before its remote is known (path identity), a
        // session in clone B that reports the remote, then hook traffic from clone A learning the
        // remote too. Identity follows the repository (§3.1) — one Project, both roots, every
        // Episode attached to it.
        var suffix = Guid.NewGuid().ToString("N");
        var remote = $"github.com/test/demo-{suffix}";
        var rootA = $@"C:\git\demo-{suffix}";
        var rootB = $@"D:\work\demo-{suffix}";

        var inCloneA = await Service().ResumeEpisodeAsync(Request(identity: rootA, root: rootA), Token);
        var inCloneB = await Service().ResumeEpisodeAsync(Request(identity: remote, root: rootB), Token);
        var backInCloneA = await Service().ResumeEpisodeAsync(Request(identity: remote, root: rootA), Token);

        var project = await FromDb(db => db.Projects.SingleAsync(p => p.Identity == remote, Token));
        project.RootPaths.ShouldBe([rootB, rootA]);
        var episodeProjects = await FromDb(db => db.Episodes
            .Where(e => new[] { inCloneA.Id, inCloneB.Id, backInCloneA.Id }.Contains(e.Id))
            .Select(e => e.ProjectId)
            .Distinct()
            .ToListAsync(Token));
        episodeProjects.ShouldBe([project.Id], "two clones of one repository are one Project");
    }

    private CaptureService Service()
        => new(
            Context,
            new ProjectResolver(Context),
            Options.Create(new CaptureOptions()),
            new FakeTimeProvider(Now),
            _feed);

    /// <summary>
    /// A request for a fresh session unless one is named. Identity and root are unique per call:
    /// the database is shared by the whole class, and §3.1 root-matching would otherwise weld
    /// every test's session onto the first test's Project.
    /// </summary>
    private static HookEventRequest Request(
        string? sessionId = null,
        object? payload = null,
        string? identity = null,
        string? root = null)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload ?? new { }));
        var suffix = Guid.NewGuid().ToString("N");
        return new HookEventRequest
        {
            SessionId = sessionId ?? $"sess-{suffix}",
            Cwd = root ?? $@"C:\git\capture-{suffix}",
            ProjectIdentity = identity ?? $"github.com/test/capture-{suffix}",
            ProjectRoot = root ?? $@"C:\git\capture-{suffix}",
            HookEvent = HookEvents.PostToolUse,
            Payload = document.RootElement.Clone(),
        };
    }

    /// <summary>
    /// Reads back through a separate context, so assertions see what Postgres persisted rather
    /// than the entities the service still tracks.
    /// </summary>
    private async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var context = fixture.CreateContext();
        return await query(context);
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
