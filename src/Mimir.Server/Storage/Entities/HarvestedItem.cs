namespace Mimir.Server.Storage.Entities;

/// <summary>
/// Spec §3: the path-keyed, content-hashed record of one auto-memory file. Each row is one
/// version; a changed hash inserts a new row and the prior rows are kept forever (§10). The
/// latest row per path is the file's current state.
/// </summary>
public sealed class HarvestedItem
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>
    /// Harvest-relative path with forward slashes (<c>slug/memory/MEMORY.md</c>): the file's
    /// stable identity across scans, independent of where the harvest root is mounted.
    /// </summary>
    public required string Path { get; set; }

    /// <summary>SHA-256 of the file bytes, lowercase hex.</summary>
    public required string ContentHash { get; set; }

    public required string Content { get; set; }

    /// <summary>When Mimir first saw this path — copied forward across versions.</summary>
    public DateTimeOffset FirstSeen { get; set; }

    /// <summary>When this version's content appeared.</summary>
    public DateTimeOffset LastChanged { get; set; }

    /// <summary>
    /// When the file was found deleted, on the version that was current at the time. Derived
    /// Wisdom is untouched by deletion (§5). A reappearing file starts a fresh version row;
    /// this marker stays on the old row as history.
    /// </summary>
    public DateTimeOffset? GoneAt { get; set; }

    /// <summary>
    /// The §5 conversion marker: when this version's candidates went through the Merge Gate.
    /// Null means pending, which is what carries versions stored before the Wisdom tier shipped
    /// (the Backfill's memory) to the gate — they never change again, so no rescan would. Set in
    /// the same transaction as the gate's writes, making the handoff exactly-once across restarts.
    /// </summary>
    public DateTimeOffset? ConvertedAt { get; set; }
}
