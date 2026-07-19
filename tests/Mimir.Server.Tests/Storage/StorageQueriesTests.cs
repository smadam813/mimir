using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Storage;

public class StorageQueriesTests
{
    [Fact]
    public void NoTables_ProducesNoQuery()
    {
        // The empty state on a fresh database: migrations have run but no domain table exists yet.
        // Returning null keeps that state entirely free of a second round trip.
        StorageQueries.Occupancy([]).ShouldBeNull();
    }

    [Fact]
    public void TheMigrationsHistoryTable_IsNotReportedAsData()
    {
        StorageQueries.Occupancy(["__EFMigrationsHistory"]).ShouldBeNull();
    }

    [Fact]
    public void EachTableBecomesOneOccupancyLeg()
    {
        var sql = StorageQueries.Occupancy(["episodes", "wisdom"]).ShouldNotBeNull();

        sql.ShouldContain("""SELECT 'episodes' AS table_name, EXISTS(SELECT 1 FROM "episodes") """.TrimEnd());
        sql.ShouldContain("""SELECT 'wisdom' AS table_name, EXISTS(SELECT 1 FROM "wisdom") """.TrimEnd());
        sql.ShouldContain("UNION ALL");
    }

    [Fact]
    public void OccupancyNeverCounts()
    {
        // The whole point of ADR-0006: an exact count is unbounded under §10 keep-forever. If this
        // ever fails, someone has quietly put the sequential scan back.
        var sql = StorageQueries.Occupancy(["events"]).ShouldNotBeNull();

        sql.ShouldNotContain("count(", Case.Insensitive);
    }

    [Theory]
    [InlineData("weird\"name", "\"weird\"\"name\"")]
    [InlineData("Wisdom", "\"Wisdom\"")]
    public void IdentifiersAreQuoted_SoCatalogNamesCanNeverBreakOutOfTheQuery(string table, string expected)
    {
        var sql = StorageQueries.Occupancy([table]).ShouldNotBeNull();

        sql.ShouldContain($"FROM {expected}");
    }

    [Fact]
    public void LabelLiteralsEscapeTheirQuotes()
    {
        var sql = StorageQueries.Occupancy(["o'brien"]).ShouldNotBeNull();

        sql.ShouldContain("""SELECT 'o''brien' AS table_name""");
    }

    [Fact]
    public void DiscoveryExcludesPartitionChildren_SoAPartitionedTableIsCountedOnce()
    {
        // pg_tables returns parents AND children; summing both double-counts a partitioned table
        // (measured: 50,000 real rows reported as 100,000). Reading pg_class lets us exclude the
        // children and roll their size up into the parent instead.
        StorageQueries.TableFootprints.ShouldContain("NOT c.relispartition");
        StorageQueries.TableFootprints.ShouldContain("c.relkind IN ('r', 'p')");
        StorageQueries.TableFootprints.ShouldNotContain("pg_tables");
    }

    [Fact]
    public void DiscoveryRollsUpPartitionSizes_OnlyForPartitionedParents()
    {
        // pg_partition_tree returns ZERO rows for an ordinary table, so an unconditional rollup
        // would report every plain table as 0 bytes — and, to anything that treats 0 bytes as
        // proof of emptiness, as proven empty. The CASE on relkind is load-bearing, not defensive.
        StorageQueries.TableFootprints.ShouldContain("CASE WHEN c.relkind = 'p'");
        StorageQueries.TableFootprints.ShouldContain("pg_partition_tree");
        StorageQueries.TableFootprints.ShouldContain("ELSE pg_total_relation_size(c.oid)");
    }
}
