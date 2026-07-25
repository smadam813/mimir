using System.Globalization;

namespace Mimir.Server.Recall;

/// <summary>
/// The Brief's growth tripwire (issue [#72]): every composition re-measures itself, and one that
/// crosses either threshold fires on both channels — a warning log for whoever is watching the
/// server, and a line appended inside the Brief for whoever is not.
/// </summary>
/// <remarks>
/// The second channel is the point. The ambient Candidate Universe grows monotonically by design
/// (the recency floor keeps every row alive; Retire is deliberate, never automatic), and the §11
/// hook cap it grows towards degrades to an empty Brief and exit 0 — a session cannot tell that
/// apart from Mimir having nothing to say. So the warning goes where a session is guaranteed to
/// look: into the Brief itself, the one recall surface every session reads. That is a deliberate
/// purity violation, admitted in the glossary's Brief entry, and it is the only non-Wisdom content
/// any recall surface volunteers.
/// </remarks>
internal static class BriefTripwire
{
    /// <summary>
    /// Wall-clock threshold. Deliberately below the §11 cap: past that the hook prints nothing at
    /// all, so a warning armed at the cliff would only ever reach a Brief nobody receives.
    /// </summary>
    public static readonly TimeSpan ComposeWarnAfter = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Size threshold, so the warning arrives on a fast machine too — wall time on the day is a
    /// function of hardware, but the corpus that will eventually outgrow any hardware is not.
    /// </summary>
    public const int CandidateWarnAbove = 25_000;

    /// <summary>
    /// The §11 hook round-trip cap, quoted in the line so its seconds mean something to whoever
    /// reads them. Restated from <c>HookCommand.Cap</c>, which lives in the CLI and cannot be
    /// referenced from here; drift makes the sentence stale, never the thresholds — neither
    /// threshold is derived from it.
    /// </summary>
    private const string HookCap = "3s";

    /// <summary>
    /// Both channels or neither: a composition inside both thresholds logs nothing and leaves the
    /// Brief byte-for-byte what it would otherwise have been.
    /// </summary>
    /// <param name="elapsed">Wall time spent listing, hydrating and scoring — everything that
    /// grows with the corpus.</param>
    /// <param name="candidates">How large the ambient Candidate Universe was this time.</param>
    /// <returns>The line to append inside the Brief, or null when neither threshold was crossed.</returns>
    public static string? Fire(ILogger logger, TimeSpan elapsed, int candidates)
    {
        if (elapsed < ComposeWarnAfter && candidates <= CandidateWarnAbove)
        {
            return null;
        }

        var seconds = elapsed.TotalSeconds;
        logger.LogWarning(
            "Brief composed in {Seconds:F1}s (hook cap {HookCap}) over an ambient set of "
            + "{Candidates} rows; see issue #72.",
            seconds,
            HookCap,
            candidates);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"⚠ Mimir: Brief composed in {seconds:F1}s (budget {HookCap}); "
            + $"ambient set {candidates:N0} rows — see #72.\n");
    }
}
