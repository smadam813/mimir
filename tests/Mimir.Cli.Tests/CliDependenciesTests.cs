using System.Xml.Linq;

namespace Mimir.Cli.Tests;

/// <summary>
/// The CLI ships as a self-contained single file onto a developer's machine, so its whole
/// dependency surface is the one thing worth pinning about its project file. The copy read here is
/// the shipped <c>Mimir.Cli.csproj</c>, carried into the test output by the test project.
/// </summary>
public class CliDependenciesTests
{
    [Fact]
    public void TheCli_TakesNoPackageDependency_OnlyContracts()
    {
        var project = XDocument.Load(
            Path.Combine(AppContext.BaseDirectory, "Mimir.Cli.csproj.xml"));

        project.Descendants("PackageReference")
            .Select(p => p.Attribute("Include")?.Value)
            .ShouldBeEmpty("the JSON-RPC surface is hand-rolled precisely so nothing is taken on");
        project.Descendants("ProjectReference")
            .Select(p => Path.GetFileName(p.Attribute("Include")!.Value.Replace('\\', '/')))
            .ShouldBe(["Mimir.Contracts.csproj"]);
    }
}
