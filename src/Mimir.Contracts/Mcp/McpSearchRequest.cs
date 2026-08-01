namespace Mimir.Contracts.Mcp;

public sealed record McpSearchRequest
{
    public required string SessionId { get; init; }

    public required string ProjectIdentity { get; init; }

    public required string ProjectRoot { get; init; }

    public required string Query { get; init; }

    public string? Project { get; init; }

    public string? Kind { get; init; }

    public DateTimeOffset? Since { get; init; }

    public bool IncludeEpisodes { get; init; } = true;

    public bool IncludeRetired { get; init; }
}
