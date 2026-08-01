namespace Mimir.Cli;

internal static class RemoteIdentity
{
    public static string Normalize(string remoteUrl)
    {
        var rest = remoteUrl.Trim();

        if (IsWindowsPath(rest))
        {
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
            var colon = rest.IndexOf(':');
            var slash = rest.IndexOf('/');
            if (colon >= 0 && (slash < 0 || colon < slash))
            {
                rest = $"{rest[..colon]}/{rest[(colon + 1)..]}";
            }
        }

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
