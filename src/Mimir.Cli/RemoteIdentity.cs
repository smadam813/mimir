namespace Mimir.Cli;

/// <summary>
/// Spec §3.1 step 2: normalize a git remote URL to <c>host/owner/repo</c> — strip scheme and
/// credentials, lowercase the host, convert scp-form <c>git@host:path</c> to <c>host/path</c>,
/// strip trailing <c>.git</c> and <c>/</c>. Local-path remotes (drive-letter or UNC) keep their
/// path as identity with separators unified to <c>/</c>. Only the host and drive letter are
/// lowercased: DNS is case-insensitive, but owner/repo and directory names can be case-sensitive
/// on their host — wrongly merging two distinct repositories is irreversible, while a
/// case-variant split of one repository is healable by clone merge (#17). Identity follows the
/// repository, not the directory: every scheme, credential, and separator spelling of one remote
/// must normalize identically or clones split into separate Projects.
/// </summary>
internal static class RemoteIdentity
{
    public static string Normalize(string remoteUrl)
    {
        var rest = remoteUrl.Trim();

        if (IsWindowsPath(rest))
        {
            // Not a host at all. git accepts C:\ and C:/ spellings of one directory; unify the
            // separators and lowercase the drive letter so both stay one identity.
            var localPath = rest.Replace('\\', '/');
            if (HasDrivePrefix(localPath))
            {
                localPath = char.ToLowerInvariant(localPath[0]) + localPath[1..];
            }

            return StripTail(localPath);
        }

        var schemeEnd = rest.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
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

        return StripTail(authority.ToLowerInvariant() + path);
    }

    /// <summary>
    /// A backslash anywhere, or <c>&lt;letter&gt;:&lt;separator&gt;</c> — mirroring git's own
    /// has_dos_drive_prefix, so a bare scp <c>c:path</c> keeps parsing as host <c>c</c>.
    /// </summary>
    private static bool IsWindowsPath(string rest)
        => rest.Contains('\\') || HasDrivePrefix(rest);

    private static bool HasDrivePrefix(string rest)
        => rest.Length >= 2
            && char.IsAsciiLetter(rest[0])
            && rest[1] == ':'
            && (rest.Length == 2 || rest[2] is '/' or '\\');

    private static string StripTail(string identity)
    {
        identity = identity.TrimEnd('/');
        if (identity.EndsWith(".git", StringComparison.Ordinal))
        {
            identity = identity[..^4];
        }

        return identity.TrimEnd('/');
    }
}
