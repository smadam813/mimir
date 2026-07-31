using System.Text.RegularExpressions;

namespace Mimir.Server.Tests;

/// <summary>
/// The one thing the stylesheet scans share. Both of them read shipped CSS as text rather than
/// parsing it, and both must not be fooled by a comment: <see cref="OfflineAssetsTests"/> would
/// fail on a remote URL somebody had already commented out, and <see cref="SurfaceChassisTests"/>
/// would find a selector in prose that names it.
/// </summary>
internal static class CssText
{
    private static readonly Regex Comment = new(@"/\*.*?\*/", RegexOptions.Singleline);

    internal static string StripComments(string css) => Comment.Replace(css, string.Empty);
}
