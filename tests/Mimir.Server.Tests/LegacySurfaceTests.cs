namespace Mimir.Server.Tests;

/// <summary>
/// The chassis's <c>.app-body</c> carries no padding (#94): a ported surface fills it with panes
/// that hold their own edges, so every page still written as a padded page must wrap itself in
/// <c>.legacy-surface</c>. Nothing in the compiler enforces that — a page added without the wrapper
/// renders flush to the viewport edge and only an eye catches it, so this is the enforcement.
///
/// Coarse by design: it sees that the file names the wrapper, not that every branch of the file
/// does. It retires with the convention — #97 deletes <c>app.css</c>, the wrapper and this class
/// together, and a page ported before then leaves this list in the same commit.
///
/// Pure text scan, no SQL, so it runs everywhere including with no Postgres reachable.
/// </summary>
public class LegacySurfaceTests
{
    private static readonly string PagesPath =
        Path.Combine(AppContext.BaseDirectory, "Components", "Pages");

    [Fact]
    public void EveryUnportedPage_HoldsItsOwnPadding()
    {
        var pages = Directory.GetFiles(PagesPath, "*.razor", SearchOption.AllDirectories);

        pages.ShouldNotBeEmpty();

        foreach (var page in pages)
        {
            File.ReadAllText(page)
                .ShouldContain(
                    "legacy-surface",
                    customMessage:
                        $"{Path.GetFileName(page)} renders straight into the layout's unpadded "
                        + ".app-body: wrap it in .legacy-surface, or if it is ported to panes of "
                        + "its own, say so here.");
        }
    }
}
