namespace Mimir.Server.Storage.Entities;

public sealed class Provenance
{
    public Guid Id { get; set; }

    public Guid WisdomId { get; set; }

    public Guid? EpisodeId { get; set; }

    public Guid? EventId { get; set; }

    public Guid? HarvestedItemId { get; set; }
}
