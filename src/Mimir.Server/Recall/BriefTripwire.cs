using System.Globalization;
using Mimir.Contracts.Hooks;

namespace Mimir.Server.Recall;

internal static class BriefTripwire
{
    public static readonly TimeSpan ComposeWarnAfter = TimeSpan.FromSeconds(1);

    public const int CandidateWarnAbove = 25_000;

    private static readonly string HookCap = string.Create(
        CultureInfo.InvariantCulture,
        $"{HookLimits.RoundTripCap.TotalSeconds:0.#}s");

    public static string? Fire(ILogger logger, TimeSpan elapsed, int candidates)
    {
        if (elapsed <= ComposeWarnAfter && candidates <= CandidateWarnAbove)
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
