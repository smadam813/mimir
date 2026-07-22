namespace Mimir.Contracts.Mcp;

/// <summary>
/// What every MCP tool answers with: the finished tool-result text, composed server-side so the
/// Injection row's <c>chars</c> (§3) counts exactly what the session received. The CLI relays it
/// verbatim as the MCP text content.
/// </summary>
public sealed record McpToolReply
{
    public required string Text { get; init; }
}
