namespace Mimir.Contracts.Health;

/// <summary>
/// The live half of the spec §8 health strip. The Distillation and Harvester tiles are inert
/// placeholders until their pipeline stages exist, so they carry no state here yet.
/// </summary>
public sealed record HealthSnapshot
{
    public static readonly HealthSnapshot Pending = new()
    {
        Ollama = OllamaTile.Pending,
        Storage = StorageTile.Pending,
    };

    public required OllamaTile Ollama { get; init; }

    public required StorageTile Storage { get; init; }
}

/// <summary>How a tile is doing, independent of what it measures.</summary>
public enum HealthTileState
{
    /// <summary>Nothing has reported yet — the state at boot.</summary>
    Pending,

    /// <summary>Reporting, but mid-flight (models still pulling).</summary>
    Working,

    /// <summary>Reporting healthy.</summary>
    Ready,

    /// <summary>Reachable but unhappy, or unreachable entirely.</summary>
    Degraded,
}

/// <summary>Spec §8: Ollama state + models.</summary>
public sealed record OllamaTile
{
    public static readonly OllamaTile Pending = new()
    {
        State = HealthTileState.Pending,
        Summary = "Waiting for Ollama",
    };

    public required HealthTileState State { get; init; }

    /// <summary>One line for the tile face.</summary>
    public required string Summary { get; init; }

    public IReadOnlyList<ModelStatus> Models { get; init; } = [];
}

/// <summary>Where one of the spec §11 models is in provisioning.</summary>
public sealed record ModelStatus
{
    public required string Name { get; init; }

    public required ModelProvisioningState State { get; init; }

    /// <summary>Pull progress, when Ollama is reporting a total. Null outside a pull.</summary>
    public int? PercentComplete { get; init; }

    /// <summary>Why provisioning failed, when it did.</summary>
    public string? Error { get; init; }
}

public enum ModelProvisioningState
{
    /// <summary>Queued for provisioning; nothing attempted yet.</summary>
    Pending,

    /// <summary>Being downloaded from the registry right now.</summary>
    Pulling,

    /// <summary>Present locally and usable.</summary>
    Ready,

    /// <summary>Provisioning failed; the model is not usable.</summary>
    Failed,
}

/// <summary>
/// Spec §8: storage counts + size. Deliberately generic — it discovers whatever tables exist
/// rather than naming domain tables, which arrive in later tickets.
/// </summary>
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

    /// <summary>Row counts for every table in the public schema. Empty until a migration adds one.</summary>
    public IReadOnlyList<TableRowCount> Tables { get; init; } = [];
}

public sealed record TableRowCount(string Table, long Rows);
