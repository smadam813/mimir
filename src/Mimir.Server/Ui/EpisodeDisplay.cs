using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Ui;

/// <summary>
/// What one row of the Episode list marks (§8.2, #94): where the session itself is, and — once it
/// has Sealed and joined the queue — where its distillation is. <see cref="Done"/> is the resting
/// state the list marks with nothing, so the four that want a curator's eye are the four the state
/// chips filter by.
/// </summary>
public enum EpisodeState
{
    Live,
    Pending,
    Running,
    Done,
    Failed,
}

/// <summary>
/// The §8.2 surfaces' shared presentation: list rows and the drill-down must describe the
/// same Episode the same way, so the words live in one place. Pure by construction — no database
/// reaches this far, so its pins run on a machine with no Postgres.
/// </summary>
public static class EpisodeDisplay
{
    /// <summary>
    /// How many Events the drill-down's stream renders before it asks (#95). A session's stream runs
    /// to thousands of Events and the whole of it is one scroll pane, so the bound is what keeps the
    /// top of the record reachable; the control beside it says exactly what is being withheld, so a
    /// bounded stream is never mistaken for a short one.
    /// </summary>
    public const int StreamBound = 50;

    public static string Stamp(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'");

    /// <summary>Unsealed means live (or crashed, §4); a Seal always shows its reason.</summary>
    public static string StateLabel(DateTimeOffset? sealedAt, string? sealReason)
        => sealedAt is null ? "live" : $"sealed · {SealPhrase(sealReason)}";

    /// <summary>
    /// How a Seal reads, in the one place both §8.2 surfaces get it from: a Seal always carries a
    /// reason, and a row missing one says so rather than reading unsealed. The list reaches it
    /// through <see cref="StateLabel"/>; the drill-down's aside states it on a line of its own.
    /// </summary>
    public static string SealPhrase(string? sealReason) => sealReason ?? "no reason";

    /// <summary>
    /// How long the session ran, for the aside — null while it is unsealed, because a session that
    /// has not ended has no duration recorded and counting up to now would state a figure §3 never
    /// wrote. Reads in the largest unit it fills, days included: §4 crash-Seals an idle Episode
    /// only after <c>CrashSealIdleAfter</c>, so the ordinary swept session is over a day old and a
    /// three-digit hour count would be the common case rather than the odd one. Clamped at zero,
    /// because <c>started_at</c> and <c>sealed_at</c> are written by two hosts' clocks and a
    /// negative span would report their skew rather than the session.
    /// </summary>
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

    /// <summary>
    /// A configured §11 interval in the words the Distillation aside states it in. Whole hours
    /// where the value is whole, which every §11 default here is.
    /// </summary>
    public static string Hours(TimeSpan span) => $"{span.TotalHours:0.#} h";

    /// <summary>
    /// Where distillation actually is, for the aside. An unsealed Episode is in no queue at all —
    /// Sealing is what enqueues (§6), while the column reads
    /// <see cref="DistillationState.Pending"/> from the moment capture creates the row — so saying
    /// "pending" over a live session would tell a curator it is waiting on a worker that has never
    /// been offered it. Same rule <see cref="State"/> keeps for the list's mark, worded for a screen
    /// that names the column.
    /// </summary>
    public static string DistillationPhrase(DateTimeOffset? sealedAt, DistillationState distillation)
        => sealedAt is null
            ? "not queued — Sealing is what enqueues"
            : distillation.ToString().ToLowerInvariant();

    /// <summary>
    /// Why "What it produced" is empty. Read through <see cref="State"/> rather than off the
    /// Distillation column, so the drill-down and the aside beside it never describe one Episode
    /// two ways: a <see cref="EpisodeState.Failed"/> session <em>was</em> distilled and errored, and
    /// telling a curator it "has not been distilled" while the aside says <c>failed</c> and promises
    /// a re-queue is the same row disagreeing with itself. Only <see cref="EpisodeState.Done"/>
    /// means the emptiness is settled; the other two say the figure is not in yet.
    /// </summary>
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

    /// <summary>
    /// What the stream says about its own bound, or null when nothing is withheld and there is
    /// nothing to say. A curator must never take a bounded stream for the whole record, so the
    /// collapsed note names both figures.
    /// </summary>
    public static string? StreamBoundNote(int total, bool expanded)
        => total <= StreamBound ? null
            : expanded ? $"All {total:N0} Events."
            : $"The first {StreamBound} of {total:N0} Events.";

    /// <summary>
    /// The control beside that note, or null when the stream is whole. Reversible, because a
    /// curator who expanded a 3,000-Event stream to check one moment still wants the top of it back.
    /// </summary>
    public static string? StreamToggleLabel(int total, bool expanded)
        => total <= StreamBound ? null
            : expanded ? $"Show the first {StreamBound} only"
            : $"Show the remaining {total - StreamBound:N0}";

    /// <summary>The id prefix a §8.1 Provenance link anchors an Event by.</summary>
    private const string EventAnchor = "event-";

    /// <summary>
    /// The DOM id the stream puts on one Event, and the other half of the round trip
    /// <see cref="EventAnchorHref"/> writes. Both live here with <see cref="AnchoredEvent"/> because
    /// the three used to be spelled independently in three files: a link-writer, a stream, and this
    /// reader. A mismatch between them does not fail to build — the reader simply returns null and
    /// the §8.1 link quietly stops opening the stream at its Event.
    /// </summary>
    public static string EventAnchorId(Guid eventId) => $"{EventAnchor}{eventId}";

    /// <summary>The fragment a §8.1 Provenance link appends to reach that Event.</summary>
    public static string EventAnchorHref(Guid eventId) => $"#{EventAnchorId(eventId)}";

    /// <summary>
    /// The Event a URL anchors, or null where it names none. §8.1's Provenance links land here as
    /// <c>projects/{p}/episodes/{e}#event-{eventId}</c>, which is the only fragment this surface
    /// writes an id into.
    /// </summary>
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

    /// <summary>
    /// Whether the bound has to give way for the URL's anchor: a Provenance link opens the Episode
    /// <em>at the Event itself</em> (§8.1), so an anchor the bound would withhold has to open the
    /// stream whole or the link lands on an element that is not in the page at all. An anchor
    /// inside the bound changes nothing, and neither does a URL carrying none.
    /// </summary>
    public static bool AnchorIsPastTheBound(IEnumerable<Guid> eventIds, string uri)
        => AnchoredEvent(uri) is { } anchored && eventIds.Skip(StreamBound).Contains(anchored);

    /// <summary>
    /// An unsealed Episode is live whatever its Distillation column says: Sealing is what enqueues
    /// (§6), while the column reads <see cref="DistillationState.Pending"/> from the moment capture
    /// creates the row — so reading the column first would mark every live session "pending".
    /// </summary>
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

    /// <summary>The word the row and the chips both carry; <see cref="EpisodeState.Done"/> has none.</summary>
    public static string? StateWord(EpisodeState state) => state switch
    {
        EpisodeState.Live => "live",
        EpisodeState.Pending => "pending",
        EpisodeState.Running => "running",
        EpisodeState.Failed => "failed",
        _ => null,
    };

    /// <summary>
    /// The row's second line: where the session ran, how it ended, and what it produced —
    /// <c>~/src/mimir · sealed · clear · 2 Wisdom</c>. A Failed Episode says the sweep will re-queue
    /// it, because "failed" alone reads terminal when §6 makes it nothing of the kind. A distilled
    /// Episode that produced nothing says <c>no Wisdom</c> in words: a quiet session and a session
    /// whose figure is simply absent would otherwise read alike. Before <c>done</c> there is no
    /// figure to state — a live or queued Episode has not been distilled yet. A live Episode has
    /// not ended and has produced nothing yet, so it says only where it is running.
    /// </summary>
    public static string MetaLine(EpisodeSummary episode)
    {
        var parts = new List<string>(4) { episode.Cwd };

        // Worded by StateLabel, so a list row and the drill-down describe one Seal the same way.
        // Its live half is deliberately unused here: the row's own state mark already says "live",
        // and "unsealed" alongside it would be a second word for a fact the row states once.
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

    /// <summary>
    /// What one Event was, in words rather than the hook's own name — a curator reading a Wisdom's
    /// Provenance (§8.1) is being told where a lesson came from, and <c>PostToolUse</c> is the name
    /// of a hook, not of a moment. The four words are CONTEXT.md's own, from the Event entry:
    /// "a prompt, tool activity, an assistant message, or a deliberate save".
    /// </summary>
    public static string EventWord(EventType type) => type switch
    {
        EventType.UserPromptSubmit => "a prompt",
        EventType.PostToolUse => "tool activity",
        EventType.Stop => "an assistant message",
        _ => "a deliberate save",
    };
}
