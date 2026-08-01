using NpgsqlTypes;
using Pgvector;

namespace Mimir.Server.Storage.Entities;

public sealed class Wisdom
{
    public Guid Id { get; set; }

    public WisdomKind Kind { get; set; }

    public Guid ScopeProjectId { get; set; }

    public required string Text { get; set; }

    public required Vector Embedding { get; set; }

    public NpgsqlTsVector? Tsv { get; set; }

    public int Reinforcement { get; set; }

    public DateTimeOffset LastConfirmedAt { get; set; }

    public DateTimeOffset? ContestedAt { get; set; }

    public DateTimeOffset? RetiredAt { get; set; }

    public Guid? SupersededBy { get; set; }
}

public enum WisdomKind
{
    Fact,
    Preference,
    Lesson,
    Procedure,
}
