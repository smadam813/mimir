using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mimir.Server.Storage;

internal static class DbRaces
{
    /// <summary>Higher than the create bound because N appenders can lose consecutive rounds:
    /// every retry collides with every other retry.</summary>
    public const int SeqRaceMaxAttempts = 5;

    /// <summary>A lost create race is found by the very next query, so 3 is margin.</summary>
    public const int CreateRaceMaxAttempts = 3;

    public static bool IsUniqueViolation(this DbUpdateException exception)
        => exception.InnerException is PostgresException inner && inner.IsUniqueViolation();

    /// <summary>A second overload because on raw SQL Npgsql's exception arrives unwrapped.</summary>
    public static bool IsUniqueViolation(this PostgresException exception)
        => exception.SqlState == PostgresErrorCodes.UniqueViolation;

    public static bool IsForeignKeyViolation(this DbUpdateException exception)
        => exception.InnerException is PostgresException inner && inner.IsForeignKeyViolation();

    public static bool IsForeignKeyViolation(this PostgresException exception)
        => exception.SqlState == PostgresErrorCodes.ForeignKeyViolation;
}
