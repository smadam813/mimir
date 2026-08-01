namespace Mimir.Server.Tests;

public sealed class CssTextTests
{
    [Fact]
    public void ACommentedOutRemoteUrl_IsNotLeftForTheOfflineScanToFailOn()
        => CssText.StripComments("""
            /* @import url("https://fonts.example/x.css"); */
            .pane { color: red; }
            """)
            .ShouldNotContain("https://");

    [Fact]
    public void ASelectorNamedInProse_IsNotLeftForTheChassisScanToFind()
        => CssText.StripComments("""
            /* .pane is defined below and must not be respelt */
            .surface { color: red; }
            """)
            .ShouldNotContain(".pane");

    [Fact]
    public void ACommentSpanningLines_IsStrippedWhole()
        => CssText.StripComments("""
            .a { color: red; }
            /* one
               .b { color: blue; }
               two */
            .c { color: green; }
            """)
            .ShouldNotContain(".b");

    [Fact]
    public void TwoCommentsOnOneLine_DoNotSwallowTheRuleBetweenThem()
        => CssText.StripComments("/* a */ .pane { color: red; } /* b */")
            .Trim()
            .ShouldBe(".pane { color: red; }");
}
