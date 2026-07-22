using System.Text.Json;
using Mimir.Contracts.Hooks;
using Mimir.Server.Distillation;
using Mimir.Server.Harvest;
using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Capture;

/// <summary>
/// The HTTP surface behind <c>mimir hook</c> (spec §4). The async capture POSTs land on
/// <c>/api/capture/events</c>; the two synchronous hooks get their own routes because they answer
/// with content to print — SessionStart the Brief, UserPromptSubmit the Prompt lane's injection.
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
    /// injection (§7). Recall fails open — a capture that succeeded answers with an empty
    /// injection rather than an error, because memory must never break a session.
    /// </summary>
    public static async Task<UserPromptReply> UserPromptAsync(
        HookEventRequest request,
        CaptureService capture,
        PromptRecallService promptRecall,
        ILogger<PromptRecallService> logger,
        CancellationToken cancellationToken)
    {
        var episode = await capture.ResumeEpisodeAsync(request, cancellationToken);
        await capture.AppendEventAsync(request, EventType.UserPromptSubmit, cancellationToken);

        var injection = "";
        if (PromptOf(request.Payload) is { } prompt && !string.IsNullOrWhiteSpace(prompt))
        {
            try
            {
                injection = await promptRecall.ComposeInjectionAsync(
                    episode.SessionId, episode.ProjectId, prompt, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Prompt-lane recall failed; injecting nothing (fail open, §7).");
            }
        }

        return new UserPromptReply { Injection = injection };
    }

    /// <summary>The prompt text from the hook's stdin JSON, or null when absent.</summary>
    private static string? PromptOf(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("prompt", out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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
