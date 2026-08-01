using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Storage;

public class StorageQueriesTests
{
    [Fact]
    public void NoTables_ProducesNoQuery()
    {
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
        StorageQueries.TableFootprints.ShouldContain("NOT c.relispartition");
        StorageQueries.TableFootprints.ShouldContain("c.relkind IN ('r', 'p')");
        StorageQueries.TableFootprints.ShouldNotContain("pg_tables");
    }

    [Fact]
    public void DiscoveryRollsUpPartitionSizes_OnlyForPartitionedParents()
    {
        StorageQueries.TableFootprints.ShouldContain("CASE WHEN c.relkind = 'p'");
        StorageQueries.TableFootprints.ShouldContain("pg_partition_tree");
        StorageQueries.TableFootprints.ShouldContain("ELSE pg_total_relation_size(c.oid)");
    }
}
