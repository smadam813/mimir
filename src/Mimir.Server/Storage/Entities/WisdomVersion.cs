namespace Mimir.Server.Storage.Entities;

/// <summary>
/// Spec §3: one entry in a Wisdom's text history. The full chain is kept forever (§10); deleting
/// the Wisdom is the only thing that removes it (the cascade).
/// </summary>
public sealed class WisdomVersion
{
    public Guid WisdomId { get; set; }

    /// <summary>1-based; unique per Wisdom (the composite key).</summary>
    public int Version { get; set; }

    public required string Text { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public WisdomVersionCause Cause { get; set; }
}

/// <summary>
/// The closed §3 cause enum. <see cref="Distilled"/> marks version 1 of gate-born Wisdom — from
/// the Distiller or from harvested candidates, both entering through the same Merge Gate insert.
/// </summary>
public enum WisdomVersionCause
{
    Distilled,
    Merged,
    Adjudicated,
    Edited,
}
