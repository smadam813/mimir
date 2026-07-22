namespace Mimir.Contracts.Mcp;

/// <summary>
/// One <c>mimir_search</c> call (spec §7), POSTed by <c>mimir mcp</c>. Deliberate recall reaches
/// everything regardless of scope; the requester's Project travels along (resolved host-side per
/// §7.1) only to anchor the affinity boost and the Injection row.
/// </summary>
public sealed record McpSearchRequest
{
    /// <summary>The MCP server's pseudo session id — stdio MCP never sees a real one (§7.1).</summary>
    public required string SessionId { get; init; }

    /// <summary>Normalized <c>host/owner/repo</c>, or an absolute path for non-repos (spec §3.1).</summary>
    public required string ProjectIdentity { get; init; }

    /// <summary>The repo root when in a repository, else the resolved directory. Always absolute.</summary>
    public required string ProjectRoot { get; init; }

    public required string Query { get; init; }

    /// <summary>Optional Project filter: a display name or identity to narrow both legs to.</summary>
    public string? Project { get; init; }

    /// <summary>Optional Wisdom kind filter (<c>Fact | Preference | Lesson | Procedure</c>).</summary>
    public string? Kind { get; init; }

    /// <summary>Keep only Wisdom confirmed, and Events captured, at or after this instant.</summary>
    public DateTimeOffset? Since { get; init; }

    /// <summary>Whether to run the Episode FTS leg. Defaults to true (§7).</summary>
    public bool IncludeEpisodes { get; init; } = true;

    /// <summary>Retired Wisdom is reachable only when this is set (§7).</summary>
    public bool IncludeRetired { get; init; }
}
