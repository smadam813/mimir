using Mimir.Server.Ui;

namespace Mimir.Server.Tests.Ui;

/// <summary>
/// The one escape both browsers' search boxes narrow by. Pure, and deliberately Postgres-free —
/// this is string work that is wrong or right with no database in the picture, and the two boxes'
/// own listing tests each need Postgres to run.
/// </summary>
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
        // Unescaped, "100%" matches every row and "created_at" matches "createdXat" — a curator
        // searching for a literal gets the whole table back instead.
        LikePattern.Contains(term).ShouldBe(expected);
    }

    [Fact]
    public void TheEscapeItself_IsEscapedFirst()
    {
        // Order matters: escape "%" before "\" and the backslash just added gets doubled, leaving
        // `%\\%%` — a literal backslash followed by the wildcard the escape was meant to defuse.
        LikePattern.Contains(@"\%").ShouldBe(@"%\\\%%");
    }

    [Fact]
    public void TheEscapeCharacter_IsTheOneThePatternWasBuiltWith()
    {
        // The caller hands this to ILIKE's ESCAPE. Any other and every escape in the pattern is
        // read as a literal backslash, so the metacharacters go back to being metacharacters.
        LikePattern.EscapeCharacter.ShouldBe(@"\");
        LikePattern.Contains("100%").ShouldContain(LikePattern.EscapeCharacter + "%");
    }
}
