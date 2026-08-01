namespace Mimir.Server.Storage.Entities;

public sealed class Episode
{
    public const string CrashSweptReason = "crash-swept";

    public Guid Id { get; set; }

    public required string SessionId { get; set; }

    public Guid ProjectId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? SealedAt { get; set; }

    public string? SealReason { get; set; }

    public required string Cwd { get; set; }

    public DistillationState Distillation { get; set; }

    public DateTimeOffset? DistillationStartedAt { get; set; }

    public DateTimeOffset? DistilledAt { get; set; }
}

public enum DistillationState
{
    Pending,
    Running,
    Done,
    Failed,
}
