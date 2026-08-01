namespace Mimir.Server.Tests;

internal static class TestPostgres
{
    private const string FallbackConnectionString =
        "Host=localhost;Port=5432;Database=mimir;Username=mimir;Password=mimir";

    // Admin: this reaches the development server itself, not a test database — CREATE/DROP DATABASE
    // run over it.
    public static string AdminConnectionString { get; } =
        (Environment.GetEnvironmentVariable("MIMIR_TEST_POSTGRES") ?? FallbackConnectionString)
        + ";Timeout=3;Command Timeout=30";

    public static string SkipMessage(string reason)
        => $"No Postgres reachable for integration tests ({reason}). "
            + "Run `docker compose up -d postgres`, or set MIMIR_TEST_POSTGRES.";
}
