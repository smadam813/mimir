using Microsoft.AspNetCore.WebUtilities;
using Mimir.Server.Ui;

namespace Mimir.Server.Components.Wisdom;

/// <summary>
/// The one place the §8.1 surface's URLs are spelled and read back: the two routes
/// <c>WisdomPage</c> serves, and the lens the sidebar's "Needs attention" links carry into them.
/// The lens names are the enum's own, lowercased, so adding a lens needs no table here; an unknown
/// or missing value lands on <see cref="WisdomLens.Active"/>, the default listing, rather than
/// erroring.
/// </summary>
internal static class WisdomRoute
{
    /// <summary>The lens query-string key: <c>projects/{id}/wisdom?show=retired</c>.</summary>
    internal const string LensName = "show";

    /// <summary>The listing route — the surface with nothing selected.</summary>
    internal static string Listing(Guid projectId, WisdomLens lens)
        => $"projects/{projectId}/wisdom{LensSuffix(lens)}";

    /// <summary>
    /// The detail route. The Project is the one whose universe is being read, never the Wisdom's
    /// own Scope: a Global row read from <c>mimir</c>'s universe belongs to <c>mimir</c>'s list,
    /// and following it must not silently switch the curator to Global's.
    /// </summary>
    internal static string Detail(Guid projectId, Guid wisdomId, WisdomLens lens)
        => $"projects/{projectId}/wisdom/{wisdomId}{LensSuffix(lens)}";

    internal static WisdomLens ParseLens(string? value)
        => Enum.TryParse<WisdomLens>(value, ignoreCase: true, out var lens) ? lens : WisdomLens.Active;

    /// <summary>
    /// The lens a URL is showing. The page itself gets this from Blazor's own query binding; the
    /// sidebar has no route of its own and reads the address bar, the way it already reads the
    /// Project and tab out of it (#89).
    /// </summary>
    internal static WisdomLens LensOf(Uri uri)
        => ParseLens(QueryHelpers.ParseQuery(uri.Query)
            .TryGetValue(LensName, out var values) ? values.ToString() : null);

    /// <summary>The default lens needs no query at all, so an ordinary listing URL stays clean.</summary>
    private static string LensSuffix(WisdomLens lens)
        => lens == WisdomLens.Active ? "" : $"?{LensName}={lens.ToString().ToLowerInvariant()}";
}
