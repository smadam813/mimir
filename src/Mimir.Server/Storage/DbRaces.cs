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
    /// A lost create race on a unique key is found by the very next query; 3 is margin. The #17
    /// identity races (upgrade collision, merge vs. concurrent reference) share this bound — the
    /// same one-rival-then-re-read shape.
    /// </summary>
    public const int CreateRaceMaxAttempts = 3;

    /// <summary>
    /// Only a unique-key collision means "someone else won the same slot". Anything else — an FK
    /// violation, a dropped connection — must surface, not spin through retries.
    /// </summary>
    public static bool IsUniqueViolation(this DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>Same signal on raw SQL, where Npgsql's exception arrives unwrapped (#17 upgrade).</summary>
    public static bool IsUniqueViolation(this PostgresException exception)
        => exception.SqlState == PostgresErrorCodes.UniqueViolation;

    /// <summary>
    /// The clone merge (#17) hard-deletes the losing Project; a concurrent insert referencing it
    /// mid-merge fails this way. The loser's rows were re-pointed, so a retry finds the survivor.
    /// </summary>
    public static bool IsForeignKeyViolation(this DbUpdateException exception)
        => exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation };

    /// <summary>
    /// The clone-merge race (#17): a concurrent hook can reference the merge's loser between
    /// re-point and delete. The rolled-back merge is retried whole; anything else surfaces.
    /// </summary>
    public static bool IsForeignKeyViolation(this PostgresException exception)
        => exception.SqlState == PostgresErrorCodes.ForeignKeyViolation;
}
