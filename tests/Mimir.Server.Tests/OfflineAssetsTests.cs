using System.Text.RegularExpressions;

namespace Mimir.Server.Tests;

/// <summary>
/// ADR-0001: Mimir is fully offline. A stylesheet the browser loads must never fetch a resource
/// off the machine to render a page — this is what stops a future design-system sync from quietly
/// reintroducing the Google Fonts @import that PR #98 removed. Pure text scan, no SQL, so it
/// runs everywhere including with no Postgres reachable.
///
/// Both of the two ways CSS reaches a browser here are scanned. <c>wwwroot</c> is the two global
/// files <c>App.razor</c> links by name; the per-component <c>.razor.css</c> files are the third
/// link, arriving as the <c>Mimir.Server.styles.css</c> bundle Blazor assembles from them, so a
/// remote <c>url(…)</c> written into one of those ships and phones home exactly the same way.
/// </summary>
public class OfflineAssetsTests
{
    private static readonly Regex RemoteImport =
        new("""@import\s+(url\(\s*['"]?https?://|['"]https?://)""", RegexOptions.IgnoreCase);
    private static readonly Regex RemoteUrlReference =
        new("""url\(\s*['"]?https?://""", RegexOptions.IgnoreCase);

    private static readonly string WwwrootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot");

    private static readonly string ComponentsPath =
        Path.Combine(AppContext.BaseDirectory, "Components");

    [Fact]
    public void ShippedStylesheets_CarryNoRemoteReferences()
    {
        string[] cssFiles =
        [
            .. Directory.GetFiles(WwwrootPath, "*.css", SearchOption.AllDirectories),
            .. Directory.GetFiles(ComponentsPath, "*.razor.css", SearchOption.AllDirectories),
        ];

        cssFiles.ShouldNotBeEmpty();

        foreach (var file in cssFiles)
        {
            var code = CssText.StripComments(File.ReadAllText(file));

            RemoteImport.IsMatch(code).ShouldBeFalse($"{file} imports a remote stylesheet");
            RemoteUrlReference.IsMatch(code).ShouldBeFalse($"{file} references a remote url()");
        }
    }
}
