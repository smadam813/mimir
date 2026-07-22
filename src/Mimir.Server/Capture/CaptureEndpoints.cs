using Mimir.Contracts.Hooks;
using Mimir.Server.Distillation;
using Mimir.Server.Harvest;
using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Capture;

/// <summary>
/// The HTTP surface behind <c>mimir hook</c> (spec §4). The async capture POSTs land on
/// <c>/api/capture/events</c>; the two synchronous hooks get their own routes because they answer
/// with content to print — SessionStart the Brief, UserPromptSubmit its lane's injection (empty
/// until the Prompt-lane ticket).
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
    /// Creates or resumes the session's Episode and answers with the Brief (§7). The
    /// <c>source: "compact"</c> re-fire arrives here identically — the hook carries the same
    /// session id, so it resumes the Episode and gets a fresh Brief.
    /// </summary>
    public static async Task<SessionStartReply> SessionStartAsync(
        HookEventRequest request,
        CaptureService capture,
        BriefService brief,
        CancellationToken cancellationToken)
    {
        var episode = await capture.ResumeEpisodeAsync(request, cancellationToken);
        return new SessionStartReply
        {
            Brief = await brief.ComposeBriefAsync(
                episode.SessionId, episode.ProjectId, cancellationToken),
        };
    }
}
