namespace Mimir.Contracts.Health;

/// <summary>
/// The live half of the spec §8 health strip. The Distillation tile is an inert placeholder until
/// its pipeline stage exists, so it carries no state here yet.
/// </summary>
public sealed record HealthSnapshot
{
    public static readonly HealthSnapshot Pending = new()
    {
        Ollama = OllamaTile.Pending,
        Harvester = HarvesterTile.Pending,
        Storage = StorageTile.Pending,
    };

    public required OllamaTile Ollama { get; init; }

    public required HarvesterTile Harvester { get; init; }

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
/// Spec §8: Harvester last scan / items / changed. A failed scan goes Degraded but keeps the last
/// good figures — stale numbers labelled with their scan time beat no numbers.
/// </summary>
public sealed record HarvesterTile
{
    public static readonly HarvesterTile Pending = new()
    {
        State = HealthTileState.Pending,
        Summary = "Waiting for the first scan",
    };

    public required HealthTileState State { get; init; }

    /// <summary>One line for the tile face.</summary>
    public required string Summary { get; init; }

    /// <summary>When the last successful scan finished. Null until one has.</summary>
    public DateTimeOffset? LastScanAt { get; init; }

    /// <summary>Memory files found by the last successful scan.</summary>
    public int? Items { get; init; }

    /// <summary>Files that stored a new HarvestedItem version in the last successful scan.</summary>
    public int? Changed { get; init; }
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

    /// <summary>
    /// On-disk footprint and occupancy for every table in the public schema. Empty until a
    /// migration adds one. A partitioned table appears once, under its parent's name.
    /// </summary>
    public IReadOnlyList<TableFootprint> Tables { get; init; } = [];
}

/// <summary>
/// One table's on-disk footprint and whether it holds anything. Carries no row count: under §10's
/// keep-forever retention an exact count is an unbounded sequential scan, and every cheap estimate
/// was measured misreporting a populated table as empty. See ADR-0006.
/// </summary>
/// <param name="TotalBytes">
/// Heap + indexes + TOAST, rolled up across partitions. Always exact, and the figure the tile falls
/// back to when <paramref name="Occupancy"/> is <see cref="TableOccupancy.Unknown"/>.
/// </param>
public sealed record TableFootprint(string Table, long TotalBytes, TableOccupancy Occupancy);

/// <summary>
/// Whether a table holds any rows. Three-valued on purpose: <see cref="Unknown"/> is not
/// <see cref="Empty"/> and must never be rendered or aggregated as though it were.
/// </summary>
/// <remarks>
/// <see cref="Unknown"/> is pinned to 0 so the C# default is the honest value — a probe that half
/// fails reports "we do not know", never "there is nothing there". A <c>bool</c> would default to
/// false and commit that misreport in the one place no SQL review would catch it.
/// </remarks>
public enum TableOccupancy
{
    /// <summary>Occupancy could not be determined this probe. Render distinctly from Empty.</summary>
    Unknown = 0,

    /// <summary>Proved to hold no rows, by EXISTS, in this snapshot.</summary>
    Empty,

    /// <summary>Proved to hold at least one row, by EXISTS, in this snapshot.</summary>
    Populated,
}
