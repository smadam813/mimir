using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Storage;

public class StorageQueriesTests
{
    [Fact]
    public void NoTables_ProducesNoQuery()
    {
        // The empty state on a fresh database: migrations have run but no domain tables exist yet.
        StorageQueries.RowCounts([]).ShouldBeNull();
    }

    [Fact]
    public void TheMigrationsHistoryTable_IsNotReportedAsData()
    {
        StorageQueries.RowCounts(["__EFMigrationsHistory"]).ShouldBeNull();
    }

    [Fact]
    public void EachTableBecomesOneCountedLeg()
    {
        var sql = StorageQueries.RowCounts(["episodes", "wisdom"]).ShouldNotBeNull();

        sql.ShouldContain("""SELECT 'episodes' AS table_name, count(*) AS row_count FROM "episodes" """.TrimEnd());
        sql.ShouldContain("""SELECT 'wisdom' AS table_name, count(*) AS row_count FROM "wisdom" """.TrimEnd());
        sql.ShouldContain("UNION ALL");
    }

    [Theory]
    [InlineData("weird\"name", "\"weird\"\"name\"")]
    [InlineData("Wisdom", "\"Wisdom\"")]
    public void IdentifiersAreQuoted_SoCatalogNamesCanNeverBreakOutOfTheQuery(string table, string expected)
    {
        var sql = StorageQueries.RowCounts([table]).ShouldNotBeNull();

        sql.ShouldContain($"FROM {expected}");
    }

    [Fact]
    public void LabelLiteralsEscapeTheirQuotes()
    {
        var sql = StorageQueries.RowCounts(["o'brien"]).ShouldNotBeNull();

        sql.ShouldContain("""SELECT 'o''brien' AS table_name""");
    }
}
