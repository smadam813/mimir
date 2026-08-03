using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Contracts.Hooks;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Harvest;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// The shared fire-and-forget route, driven the way <c>UserPromptEndpointTests</c> drives its own:
/// the endpoint method with a real <c>CaptureService</c> and hand-wired collaborators. Postgres-
/// backed, and so a separate class from <c>CaptureEndpointsTests</c>, which stays SQL-free for the
/// one arm that refuses before touching anything.
/// </summary>
public sealed class CaptureEventEndpointTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task SessionEnd_PokesBothWorkersExactlyOnce_AndOnlyOnceSealed()
    {
        var request = Request(HookEvents.SessionEnd);
        var (harvest, distillation) = (Trigger(request.SessionId), Trigger(request.SessionId));

        var result = await CaptureEndpoints.CaptureEventAsync(
            request, CreateCapture(), harvest, distillation, Token);

        result.ShouldBeOfType<Accepted>("sealing answers the hook, it does not report on the workers");
        harvest.Pokes.ShouldBe([true], "the §5 scan is asked for once, and the seal is already durable");
        distillation.Pokes.ShouldBe([true], "and the §6 sweep once, off the same finished seal");
        (await SealedAtAsync(request.SessionId)).ShouldBe(Now);
    }

    /// <summary>
    /// The other side of the same rule: the fan-out belongs to the one event that ends an Episode,
    /// so hoisting either poke out of that arm and up beside the shared <c>Accepted</c> would wake
    /// both workers on every tool call of every live session.
    /// </summary>
    [Fact]
    public async Task AMidSessionEvent_IsRecorded_AndPokesNobody()
    {
        var request = Request(HookEvents.Stop);
        var (harvest, distillation) = (Trigger(request.SessionId), Trigger(request.SessionId));

        var result = await CaptureEndpoints.CaptureEventAsync(
            request, CreateCapture(), harvest, distillation, Token);

        result.ShouldBeOfType<Accepted>();
        harvest.Pokes.ShouldBeEmpty();
        distillation.Pokes.ShouldBeEmpty();
        (await FromDb(db => db.Events.CountAsync(e => e.Type == EventType.Stop, Token))).ShouldBe(1);
        (await SealedAtAsync(request.SessionId)).ShouldBeNull();
    }

    private CaptureService CreateCapture()
        => new(
            Context,
            new ProjectResolver(Context),
            Options.Create(new CaptureOptions()),
            Clock,
            new EpisodeFeed());

    /// <summary>
    /// A trigger that answers what the database looked like at the instant it was poked. On its own
    /// context, because the seal is an <c>ExecuteUpdate</c> the caller's tracked Episode never sees.
    /// </summary>
    private RecordingTrigger Trigger(string sessionId)
        => new(() =>
        {
            using var probe = CreateContext();
            return probe.Episodes.Any(e => e.SessionId == sessionId && e.SealedAt != null);
        });

    private async Task<DateTimeOffset?> SealedAtAsync(string sessionId)
        => (await FromDb(db => db.Episodes.SingleAsync(e => e.SessionId == sessionId, Token))).SealedAt;

    private static HookEventRequest Request(string hookEvent)
    {
        using var document = JsonDocument.Parse("""{"reason":"clear"}""");
        var suffix = Guid.NewGuid().ToString("N");
        return new HookEventRequest
        {
            SessionId = $"sess-{suffix}",
            Cwd = $@"C:\git\capture-event-{suffix}",
            ProjectIdentity = $"github.com/test/capture-event-{suffix}",
            ProjectRoot = $@"C:\git\capture-event-{suffix}",
            HookEvent = hookEvent,
            Payload = document.RootElement.Clone(),
        };
    }

    /// <summary>
    /// One class for both workers because the two interfaces are the same two members; the test
    /// hands out an instance each, which is what keeps the two arms of the fan-out distinguishable.
    /// </summary>
    private sealed class RecordingTrigger(Func<bool> sealedAlready) : IHarvestScanTrigger, IDistillationTrigger
    {
        private readonly List<bool> _pokes = [];

        /// <summary>
        /// One entry per <see cref="Request"/>, each saying whether the Episode was already sealed
        /// when that poke arrived — so a count and an ordering are one assertion.
        /// </summary>
        public IReadOnlyList<bool> Pokes => _pokes;

        public void Request() => _pokes.Add(sealedAlready());

        public Task WaitAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException("no hook waits on a worker; the poke is the whole contract");
    }
}
