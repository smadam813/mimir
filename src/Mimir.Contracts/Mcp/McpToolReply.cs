namespace Mimir.Contracts.Mcp;

public sealed record McpToolReply
{
    public required string Text { get; init; }
}
