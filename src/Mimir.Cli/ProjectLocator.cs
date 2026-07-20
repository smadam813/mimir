using System.Diagnostics;

namespace Mimir.Cli;

/// <summary>Where a hook fired from: the Project identity and root the CLI sends with every POST.</summary>
internal sealed record ProjectLocation(string Identity, string Root);

/// <summary>
/// Spec §3.1: resolve Project identity host-side. The remote of <c>origin</c> (else the
/// alphabetically first remote) normalized per <see cref="RemoteIdentity"/>; a repo with no remote
/// falls back to its root path, a non-repo to the cwd. Never throws — a machine without git is a
/// machine whose Projects are identified by path.
/// </summary>
internal static class ProjectLocator
{
    /// <summary>Generous for a local metadata read; the hook's own 3 s cap is the real budget.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(2);

    public static ProjectLocation Locate(string cwd)
    {
        var root = RunGit(cwd, "rev-parse", "--show-toplevel") is { } toplevel
            ? Path.GetFullPath(toplevel)
            : null;

        if (root is null)
        {
            return new ProjectLocation(cwd, cwd);
        }

        return RemoteUrl(cwd) is { } url
            ? new ProjectLocation(RemoteIdentity.Normalize(url), root)
            : new ProjectLocation(root, root);
    }

    private static string? RemoteUrl(string cwd)
    {
        if (RunGit(cwd, "remote", "get-url", "origin") is { } originUrl)
        {
            return originUrl;
        }

        var firstRemote = RunGit(cwd, "remote")
            ?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();

        return firstRemote is null ? null : RunGit(cwd, "remote", "get-url", firstRemote);
    }

    /// <summary>Runs git, returning trimmed stdout — or null on any failure at all.</summary>
    private static string? RunGit(string cwd, params string[] args)
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(cwd);
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(GitTimeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            var output = stdout.Trim();
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception)
        {
            // No git, no PATH, no permissions — all the same answer: no repository information.
            return null;
        }
    }
}
