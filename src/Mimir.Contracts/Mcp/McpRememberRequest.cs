namespace Mimir.Contracts.Mcp;

public sealed record McpRememberRequest
{
    public required string ProjectIdentity { get; init; }

    public required string ProjectRoot { get; init; }

    public required string Content { get; init; }

    public required string Kind { get; init; }
}
