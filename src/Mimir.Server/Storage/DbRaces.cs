using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mimir.Server.Storage;

/// <summary>
/// The capture write path is optimistic: concurrent hooks race on unique indexes and the loser
/// re-reads and tries again. These are the shared pieces of that idiom.
/// </summary>
internal static class DbRaces
{
    /// <summary>
    /// N concurrent appenders can lose several consecutive rounds on the (episode_id, seq)
    /// unique index — every retry collides with every other retry. Give them room.
    /// </summary>
    public const int SeqRaceMaxAttempts = 5;

    /// <summary>
    /// A lost create race on a unique key is found by the very next query; 3 is margin.
    /// </summary>
    public const int CreateRaceMaxAttempts = 3;

    /// <summary>
    /// Only a unique-key collision means "someone else won the same slot". Anything else — an FK
    /// violation, a dropped connection — must surface, not spin through retries.
    /// </summary>
    public static bool IsUniqueViolation(this DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>
    /// The clone-merge race (#17): a concurrent hook can reference the merge's loser between
    /// re-point and delete. The rolled-back merge is retried whole; anything else surfaces.
    /// </summary>
    public static bool IsForeignKeyViolation(this PostgresException exception)
        => exception.SqlState == PostgresErrorCodes.ForeignKeyViolation;
}
