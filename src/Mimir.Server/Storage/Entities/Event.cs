using NpgsqlTypes;

namespace Mimir.Server.Storage.Entities;

public sealed class Event
{
    public Guid Id { get; set; }

    public Guid EpisodeId { get; set; }

    public int Seq { get; set; }

    public EventType Type { get; set; }

    public DateTimeOffset At { get; set; }

    public required string Payload { get; set; }

    public int PayloadFullSize { get; set; }

    public bool Salient { get; set; }

    public NpgsqlTsVector? Tsv { get; set; }
}

public enum EventType
{
    UserPromptSubmit,
    PostToolUse,
    Stop,
    Remember,
}
