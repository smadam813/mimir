using System.Text.RegularExpressions;

namespace Mimir.Server.Tests;

/// <summary>
/// ADR-0001: Mimir is fully offline. A vendored stylesheet must never fetch a resource off the
/// machine to render a page — this is what stops a future design-system sync from quietly
/// reintroducing the Google Fonts @import that PR #98 removed. Pure text scan, no SQL, so it
/// runs everywhere including with no Postgres reachable.
/// </summary>
public class OfflineAssetsTests
{
    private static readonly Regex CssComment = new(@"/\*.*?\*/", RegexOptions.Singleline);
    private static readonly Regex RemoteImport =
        new("""@import\s+(url\(\s*['"]?https?://|['"]https?://)""", RegexOptions.IgnoreCase);
    private static readonly Regex RemoteUrlReference =
        new("""url\(\s*['"]?https?://""", RegexOptions.IgnoreCase);

    private static readonly string WwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");

    [Fact]
    public void VendoredStylesheets_CarryNoRemoteReferences()
    {
        var cssFiles = Directory.GetFiles(WwwrootPath, "*.css", SearchOption.AllDirectories);

        cssFiles.ShouldNotBeEmpty();

        foreach (var file in cssFiles)
        {
            var code = CssComment.Replace(File.ReadAllText(file), string.Empty);

            RemoteImport.IsMatch(code).ShouldBeFalse($"{file} imports a remote stylesheet");
            RemoteUrlReference.IsMatch(code).ShouldBeFalse($"{file} references a remote url()");
        }
    }
}
