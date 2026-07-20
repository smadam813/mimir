using Mimir.Cli;

namespace Mimir.Cli.Tests;

/// <summary>
/// The spec §3.1 normalization matrix: every way a machine can spell one repository's remote must
/// land on the same identity, because identity is what makes two clones one Project.
/// </summary>
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
