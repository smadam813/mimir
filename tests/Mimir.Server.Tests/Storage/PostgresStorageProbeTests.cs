using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mimir.Contracts.Health;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Storage;

/// <summary>
/// The traps in ADR-0006 are all about what Postgres <em>actually</em> reports, so they can only be
/// pinned against a real one. Skips when no database is reachable, which keeps <c>dotnet test</c>
/// useful on a machine with nothing running; <c>docker compose up postgres</c> turns them on.
/// </summary>
public sealed class PostgresStorageProbeTests(PostgresFixture fixture)
    : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly List<string> _scratchTables = [];
    private MimirDbContext? _context;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_context is null)
        {
            return;
        }

        foreach (var table in _scratchTables)
        {
            await ExecuteAsync($"DROP TABLE IF EXISTS \"{table}\" CASCADE;");
        }

        await _context.DisposeAsync();
    }

    [Fact]
    public async Task AnalyzedWhileEmptyThenPopulated_ReportsPopulated()
    {
        // THE trap that disqualified every estimator. This is the shape an EF migration produces:
        // the table is created empty, gets analyzed while empty, and is only then written to.
        // Measured in this exact state: reltuples = 0, relpages = 0, n_live_tup = 0 — with 200,000
        // rows present. Anything reading those statistics reports a populated table as empty.
        var table = await ScratchTable();
        await ExecuteAsync($"ANALYZE \"{table}\";");
        await ExecuteAsync($"INSERT INTO \"{table}\" SELECT g, repeat('x', 100) FROM generate_series(1, 200000) g;");

        var footprint = await ProbeFor(table);

        footprint.Occupancy.ShouldBe(TableOccupancy.Populated);
        footprint.TotalBytes.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task PopulatedThenFullyDeleted_ReportsEmpty()
    {
        // The mirror trap, and the one that matters most to a user: spec §8.2 makes hard Delete of
        // sensitive Events and Episodes a user-facing action, so this fires exactly when someone is
        // checking whether their deletion took effect. n_live_tup was measured still reporting
        // 50,000 here, and reltuples 200,000, with the table genuinely empty.
        var table = await ScratchTable();
        await ExecuteAsync($"INSERT INTO \"{table}\" SELECT g, repeat('x', 100) FROM generate_series(1, 200000) g;");
        await ExecuteAsync($"ANALYZE \"{table}\";");
        await ExecuteAsync($"DELETE FROM \"{table}\";");

        (await ProbeFor(table)).Occupancy.ShouldBe(TableOccupancy.Empty);
    }

    [Fact]
    public async Task AnEmptyTableIsReportedEmpty_NotUnknown()
    {
        var table = await ScratchTable();

        (await ProbeFor(table)).Occupancy.ShouldBe(TableOccupancy.Empty);
    }

    [Fact]
    public async Task APlainTableReportsItsRealSize()
    {
        // The pg_partition_tree landmine: that function returns zero rows for an ordinary table, so
        // an unconditional rollup sizes every plain table at 0 bytes — the worst kind of silent
        // failure, because the tile still looks healthy.
        var table = await ScratchTable();
        await ExecuteAsync($"INSERT INTO \"{table}\" SELECT g, repeat('x', 100) FROM generate_series(1, 20000) g;");

        (await ProbeFor(table)).TotalBytes.ShouldBeGreaterThan(1_000_000);
    }

    [Fact]
    public async Task APartitionedTableIsDiscoveredOnceUnderItsParentName()
    {
        // pg_tables returns parents AND children; summing both double-counted a partitioned table
        // (measured: 50,000 real rows reported as 100,000).
        var parent = Name("part");
        var child = $"{parent}_p1";
        _scratchTables.Add(parent);
        _scratchTables.Add(child);

        await ExecuteAsync($"CREATE TABLE \"{parent}\" (id int) PARTITION BY RANGE (id);");
        await ExecuteAsync($"CREATE TABLE \"{child}\" PARTITION OF \"{parent}\" FOR VALUES FROM (1) TO (100000);");
        await ExecuteAsync($"INSERT INTO \"{parent}\" SELECT generate_series(1, 50000);");

        var tile = await Probe();

        tile.Tables.Count(t => t.Table == parent).ShouldBe(1);
        tile.Tables.ShouldNotContain(t => t.Table == child, "a partition child must not be listed separately");

        var footprint = tile.Tables.Single(t => t.Table == parent);
        footprint.TotalBytes.ShouldBeGreaterThan(0, "the parent holds no data itself; its leaves' size must roll up");
        footprint.Occupancy.ShouldBe(TableOccupancy.Populated, "EXISTS must see through to the leaf");
    }

    [Fact]
    public async Task AZeroByteTableIsNeverReportedPopulated()
    {
        // A tuple cannot exist without a page, so this is an invariant across the two queries: if
        // it ever fails, size and occupancy are disagreeing and one of them is lying. Deliberately
        // an assertion and not a production shortcut — EXISTS stays the only source of occupancy.
        var tile = await Probe();

        foreach (var table in tile.Tables.Where(t => t.TotalBytes == 0))
        {
            table.Occupancy.ShouldNotBe(TableOccupancy.Populated);
        }
    }

    [Fact]
    public async Task TheMigrationsHistoryTableIsNotReported()
    {
        (await Probe()).Tables.ShouldNotContain(t => t.Table == "__EFMigrationsHistory");
    }

    private async Task<StorageTile> Probe()
    {
        var probe = new PostgresStorageProbe(Context, NullLogger<PostgresStorageProbe>.Instance);
        var tile = await probe.ProbeAsync(TestContext.Current.CancellationToken);

        tile.State.ShouldBe(HealthTileState.Ready, tile.Summary);
        return tile;
    }

    private async Task<TableFootprint> ProbeFor(string table)
    {
        var tile = await Probe();
        tile.Tables.ShouldContain(t => t.Table == table, $"table {table} was never discovered");
        return tile.Tables.Single(t => t.Table == table);
    }

    /// <summary>Creates an empty scratch table and registers it for cleanup.</summary>
    private async Task<string> ScratchTable()
    {
        var table = Name("tbl");
        _scratchTables.Add(table);
        await ExecuteAsync($"CREATE TABLE \"{table}\" (id int, payload text);");
        return table;
    }

    private static string Name(string kind) => $"wf_{kind}_{Guid.NewGuid():N}"[..24];

    private Task ExecuteAsync(string sql)
        => Context.Database.ExecuteSqlRawAsync(sql, TestContext.Current.CancellationToken);

    private MimirDbContext Context
    {
        get
        {
            if (fixture.UnavailableReason is { } reason)
            {
                Assert.Skip(TestPostgres.SkipMessage(reason));
            }

            return _context ??= new MimirDbContext(new DbContextOptionsBuilder<MimirDbContext>()
                .UseNpgsql(fixture.ConnectionString, npgsql => npgsql.UseVector())
                .Options);
        }
    }
}

/// <summary>
/// Probes for a usable Postgres once per class rather than once per test, so the skip path stays
/// cheap on a machine with nothing running.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public string ConnectionString { get; } = TestPostgres.AdminConnectionString;

    /// <summary>Why the database is unusable, or null when it is usable.</summary>
    public string? UnavailableReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        await using var context = new MimirDbContext(new DbContextOptionsBuilder<MimirDbContext>()
            .UseNpgsql(ConnectionString, npgsql => npgsql.UseVector())
            .Options);

        try
        {
            await context.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await context.Database.CloseConnectionAsync();
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.Message;
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
