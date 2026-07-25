using Mimir.Server.Recall;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The one §7 injection label line, shared by the ambient lanes and <c>mimir_search</c>'s Wisdom
/// leg. Pure by construction — no database — so the date rule fails on every machine, not only one
/// with Postgres up.
/// </summary>
public class InjectionLabelTests
{
    /// <summary>
    /// 2026-07-02 01:30 at +13:00 is 2026-07-01 12:30 UTC: the two readings name different
    /// calendar days, so a label formatting in the value's own offset cannot pass this.
    /// </summary>
    private static readonly DateTimeOffset AcrossTheDateLine =
        new(2026, 7, 2, 1, 30, 0, TimeSpan.FromHours(13));

    [Fact]
    public void Line_RendersTheConfirmedDateInUtc_NotTheValuesOwnOffset()
    {
        var line = InjectionLabel.Line(
            WisdomKind.Lesson, "Global", AcrossTheDateLine, "Prefer rebase over merge.");

        line.ShouldBe("- [Lesson · Global · confirmed 2026-07-01] Prefer rebase over merge.\n");
    }

    [Fact]
    public void Date_IsTheUtcCalendarDay_WhateverOffsetTheValueCarries()
    {
        InjectionLabel.Date(AcrossTheDateLine).ShouldBe("2026-07-01");
        InjectionLabel.Date(AcrossTheDateLine.ToUniversalTime()).ShouldBe("2026-07-01");
        InjectionLabel.Date(AcrossTheDateLine.ToOffset(TimeSpan.FromHours(-11))).ShouldBe("2026-07-01");
    }

    [Fact]
    public void Line_TakesItsScopeTextFromTheCaller()
    {
        var ambient = InjectionLabel.Line(
            WisdomKind.Preference, "this project", AcrossTheDateLine, "Squash before merging.");
        var deliberate = InjectionLabel.Line(
            WisdomKind.Preference, "mimir", AcrossTheDateLine, "Squash before merging.");

        ambient.ShouldStartWith("- [Preference · this project · confirmed ");
        deliberate.ShouldStartWith("- [Preference · mimir · confirmed ");
    }

    [Fact]
    public void Line_CarriesTheCallersExtraTag_InsideTheBracket()
    {
        var line = InjectionLabel.Line(
            WisdomKind.Fact,
            "mimir",
            AcrossTheDateLine,
            "Postgres is the single store.",
            extra: $" · Retired {InjectionLabel.Date(AcrossTheDateLine)}");

        line.ShouldBe(
            "- [Fact · mimir · confirmed 2026-07-01 · Retired 2026-07-01] Postgres is the single store.\n");
    }
}
