using System.Diagnostics;

namespace Mimir.Cli.Tests;

/// <summary>
/// Spec §3.1 steps 1 and 3 against real git: which remote wins, and the path fallback when there
/// is no remote or no repository at all.
/// </summary>
public sealed class ProjectLocatorTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Scratch space; Windows sometimes holds .git files briefly. Leaked temp is fine.
            }
            catch (UnauthorizedAccessException)
            {
                // git marks some object files read-only; same story.
            }
        }
    }

    [Fact]
    public async Task ARepoWithAnOriginRemote_GetsTheNormalizedOriginIdentity()
    {
        var repo = GitRepo();
        Git(repo, "remote", "add", "origin", "https://github.com/smadam813/mimir.git");
        Git(repo, "remote", "add", "upstream", "https://github.com/aaa/first-alphabetically.git");

        var location = await Locate(repo);

        location.Identity.ShouldBe("github.com/smadam813/mimir");
        location.Root.ShouldBe(repo);
    }

    [Fact]
    public async Task WithNoOrigin_TheAlphabeticallyFirstRemoteWins()
    {
        var repo = GitRepo();
        Git(repo, "remote", "add", "zeta", "https://github.com/owner/zeta.git");
        Git(repo, "remote", "add", "alpha", "https://github.com/owner/alpha.git");

        (await Locate(repo)).Identity.ShouldBe("github.com/owner/alpha");
    }

    [Fact]
    public async Task ARepoWithNoRemote_FallsBackToItsRootPath()
    {
        var repo = GitRepo();

        var location = await Locate(repo);

        location.Identity.ShouldBe(repo);
        location.Root.ShouldBe(repo);
    }

    [Fact]
    public async Task ANonRepoDirectory_FallsBackToTheCwd()
    {
        var dir = TempDir();

        var location = await Locate(dir);

        location.Identity.ShouldBe(dir);
        location.Root.ShouldBe(dir);
    }

    [Fact]
    public async Task ASubdirectoryResolvesToTheRepoRoot_NotTheCwd()
    {
        var repo = GitRepo();
        Git(repo, "remote", "add", "origin", "git@github.com:smadam813/mimir.git");
        var nested = Path.Combine(repo, "src", "deep");
        Directory.CreateDirectory(nested);

        var location = await Locate(nested);

        location.Identity.ShouldBe("github.com/smadam813/mimir");
        location.Root.ShouldBe(repo);
    }

    [Fact]
    public async Task AnAlreadyCancelledCap_StillAnswersWithThePathFallback()
    {
        // The hook's 3 s cap can expire mid-resolution; out of time means "no repository
        // information", never an exception (spec §4 fail-open).
        var repo = GitRepo();
        using var expired = new CancellationTokenSource();
        await expired.CancelAsync();

        var location = await ProjectLocator.LocateAsync(repo, expired.Token);

        location.Identity.ShouldBe(repo);
        location.Root.ShouldBe(repo);
    }

    private static Task<ProjectLocation> Locate(string cwd)
        => ProjectLocator.LocateAsync(cwd, TestContext.Current.CancellationToken);

    private string GitRepo()
    {
        var dir = TempDir();
        Git(dir, "init");
        return dir;
    }

    private string TempDir()
    {
        var dir = Directory.CreateTempSubdirectory("mimir-cli-test-").FullName;
        _tempDirs.Add(dir);
        return dir;
    }

    private static void Git(string workingDir, params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo)!;
        process.WaitForExit();
        process.ExitCode.ShouldBe(0, $"git {string.Join(' ', args)} failed: {process.StandardError.ReadToEnd()}");
    }
}
