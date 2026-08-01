namespace Mimir.Server.Storage.Entities;

public sealed class WisdomVersion
{
    public Guid WisdomId { get; set; }

    public int Version { get; set; }

    public required string Text { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public WisdomVersionCause Cause { get; set; }
}

public enum WisdomVersionCause
{
    Distilled,
    Merged,
    Adjudicated,
    Edited,
}
