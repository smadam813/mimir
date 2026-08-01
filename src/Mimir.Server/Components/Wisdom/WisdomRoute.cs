using Microsoft.AspNetCore.WebUtilities;
using Mimir.Server.Ui;

namespace Mimir.Server.Components.Wisdom;

internal static class WisdomRoute
{
    internal const string LensName = "show";

    internal static string Listing(Guid projectId, WisdomLens lens)
        => $"projects/{projectId}/wisdom{LensSuffix(lens)}";

    internal static string Detail(Guid projectId, Guid wisdomId, WisdomLens lens)
        => $"projects/{projectId}/wisdom/{wisdomId}{LensSuffix(lens)}";

    internal static WisdomLens ParseLens(string? value)
        => Enum.TryParse<WisdomLens>(value, ignoreCase: true, out var lens) ? lens : WisdomLens.Active;

    internal static WisdomLens LensOf(Uri uri)
        => ParseLens(QueryHelpers.ParseQuery(uri.Query)
            .TryGetValue(LensName, out var values) ? values.ToString() : null);

    private static string LensSuffix(WisdomLens lens)
        => lens == WisdomLens.Active ? "" : $"?{LensName}={lens.ToString().ToLowerInvariant()}";
}
