using Mimir.Contracts.Hooks;
using Mimir.Server.Distillation;
using Mimir.Server.Harvest;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Capture;

/// <summary>
/// The HTTP surface behind <c>mimir hook</c> (spec §4). The async capture POSTs land on
/// <c>/api/capture/events</c>; the two synchronous hooks get their own routes because they answer
/// with content to print. Their replies are empty until the Recall tickets fill them in.
/// </summary>
internal static class CaptureEndpoints
{
    public static async Task<IResult> CaptureEventAsync(
        HookEventRequest request,
        CaptureService capture,
        IHarvestScanTrigger harvestTrigger,
        IDistillationTrigger distillationTrigger,
        CancellationToken cancellationToken)
    {
        switch (request.HookEvent)
        {
            case HookEvents.PostToolUse:
                await capture.AppendEventAsync(request, EventType.PostToolUse, cancellationToken);
                return Results.Accepted();

            case HookEvents.Stop:
                await capture.AppendEventAsync(request, EventType.Stop, cancellationToken);
                return Results.Accepted();

            case HookEvents.SessionEnd:
                await capture.SealEpisodeAsync(request, cancellationToken);
                // §5: every SessionEnd asks the Harvester for an opportunistic scan — the session
                // may just have written auto-memory. §6: Sealing queued the Episode, so the
                // Distiller is worth waking too. Fire-and-forget; sealing never waits on either.
                harvestTrigger.Request();
                distillationTrigger.Request();
                return Results.Accepted();

            default:
                // The §3 Event enum is closed; guessing at an unknown hook would corrupt it.
                return Results.BadRequest($"Unknown capture event '{request.HookEvent}'.");
        }
    }

    /// <summary>
    /// The single §4 round-trip: record the prompt Event and answer with any Prompt-lane
    /// injection. The injection stays empty until the Prompt-lane ticket.
    /// </summary>
    public static async Task<UserPromptReply> UserPromptAsync(
        HookEventRequest request,
        CaptureService capture,
        CancellationToken cancellationToken)
    {
        await capture.AppendEventAsync(request, EventType.UserPromptSubmit, cancellationToken);
        return new UserPromptReply();
    }

    /// <summary>
    /// Creates or resumes the session's Episode. The Brief stays empty until its ticket (§7).
    /// </summary>
    public static async Task<SessionStartReply> SessionStartAsync(
        HookEventRequest request,
        CaptureService capture,
        CancellationToken cancellationToken)
    {
        await capture.ResumeEpisodeAsync(request, cancellationToken);
        return new SessionStartReply();
    }
}
