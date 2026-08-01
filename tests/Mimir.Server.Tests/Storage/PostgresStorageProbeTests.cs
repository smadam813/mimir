using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mimir.Contracts.Health;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Storage;

public sealed class PostgresStorageProbeTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task AnalyzedWhileEmptyThenPopulated_ReportsPopulated()
    {
        // Measured on Postgres in this exact state — created empty, analyzed, then written to,
        // which is the shape an EF migration produces: reltuples = 0, relpages = 0,
        // n_live_tup = 0, with 200,000 rows present.
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
        // Measured after a full DELETE: n_live_tup still reported 50,000 and reltuples 200,000,
        // with the table genuinely empty.
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
        // pg_partition_tree returns zero rows for an ordinary table.
        var table = await ScratchTable();
        await ExecuteAsync($"INSERT INTO \"{table}\" SELECT g, repeat('x', 100) FROM generate_series(1, 20000) g;");

        (await ProbeFor(table)).TotalBytes.ShouldBeGreaterThan(1_000_000);
    }

    [Fact]
    public async Task APartitionedTableIsDiscoveredOnceUnderItsParentName()
    {
        // pg_tables returns parents AND children; summing both was measured reporting 50,000 real
        // rows as 100,000.
        var parent = Name("part");
        var child = $"{parent}_p1";

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
        // Both sides are seeded rather than left to whatever else the database holds: a truncated
        // mapped table keeps its index pages and never reads as zero-byte, and a text column
        // would bring a TOAST index whose metapage alone is 8 KB.
        var empty = Name("void");
        await ExecuteAsync($"CREATE TABLE \"{empty}\" (id int);");
        var written = await ScratchTable();
        await ExecuteAsync($"INSERT INTO \"{written}\" SELECT g, repeat('x', 100) FROM generate_series(1, 20000) g;");

        var tile = await Probe();

        tile.Tables.Single(t => t.Table == empty).TotalBytes.ShouldBe(
            0, "an unindexed, unwritten table holds no pages — the case the invariant sweeps");
        tile.Tables.Single(t => t.Table == written).Occupancy.ShouldBe(TableOccupancy.Populated);
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
        var tile = await probe.ProbeAsync(Token);

        tile.State.ShouldBe(HealthTileState.Ready, tile.Summary);
        return tile;
    }

    private async Task<TableFootprint> ProbeFor(string table)
    {
        var tile = await Probe();
        tile.Tables.ShouldContain(t => t.Table == table, $"table {table} was never discovered");
        return tile.Tables.Single(t => t.Table == table);
    }

    private async Task<string> ScratchTable()
    {
        var table = Name("tbl");
        await ExecuteAsync($"CREATE TABLE \"{table}\" (id int, payload text);");
        return table;
    }

    private static string Name(string kind) => $"wf_{kind}_{Guid.NewGuid():N}"[..24];

    private Task ExecuteAsync(string sql) => Context.Database.ExecuteSqlRawAsync(sql, Token);
}
