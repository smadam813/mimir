using System.Text.RegularExpressions;

namespace Mimir.Server.Harvest;

internal static partial class MemorySlug
{
    public static string Mangle(string absolutePath)
        => NonAlphanumeric().Replace(absolutePath, "-");

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
