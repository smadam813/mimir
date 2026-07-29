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
    public static string Stamp(DateTimeOffset at)
        => at.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'");

    /// <summary>Unsealed means live (or crashed, §4); a Seal always shows its reason.</summary>
    public static string StateLabel(DateTimeOffset? sealedAt, string? sealReason)
        => sealedAt is null ? "live" : $"sealed · {SealPhrase(sealReason)}";

    /// <summary>
    /// How a Seal reads, in the one place both §8.2 surfaces get it from: a Seal always carries a
    /// reason, and a row missing one says so rather than reading unsealed.
    /// </summary>
    private static string SealPhrase(string? sealReason) => sealReason ?? "no reason";

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
