namespace Mimir.Server.Storage.Entities;

/// <summary>
/// Spec §3: the raw record of one Claude Code session (ADR-0003 — an Episode <em>is</em> a
/// session, keyed by its session id). Unsealed means live or crashed; Sealing carries the
/// hook-reported reason, or <c>crash-swept</c> when the Distiller's sweep closes it.
/// </summary>
public sealed class Episode
{
    public Guid Id { get; set; }

    /// <summary>Claude Code's session id. Unique — one Episode per session.</summary>
    public required string SessionId { get; set; }

    public Guid ProjectId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? SealedAt { get; set; }

    /// <summary>Why the Episode Sealed: the SessionEnd reason, or <c>crash-swept</c> (§4).</summary>
    public string? SealReason { get; set; }

    public required string Cwd { get; set; }

    /// <summary>Spec §6 bookkeeping — the distillation queue is this column.</summary>
    public DistillationState Distillation { get; set; }

    public DateTimeOffset? DistilledAt { get; set; }
}

/// <summary>Where an Episode is in the spec §6 pipeline. The queue is this DB state.</summary>
public enum DistillationState
{
    Pending,
    Running,
    Done,
    Failed,
}
