namespace Mimir.Server.Components.Layout;

internal static class ProjectRoute
{
    internal const string DefaultTab = "episodes";

    private static readonly string[] Tabs = ["wisdom", "episodes", "injections"];

    public static (Guid ProjectId, string Tab)? Parse(string relativePath)
    {
        var segments = Segments(relativePath);
        if (segments.Length < 2
            || !string.Equals(segments[0], "projects", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(segments[1], out var projectId))
        {
            return null;
        }

        var tab = segments.Length >= 3 ? segments[2].ToLowerInvariant() : DefaultTab;
        return (projectId, Array.IndexOf(Tabs, tab) >= 0 ? tab : DefaultTab);
    }

    // ToBaseRelativePath keeps the query string and fragment, so both are stripped here first.
    private static string[] Segments(string relativePath) => relativePath
        .Split(['?', '#'], 2)[0]
        .Split('/', StringSplitOptions.RemoveEmptyEntries);
}
