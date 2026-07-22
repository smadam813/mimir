namespace Mimir.Contracts.Mcp;

/// <summary>One <c>mimir_timeline</c> call (spec §7): the Episode timeline, optionally narrowed.</summary>
public sealed record McpTimelineRequest
{
    /// <summary>Optional Project filter: a display name or identity to narrow the timeline to.</summary>
    public string? Project { get; init; }

    /// <summary>Keep only Episodes started at or after this instant.</summary>
    public DateTimeOffset? Since { get; init; }
}
