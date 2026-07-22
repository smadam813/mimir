namespace Mimir.Server.Storage.Entities;

/// <summary>
/// Spec §3: one source a Wisdom was derived or confirmed from — an Episode, an Event, or a
/// HarvestedItem. Unioned on merge. The schema declares the §3 deletion contract: hard-deleting a
/// referenced Event or Episode (§8.2) cascades these rows away — the sole operation that removes
/// Provenance — while the Wisdom itself survives with its provenance orphaned.
/// </summary>
public sealed class Provenance
{
    public Guid Id { get; set; }

    public Guid WisdomId { get; set; }

    public Guid? EpisodeId { get; set; }

    public Guid? EventId { get; set; }

    public Guid? HarvestedItemId { get; set; }
}
