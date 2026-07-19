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
    public void WithTables_TheSummaryCountsTablesAndFlagsTheEmptyOnes()
    {
        var tile = StorageTileFactory.Ready(1024, [
            Table("episodes", TableOccupancy.Populated),
            Table("wisdom", TableOccupancy.Populated),
            Table("injections", TableOccupancy.Empty),
        ]);

        tile.State.ShouldBe(HealthTileState.Ready);
        tile.Summary.ShouldBe("1.0 KB · 3 tables, 1 empty");
    }

    [Fact]
    public void WhenNothingIsEmpty_TheSummaryDoesNotSaySoAtLength()
    {
        var tile = StorageTileFactory.Ready(1024, [Table("episodes", TableOccupancy.Populated)]);

        tile.Summary.ShouldBe("1.0 KB · 1 table", "the strip sits above every surface; it should read properly");
    }

    [Fact]
    public void WhenEveryTableIsEmpty_TheSummarySaysSo()
    {
        // A genuinely empty database must not read the same as a populated one.
        var tile = StorageTileFactory.Ready(8_388_608, [
            Table("episodes", TableOccupancy.Empty),
            Table("wisdom", TableOccupancy.Empty),
        ]);

        tile.Summary.ShouldBe("8.0 MB · 2 tables, all empty");
    }

    [Fact]
    public void WhenAnyTableIsUnknown_TheSummaryMakesNoOccupancyClaim()
    {
        // Counting an Unknown table as populated would reintroduce the prohibited misreport one
        // layer above the contract that defends against it. Say nothing instead.
        var tile = StorageTileFactory.Ready(1024, [
            Table("episodes", TableOccupancy.Populated),
            Table("wisdom", TableOccupancy.Empty),
            Table("events", TableOccupancy.Unknown),
        ]);

        tile.Summary.ShouldBe("1.0 KB · 3 tables");
        tile.Summary.ShouldNotContain("empty");
    }

    [Fact]
    public void TheSummaryNeverInfersEmptinessFromBytes()
    {
        // Measured: a 28 MB heap can hold zero live rows, and a 0-byte table is empty but only
        // EXISTS may say so. Occupancy is carried in words, never derived from the byte figure.
        var tile = StorageTileFactory.Ready(29_360_128, [Table("episodes", TableOccupancy.Empty, 28_254_208)]);

        tile.Summary.ShouldBe("28.0 MB · 1 table, all empty");
    }

    [Fact]
    public void TheDefaultOccupancyIsUnknown()
    {
        // Pinned so a future reordering of the enum cannot make Empty the accidental default of a
        // half-initialised value.
        default(TableOccupancy).ShouldBe(TableOccupancy.Unknown);
    }

    [Fact]
    public void AnUnreachableDatabase_DegradesTheTileAndSaysWhy()
    {
        var tile = StorageTileFactory.Unreachable("connection refused");

        tile.State.ShouldBe(HealthTileState.Degraded);
        tile.Summary.ShouldContain("connection refused");
        tile.DatabaseSizeBytes.ShouldBeNull();
    }

    private static TableFootprint Table(string name, TableOccupancy occupancy, long bytes = 8_192)
        => new(name, bytes, occupancy);
}
