using Mimir.Server.Harvest;

namespace Mimir.Server.Tests.Harvest;

public class MemorySlugTests
{
    [Fact]
    public void Mangle_TurnsAWindowsRootIntoItsSlug()
        => MemorySlug.Mangle(@"C:\git\mimir").ShouldBe("C--git-mimir");

    [Fact]
    public void Mangle_KeepsRealHyphensSoTheyAreIndistinguishableFromSeparators()
        => MemorySlug.Mangle(@"C:\git\fh6-tuning-calculator").ShouldBe("C--git-fh6-tuning-calculator");

    [Fact]
    public void Mangle_ReplacesDotsAndUnderscoresLikeSeparators()
        => MemorySlug.Mangle(@"C:\git\mimir\.claude\worktrees\my_tree")
            .ShouldBe("C--git-mimir--claude-worktrees-my-tree");

    [Fact]
    public void Mangle_TurnsAUnixRootIntoItsSlug()
        => MemorySlug.Mangle("/home/user/proj").ShouldBe("-home-user-proj");

    [Fact]
    public void MatchesRoot_IgnoresDriveLetterCase()
        => MemorySlug.MatchesRoot("C--git-mimir", @"c:\git\mimir").ShouldBeTrue();

    [Fact]
    public void MatchesRoot_RejectsADifferentRoot()
        => MemorySlug.MatchesRoot("C--git-mimir", @"C:\git\mjolnir").ShouldBeFalse();

    [Fact]
    public void Demangle_RecoversADriveLetterPath()
        => MemorySlug.Demangle("C--git-mimir").ShouldBe(@"C:\git\mimir");

    [Fact]
    public void Demangle_RecoversAUnixPath()
        => MemorySlug.Demangle("-home-user-proj").ShouldBe("/home/user/proj");

    [Fact]
    public void Demangle_CollapsesTheDoubleSeparatorAMangledDotLeaves()
        => MemorySlug.Demangle("C--git-mimir--claude-worktrees-pr-review")
            .ShouldBe(@"C:\git\mimir\claude\worktrees\pr\review");

    [Fact]
    public void Demangle_LeavesAnUnrecognisedShapeAlone()
        => MemorySlug.Demangle("no-drive-prefix").ShouldBe("no-drive-prefix");
}
