using Mimir.Contracts.Mcp;

namespace Mimir.Server.Recall;

/// <summary>
/// The HTTP surface behind <c>mimir mcp</c> (spec §7): one route per tool, each answering with
/// the finished tool-result text. Unlike the fail-open hook routes, errors here surface — the MCP
/// lane is deliberate, and an honest error beats a silent empty answer.
/// </summary>
internal static class McpEndpoints
{
    public static async Task<McpToolReply> SearchAsync(
        McpSearchRequest request, McpSearchService search, CancellationToken cancellationToken)
        => new() { Text = await search.SearchAsync(request, cancellationToken) };

    public static async Task<McpToolReply> TimelineAsync(
        McpTimelineRequest request, McpTimelineService timeline, CancellationToken cancellationToken)
        => new() { Text = await timeline.TimelineAsync(request, cancellationToken) };

    public static async Task<McpToolReply> RememberAsync(
        McpRememberRequest request, McpRememberService remember, CancellationToken cancellationToken)
        => new() { Text = await remember.RememberAsync(request, cancellationToken) };
}
