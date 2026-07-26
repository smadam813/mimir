namespace Mimir.Server.Components.Layout;

/// <summary>
/// Parses the chassis's one route shape — <c>projects/{id}/{tab}[...]</c> — the way
/// <c>ProjectPage</c> already read <c>Tab</c>, so <c>ProjectSidebar</c> and <c>SurfaceTabStrip</c>
/// can each read the current Project and active surface from the URL alone, without a cascading
/// parameter from the page they sit beside.
/// </summary>
internal static class ProjectRoute
{
    /// <summary>The three surfaces, in spec order. Anything else — or nothing — lands on Episodes.</summary>
    internal const string DefaultTab = "episodes";

    private static readonly string[] Tabs = ["wisdom", "episodes", "injections"];

    /// <summary>
    /// Reads a Blazor <c>NavigationManager.ToBaseRelativePath(...)</c> result. Null off the
    /// <c>projects/{guid}</c> route entirely (the home page, say); the tab defaults to
    /// <see cref="DefaultTab"/> both when the segment is missing and when it names neither surface
    /// nor a recognised one — matching <c>ProjectPage.ActiveTab</c>'s own fallback.
    /// </summary>
    public static (Guid ProjectId, string Tab)? Parse(string relativePath)
    {
        // ToBaseRelativePath keeps the query string and fragment; strip them before segmenting so
        // a pasted "?highlight=…" or "#anchor" doesn't get parsed as (or corrupt) the tab segment.
        var path = relativePath.Split(['?', '#'], 2)[0];
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2
            || !string.Equals(segments[0], "projects", StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParse(segments[1], out var projectId))
        {
            return null;
        }

        var tab = segments.Length >= 3 ? segments[2].ToLowerInvariant() : DefaultTab;
        return (projectId, Array.IndexOf(Tabs, tab) >= 0 ? tab : DefaultTab);
    }
}
