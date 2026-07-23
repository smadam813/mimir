namespace Mimir.Contracts.Mcp;

/// <summary>
/// One <c>mimir_remember</c> call (spec §4, §7.1): an explicit save, bound server-side to the
/// most recently active unsealed Episode of the requester's Project — or straight to the Merge
/// Gate when none is live. A deliberate save is never dropped.
/// </summary>
public sealed record McpRememberRequest
{
    /// <summary>Normalized <c>host/owner/repo</c>, or an absolute path for non-repos (spec §3.1).</summary>
    public required string ProjectIdentity { get; init; }

    /// <summary>The repo root when in a repository, else the resolved directory. Always absolute.</summary>
    public required string ProjectRoot { get; init; }

    public required string Content { get; init; }

    /// <summary>The Wisdom kind (<c>Fact | Preference | Lesson | Procedure</c>).</summary>
    public required string Kind { get; init; }
}
