namespace Mimir.Contracts.Mcp;

public sealed record McpTimelineRequest
{
    public string? Project { get; init; }

    public DateTimeOffset? Since { get; init; }
}
