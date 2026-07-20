using System.Diagnostics;

namespace Mimir.Cli;

/// <summary>Where a hook fired from: the Project identity and root the CLI sends with every POST.</summary>
internal sealed record ProjectLocation(string Identity, string Root);

/// <summary>
/// Spec §3.1: resolve Project identity host-side. The remote of <c>origin</c> (else the
/// alphabetically first remote) normalized per <see cref="RemoteIdentity"/>; a repo with no remote
/// falls back to its root path, a non-repo to the cwd. Never throws, and honors the caller's
/// cancellation — the hook's 3 s cap must bound git too, so a hung git is killed, not awaited. A
/// machine without git is a machine whose Projects are identified by path.
/// </summary>
internal static class ProjectLocator
{
    /// <summary>Per-call ceiling for a local metadata read; the hook's cap still bounds the total.</summary>
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(2);

    public static async Task<ProjectLocation> LocateAsync(string cwd, CancellationToken cancellationToken)
    {
        // Both lookups read from cwd — no data dependency. Started together they cost one git
        // round-trip instead of two on the prompt hook's 500 ms budget (§11).
        var toplevelTask = RunGitAsync(cwd, ["rev-parse", "--show-toplevel"], cancellationToken);
        var remoteTask = RemoteUrlAsync(cwd, cancellationToken);

        var root = await toplevelTask is { } toplevel ? Path.GetFullPath(toplevel) : null;
        var url = await remoteTask; // Always awaited: a hung git is killed, never orphaned.

        if (root is null)
        {
            return new ProjectLocation(cwd, cwd);
        }

        return url is { } remoteUrl
            ? new ProjectLocation(RemoteIdentity.Normalize(remoteUrl), root)
            : new ProjectLocation(root, root);
    }

    private static async Task<string?> RemoteUrlAsync(string cwd, CancellationToken cancellationToken)
    {
        if (await RunGitAsync(cwd, ["remote", "get-url", "origin"], cancellationToken) is { } originUrl)
        {
            return originUrl;
        }

        var firstRemote = (await RunGitAsync(cwd, ["remote"], cancellationToken))
            ?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();

        return firstRemote is null
            ? null
            : await RunGitAsync(cwd, ["remote", "get-url", firstRemote], cancellationToken);
    }

    /// <summary>Runs git, returning trimmed stdout — or null on failure, timeout, or cancellation.</summary>
    private static async Task<string?> RunGitAsync(string cwd, string[] args, CancellationToken cancellationToken)
    {
        Process? process = null;
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

            process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(GitTimeout);

            // Drain both pipes while waiting: a full, unread stderr buffer can wedge git.
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);

            var output = (await stdout).Trim();
            await stderr; // Observed, not abandoned: an I/O fault routes into the catch below.
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception)
        {
            // No git, no PATH, no permissions, or out of time — all the same answer: no
            // repository information. A still-running git must not outlive the hook.
            TryKill(process);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void TryKill(Process? process)
    {
        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already exited or already gone — either way it is no longer our problem.
        }
    }
}
