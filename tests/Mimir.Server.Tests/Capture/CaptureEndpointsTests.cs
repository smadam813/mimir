using System.Text.Json;
using Mimir.Contracts.Hooks;
using Mimir.Server.Capture;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// The §4 hook router's one answer that needs no database: an unrecognised hook event is refused
/// before anything downstream is touched. Deliberately not <c>PostgresTestBase</c> — this issues
/// no SQL, so a skip-gated context would hide the guard on a machine without Docker.
/// </summary>
public sealed class CaptureEndpointsTests
{
    [Fact]
    public async Task AnUnknownHookEvent_IsRefused_WithoutReachingAnyService()
    {
        // The nulls are the assertion: the 400 arm must return before it touches the capture
        // service or either trigger. Reorder the switch so an unknown event falls through to a
        // handler and this dies on a NullReferenceException rather than quietly accepting.
        var result = await CaptureEndpoints.CaptureEventAsync(
            Request("NotAHook"), capture: null!, harvestTrigger: null!, distillationTrigger: null!,
            TestContext.Current.CancellationToken);

        var badRequest = result.ShouldBeOfType<
            Microsoft.AspNetCore.Http.HttpResults.BadRequest<string>>();
        badRequest.Value.ShouldNotBeNull().ShouldContain("NotAHook");
    }

    private static HookEventRequest Request(string hookEvent)
    {
        using var document = JsonDocument.Parse("{}");
        var suffix = Guid.NewGuid().ToString("N");
        return new HookEventRequest
        {
            SessionId = $"sess-{suffix}",
            Cwd = $@"C:\git\router-{suffix}",
            ProjectIdentity = $"github.com/test/router-{suffix}",
            ProjectRoot = $@"C:\git\router-{suffix}",
            HookEvent = hookEvent,
            Payload = document.RootElement.Clone(),
        };
    }
}
