using Mimir.Contracts.Mcp;

namespace Mimir.Server.Recall;

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
