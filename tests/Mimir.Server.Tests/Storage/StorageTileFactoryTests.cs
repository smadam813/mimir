using Mimir.Contracts.Health;
using Mimir.Server.Storage;

namespace Mimir.Server.Tests.Storage;

public class StorageTileFactoryTests
{
    [Fact]
    public void AFreshlyMigratedDatabase_IsReadyWithNoTables()
    {
        // Domain tables belong to later tickets; the empty state is the correct state today.
        var tile = StorageTileFactory.Ready(8_388_608, []);

        tile.State.ShouldBe(HealthTileState.Ready);
        tile.DatabaseSizeBytes.ShouldBe(8_388_608);
        tile.Tables.ShouldBeEmpty();
        tile.Summary.ShouldBe("8.0 MB · no tables yet");
    }

    [Fact]
    public void WithTables_TheSummaryCountsTablesAndRows()
    {
        var tile = StorageTileFactory.Ready(1024, [new TableRowCount("episodes", 12), new TableRowCount("wisdom", 1_500)]);

        tile.State.ShouldBe(HealthTileState.Ready);
        tile.Summary.ShouldBe("1.0 KB · 2 tables, 1,512 rows");
    }

    [Fact]
    public void AnUnreachableDatabase_DegradesTheTileAndSaysWhy()
    {
        var tile = StorageTileFactory.Unreachable("connection refused");

        tile.State.ShouldBe(HealthTileState.Degraded);
        tile.Summary.ShouldContain("connection refused");
        tile.DatabaseSizeBytes.ShouldBeNull();
    }
}
