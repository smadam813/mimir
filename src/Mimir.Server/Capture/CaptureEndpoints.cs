using Mimir.Contracts.Hooks;
using Mimir.Server.Distillation;
using Mimir.Server.Harvest;
using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Capture;

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
                harvestTrigger.Request();
                distillationTrigger.Request();
                return Results.Accepted();

            default:
                return Results.BadRequest($"Unknown capture event '{request.HookEvent}'.");
        }
    }

    public static async Task<UserPromptReply> UserPromptAsync(
        HookEventRequest request,
        CaptureService capture,
        PromptRecallService promptRecall,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var episode = await capture.ResumeEpisodeAsync(request, cancellationToken);
        await capture.AppendEventAsync(episode, request, EventType.UserPromptSubmit, cancellationToken);

        var injection = "";
        if (request.Payload.StringProperty("prompt") is { } prompt && !string.IsNullOrWhiteSpace(prompt))
        {
            try
            {
                injection = await promptRecall.ComposeInjectionAsync(
                    episode.SessionId, episode.ProjectId, prompt, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                loggerFactory.CreateLogger(typeof(CaptureEndpoints))
                    .LogWarning(ex, "Prompt-lane recall failed; injecting nothing (fail open, §7).");
            }
        }

        return new UserPromptReply { Injection = injection };
    }

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
