namespace Mimir.Server.Tests;

/// <summary>
/// The one place the test-Postgres convention lives: MIMIR_TEST_POSTGRES overrides the compose
/// default, and short timeouts keep the no-database skip path fast. Both fixtures build on this —
/// drifting copies would turn a config change into silent skips, not failures.
/// </summary>
internal static class TestPostgres
{
    private const string FallbackConnectionString =
        "Host=localhost;Port=5432;Database=mimir;Username=mimir;Password=mimir";

    /// <summary>Admin connection to the development server (not a test database).</summary>
    public static string AdminConnectionString { get; } =
        (Environment.GetEnvironmentVariable("MIMIR_TEST_POSTGRES") ?? FallbackConnectionString)
        + ";Timeout=3;Command Timeout=30";

    /// <summary>The uniform skip text for a missing database.</summary>
    public static string SkipMessage(string reason)
        => $"No Postgres reachable for integration tests ({reason}). "
            + "Run `docker compose up -d postgres`, or set MIMIR_TEST_POSTGRES.";
}
