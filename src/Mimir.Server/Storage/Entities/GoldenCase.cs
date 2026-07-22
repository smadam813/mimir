namespace Mimir.Server.Storage.Entities;

/// <summary>
/// Spec §3: one regression case in the golden set (§9) — "for this query, under this Project's
/// affinity context, this Wisdom should rank". Grown by promoting marked injections in the UI
/// (§8.3) or inserted by hand for misses; consumed only by the dev-time golden runner.
/// </summary>
public sealed class GoldenCase
{
    public Guid Id { get; set; }

    /// <summary>The prompt text the runner replays through the §7 query ranking.</summary>
    public required string QueryContext { get; set; }

    /// <summary>The affinity context the runner ranks under — never a scope filter (§9).</summary>
    public Guid ProjectId { get; set; }

    public Guid ExpectedWisdomId { get; set; }

    /// <summary>The Injection this case was promoted from; null for hand-inserted cases.</summary>
    public Guid? CreatedFromInjectionId { get; set; }

    /// <summary>Why this case exists, in a curator's words.</summary>
    public required string Note { get; set; }
}
