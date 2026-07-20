using System.Text.RegularExpressions;

namespace Mimir.Server.Harvest;

/// <summary>
/// Spec §5 slug mapping: each directory under the harvest root is a project directory Claude Code
/// mangled by replacing every non-alphanumeric character with a hyphen. That is lossy — real
/// hyphens survive unchanged — so the exact direction is <see cref="Mangle"/> over a known root
/// (<see cref="MatchesRoot"/>), and <see cref="Demangle"/> is the best-effort guess that only
/// names the path-identity Project created when no known root matches.
/// </summary>
internal static partial class MemorySlug
{
    public static string Mangle(string absolutePath)
        => NonAlphanumeric().Replace(absolutePath, "-");

    /// <summary>Whether <paramref name="rootPath"/> is a directory this slug could name.</summary>
    /// <remarks>Case-insensitive: Windows paths are, and drive-letter case varies in the wild.</remarks>
    public static bool MatchesRoot(string slug, string rootPath)
        => string.Equals(Mangle(rootPath), slug, StringComparison.OrdinalIgnoreCase);

    public static string Demangle(string slug)
    {
        if (DrivePrefix().Match(slug) is { Success: true } drive)
        {
            var rest = drive.Groups[2].Value.Replace('-', '\\');
            return $@"{drive.Groups[1].Value}:\{Collapse(rest, '\\')}";
        }

        if (slug.StartsWith('-'))
        {
            return $"/{Collapse(slug[1..].Replace('-', '/'), '/')}";
        }

        return slug;
    }

    /// <summary>
    /// A mangled dot (<c>\.claude</c> → <c>--</c>) demangles to a doubled separator; collapsing
    /// yields a plausible path where keeping it would yield an impossible one.
    /// </summary>
    private static string Collapse(string path, char separator)
    {
        var doubled = new string(separator, 2);
        var single = separator.ToString();
        while (path.Contains(doubled))
        {
            path = path.Replace(doubled, single);
        }

        return path;
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlphanumeric();

    [GeneratedRegex("^([A-Za-z])--(.*)$")]
    private static partial Regex DrivePrefix();
}
