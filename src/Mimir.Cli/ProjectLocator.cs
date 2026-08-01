using System.Diagnostics;

namespace Mimir.Cli;

internal sealed record ProjectLocation(string Identity, string Root);

internal static class ProjectLocator
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(2);

    public static async Task<ProjectLocation> LocateAsync(string cwd, CancellationToken cancellationToken)
    {
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
            // Swallowed deliberately, but a still-running git must not outlive the hook.
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
            // Swallowed deliberately: already exited, or already gone.
        }
    }
}
