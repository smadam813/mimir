using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

public enum EpisodeState
{
    Live,
    Pending,
    Running,
    Done,
    Failed,
}

public static class EpisodeDisplay
{
    public const int StreamBound = 50;

    public static string Stamp(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'");

    public static string StateLabel(DateTimeOffset? sealedAt, string? sealReason)
        => sealedAt is null ? "live" : $"sealed · {SealPhrase(sealReason)}";

    public static string SealPhrase(string? sealReason) => sealReason ?? "no reason";

    public static string? Duration(DateTimeOffset startedAt, DateTimeOffset? sealedAt)
    {
        if (sealedAt is not { } ended)
        {
            return null;
        }

        var span = ended - startedAt;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours:00}h"
            : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes:00}m"
            : span.TotalMinutes >= 1 ? $"{span.Minutes}m"
            : $"{span.Seconds}s";
    }

    public static string Hours(TimeSpan span) => $"{span.TotalHours:0.#} h";

    public static string DistillationPhrase(DateTimeOffset? sealedAt, DistillationState distillation)
        => sealedAt is null
            ? "not queued — Sealing is what enqueues"
            : distillation.ToString().ToLowerInvariant();

    public static string NothingProducedNote(DateTimeOffset? sealedAt, DistillationState distillation)
        => State(sealedAt, distillation) switch
        {
            EpisodeState.Done =>
                "Distillation drew no durable memory from this session — §6 prefers no candidate"
                + " over a weak one, so a quiet Episode is the ordinary outcome.",
            EpisodeState.Failed =>
                "Distillation was attempted on this session and failed, so nothing was admitted."
                + " §6 parks a failure rather than dropping it: the sweep re-queues it, and this is"
                + " not the last word on what the session produced.",
            _ =>
                "Nothing yet: this Episode has not been distilled, so no Wisdom carries it as"
                + " Provenance.",
        };

    public static string? StreamBoundNote(int total, bool expanded)
        => total <= StreamBound ? null
            : expanded ? $"All {total:N0} Events."
            : $"The first {StreamBound} of {total:N0} Events.";

    public static string? StreamToggleLabel(int total, bool expanded)
        => total <= StreamBound ? null
            : expanded ? $"Show the first {StreamBound} only"
            : $"Show the remaining {total - StreamBound:N0}";

    private const string EventAnchor = "event-";

    public static string EventAnchorId(Guid eventId) => $"{EventAnchor}{eventId}";

    public static string EventAnchorHref(Guid eventId) => $"#{EventAnchorId(eventId)}";

    public static Guid? AnchoredEvent(string uri)
    {
        var hash = uri.LastIndexOf('#');
        if (hash < 0)
        {
            return null;
        }

        var fragment = uri[(hash + 1)..];
        return fragment.StartsWith(EventAnchor, StringComparison.Ordinal)
            && Guid.TryParse(fragment[EventAnchor.Length..], out var eventId)
                ? eventId
                : null;
    }

    public static bool AnchorIsPastTheBound(IEnumerable<Guid> eventIds, string uri)
        => AnchoredEvent(uri) is { } anchored && eventIds.Skip(StreamBound).Contains(anchored);

    public static EpisodeState State(DateTimeOffset? sealedAt, DistillationState distillation)
        => sealedAt is null
            ? EpisodeState.Live
            : distillation switch
            {
                DistillationState.Running => EpisodeState.Running,
                DistillationState.Done => EpisodeState.Done,
                DistillationState.Failed => EpisodeState.Failed,
                _ => EpisodeState.Pending,
            };

    public static string? StateWord(EpisodeState state) => state switch
    {
        EpisodeState.Live => "live",
        EpisodeState.Pending => "pending",
        EpisodeState.Running => "running",
        EpisodeState.Failed => "failed",
        _ => null,
    };

    public static string MetaLine(EpisodeSummary episode)
    {
        var parts = new List<string>(4) { episode.Cwd };

        if (episode.SealedAt is not null)
        {
            parts.Add(StateLabel(episode.SealedAt, episode.SealReason));
        }

        if (episode.WisdomCount > 0)
        {
            parts.Add($"{episode.WisdomCount} Wisdom");
        }
        else if (episode.State == EpisodeState.Done)
        {
            parts.Add("no Wisdom");
        }

        if (episode.State == EpisodeState.Failed)
        {
            parts.Add("re-queued next sweep");
        }

        return string.Join(" · ", parts);
    }

    public static string EventsLabel(int count) => count == 1 ? "1 Event" : $"{count} Events";

    public static string EventWord(EventType type) => type switch
    {
        EventType.UserPromptSubmit => "a prompt",
        EventType.PostToolUse => "tool activity",
        EventType.Stop => "an assistant message",
        _ => "a deliberate save",
    };
}
