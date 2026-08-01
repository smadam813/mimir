using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

public class LikePatternTests
{
    [Fact]
    public void APlainTerm_BecomesAContainsMatch()
    {
        LikePattern.Contains("migrations").ShouldBe("%migrations%");
    }

    [Theory]
    [InlineData("100%", @"%100\%%")]
    [InlineData("created_at", @"%created\_at%")]
    [InlineData(@"a\b", @"%a\\b%")]
    public void AMetacharacter_IsEscapedSoItMatchesItself(string term, string expected)
    {
        LikePattern.Contains(term).ShouldBe(expected);
    }

    [Fact]
    public void TheEscapeItself_IsEscapedFirst()
    {
        LikePattern.Contains(@"\%").ShouldBe(@"%\\\%%");
    }

    [Fact]
    public void TheEscapeCharacter_IsTheOneThePatternWasBuiltWith()
    {
        LikePattern.EscapeCharacter.ShouldBe(@"\");
        LikePattern.Contains("100%").ShouldContain(LikePattern.EscapeCharacter + "%");
    }
}
