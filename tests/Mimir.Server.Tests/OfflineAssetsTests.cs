using System.Text.RegularExpressions;

namespace Mimir.Server.Tests;

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
