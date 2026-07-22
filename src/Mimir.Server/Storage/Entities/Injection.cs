namespace Mimir.Server.Storage.Entities;

/// <summary>
/// Spec §3: one actual injection — what a Recall lane put in front of a session, when, and how
/// much. Empty decisions are never logged (§7). The verdict pair is the §9 mark, set from the
/// injection-log UI; it applies to the entry as a whole.
/// </summary>
public sealed class Injection
{
    public Guid Id { get; set; }

    /// <summary>
    /// Claude Code's session id, as the hook reported it. Deliberately not a foreign key: an
    /// Episode hard-delete (§8.2) purges captured content, not the record that an injection
    /// happened.
    /// </summary>
    public required string SessionId { get; set; }

    public Guid ProjectId { get; set; }

    public DateTimeOffset At { get; set; }

    public InjectionLane Lane { get; set; }

    /// <summary>The prompt text for <c>Prompt</c>, the tool query for <c>MCP</c>; null for <c>Brief</c> (§3).</summary>
    public string? QueryContext { get; set; }

    /// <summary>The injected payload's size — the whole labeled wrapper, as printed.</summary>
    public int Chars { get; set; }

    /// <summary>The injected Wisdom with the scores that ordered them (§3).</summary>
    public List<InjectionItem> Items { get; set; } = [];

    /// <summary>The §9 mark: useful or noise, for the entry as a whole.</summary>
    public InjectionVerdict? Verdict { get; set; }

    public DateTimeOffset? VerdictAt { get; set; }
}

/// <summary>One injected Wisdom and the score that ranked it, kept in the entry's jsonb.</summary>
public sealed class InjectionItem
{
    public Guid WisdomId { get; set; }

    public double Score { get; set; }
}

/// <summary>The closed §3 lane enum.</summary>
public enum InjectionLane
{
    Brief,
    Prompt,
    Mcp,
}

/// <summary>The closed §3 verdict enum (§9's marks).</summary>
public enum InjectionVerdict
{
    Useful,
    Noise,
}
