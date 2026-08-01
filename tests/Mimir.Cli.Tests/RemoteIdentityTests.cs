using Mimir.Cli;

namespace Mimir.Cli.Tests;

public class RemoteIdentityTests
{
    [Theory]
    [InlineData("https://github.com/smadam813/mimir.git")]
    [InlineData("https://github.com/smadam813/mimir")]
    [InlineData("https://user:s3cret@github.com/smadam813/mimir.git")]
    [InlineData("ssh://git@github.com/smadam813/mimir.git")]
    [InlineData("git://github.com/smadam813/mimir.git")]
    [InlineData("git@github.com:smadam813/mimir.git")]
    [InlineData("git@github.com:smadam813/mimir")]
    [InlineData("https://GitHub.com/smadam813/mimir.git")]
    [InlineData("git@GITHUB.COM:smadam813/mimir.git")]
    [InlineData("https://github.com/smadam813/mimir/")]
    [InlineData("https://github.com/smadam813/mimir.git/")]
    public void EverySpellingOfOneRemote_IsOneIdentity(string remoteUrl)
    {
        RemoteIdentity.Normalize(remoteUrl).ShouldBe("github.com/smadam813/mimir");
    }

    [Fact]
    public void OnlyTheHostIsLowercased_OwnerAndRepoKeepTheirCase()
    {
        RemoteIdentity.Normalize("https://GitHub.com/SmAdam813/MiMir.git")
            .ShouldBe("github.com/SmAdam813/MiMir");
    }

    [Theory]
    [InlineData(@"C:\repos\bare-repo.git")]
    [InlineData("C:/repos/bare-repo.git")]
    [InlineData("c:/repos/bare-repo.git")]
    [InlineData(@"C:\repos\bare-repo")]
    [InlineData("C:/repos/bare-repo/")]
    public void EverySpellingOfOneLocalWindowsRemote_IsOneIdentity(string remoteUrl)
    {
        // git accepts C:\ and C:/ for one directory, and a drive letter is not an SSH host.
        RemoteIdentity.Normalize(remoteUrl).ShouldBe("c:/repos/bare-repo");
    }

    [Fact]
    public void AUnixPathRemote_KeepsItsPathAsIdentity()
    {
        RemoteIdentity.Normalize("/srv/git/repo.git").ShouldBe("/srv/git/repo");
    }

    [Fact]
    public void AUncShareRemote_IsOneIdentityInBothSeparatorSpellings()
    {
        RemoteIdentity.Normalize(@"\\server\share\repo.git").ShouldBe("//server/share/repo");
        RemoteIdentity.Normalize("//server/share/repo.git").ShouldBe("//server/share/repo");
    }

    [Fact]
    public void AColonWithNoSeparatorAfterIt_IsStillAnScpHostNotADrive()
    {
        // git only treats <letter>:<separator> as a DOS path (has_dos_drive_prefix), so a bare
        // "c:path" keeps parsing as scp-form host "c".
        RemoteIdentity.Normalize("c:repos/x").ShouldBe("c/repos/x");
    }

    [Fact]
    public void ADotGitInTheMiddleOfANameSurvives()
    {
        RemoteIdentity.Normalize("https://github.com/owner/my.github.io.git")
            .ShouldBe("github.com/owner/my.github.io");
    }

    [Fact]
    public void SelfHostedDeepPathsKeepEverySegment()
    {
        RemoteIdentity.Normalize("ssh://git@gitlab.example.com/group/subgroup/repo.git")
            .ShouldBe("gitlab.example.com/group/subgroup/repo");
    }
}
