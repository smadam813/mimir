namespace Mimir.Cli;

/// <summary>
/// Spec §3.1 step 2: normalize a git remote URL to <c>host/owner/repo</c> — strip scheme and
/// credentials, lowercase the host, convert scp-form <c>git@host:path</c> to <c>host/path</c>,
/// strip trailing <c>.git</c> and <c>/</c>. Identity follows the repository, not the directory:
/// every spelling of one remote must normalize identically or clones split into separate Projects.
/// </summary>
internal static class RemoteIdentity
{
    public static string Normalize(string remoteUrl)
    {
        var rest = remoteUrl.Trim();

        var schemeEnd = rest.IndexOf("://", StringComparison.Ordinal);
        var hadScheme = schemeEnd >= 0;
        if (hadScheme)
        {
            rest = rest[(schemeEnd + 3)..];
        }
        else
        {
            // scp form: a ':' before any '/' separates host from path.
            var colon = rest.IndexOf(':');
            var slash = rest.IndexOf('/');
            if (colon >= 0 && (slash < 0 || colon < slash))
            {
                rest = $"{rest[..colon]}/{rest[(colon + 1)..]}";
            }
        }

        // The authority runs to the first '/'; credentials are everything up to its last '@'.
        var hostEnd = rest.IndexOf('/');
        var authority = hostEnd < 0 ? rest : rest[..hostEnd];
        var path = hostEnd < 0 ? "" : rest[hostEnd..];

        var at = authority.LastIndexOf('@');
        if (at >= 0)
        {
            authority = authority[(at + 1)..];
        }

        var identity = authority.ToLowerInvariant() + path;

        identity = identity.TrimEnd('/');
        if (identity.EndsWith(".git", StringComparison.Ordinal))
        {
            identity = identity[..^4];
        }

        return identity.TrimEnd('/');
    }
}
