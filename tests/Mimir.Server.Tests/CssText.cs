using System.Text.RegularExpressions;

namespace Mimir.Server.Tests;

internal static class CssText
{
    private static readonly Regex Comment = new(@"/\*.*?\*/", RegexOptions.Singleline);

    internal static string StripComments(string css) => Comment.Replace(css, string.Empty);
}
