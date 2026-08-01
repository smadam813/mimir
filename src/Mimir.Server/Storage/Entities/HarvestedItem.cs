namespace Mimir.Server.Storage.Entities;

public sealed class HarvestedItem
{
    public Guid Id { get; set; }

    public Guid ProjectId { get; set; }

    public required string Path { get; set; }

    public required string ContentHash { get; set; }

    public required string Content { get; set; }

    public DateTimeOffset FirstSeen { get; set; }

    public DateTimeOffset LastChanged { get; set; }

    public DateTimeOffset? GoneAt { get; set; }

    public DateTimeOffset? ConvertedAt { get; set; }
}
