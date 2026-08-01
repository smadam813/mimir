namespace Mimir.Contracts.Health;

public sealed record HealthSnapshot
{
    public static readonly HealthSnapshot Pending = new()
    {
        Ollama = OllamaTile.Pending,
        Distillation = DistillationTile.Pending,
        Harvester = HarvesterTile.Pending,
        Storage = StorageTile.Pending,
    };

    public required OllamaTile Ollama { get; init; }

    public required DistillationTile Distillation { get; init; }

    public required HarvesterTile Harvester { get; init; }

    public required StorageTile Storage { get; init; }
}

public enum HealthTileState
{
    Pending,

    Working,

    Ready,

    Degraded,
}

public sealed record OllamaTile
{
    public static readonly OllamaTile Pending = new()
    {
        State = HealthTileState.Pending,
        Summary = "Waiting for Ollama",
    };

    public required HealthTileState State { get; init; }

    public required string Summary { get; init; }

    public IReadOnlyList<ModelStatus> Models { get; init; } = [];
}

public sealed record ModelStatus
{
    public required string Name { get; init; }

    public required ModelProvisioningState State { get; init; }

    public int? PercentComplete { get; init; }

    public string? Error { get; init; }
}

public enum ModelProvisioningState
{
    Pending,

    Pulling,

    Ready,

    Failed,
}

public sealed record DistillationTile
{
    public static readonly DistillationTile Pending = new()
    {
        State = HealthTileState.Pending,
        Summary = "Waiting for the first pass",
    };

    public required HealthTileState State { get; init; }

    public required string Summary { get; init; }

    public int? QueueDepth { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }
}

public sealed record HarvesterTile
{
    public static readonly HarvesterTile Pending = new()
    {
        State = HealthTileState.Pending,
        Summary = "Waiting for the first scan",
    };

    public required HealthTileState State { get; init; }

    public required string Summary { get; init; }

    public DateTimeOffset? LastScanAt { get; init; }

    public int? Items { get; init; }

    public int? Changed { get; init; }
}

public sealed record StorageTile
{
    public static readonly StorageTile Pending = new()
    {
        State = HealthTileState.Pending,
        Summary = "Waiting for Postgres",
    };

    public required HealthTileState State { get; init; }

    public required string Summary { get; init; }

    public long? DatabaseSizeBytes { get; init; }

    public IReadOnlyList<TableFootprint> Tables { get; init; } = [];
}

public sealed record TableFootprint(string Table, long TotalBytes, TableOccupancy Occupancy);

public enum TableOccupancy
{
    Unknown = 0,

    Empty,

    Populated,
}
