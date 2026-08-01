using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Contracts.Health;
using Mimir.Server.Components.Health;
using Mimir.Server.Health;

namespace Mimir.Server.Tests.Components.Health;

/// <summary>
/// The §8 health pill, and with it the <see cref="HealthAwareComponent"/> base both health
/// consumers inherit. The real <see cref="HealthState"/> rather than a fake: it is a public
/// concrete class with an <c>Update</c> a test can call, and the thing under test on half of these
/// is the subscription between the two — a fake would be pinning the test's own wiring.
/// <para>
/// Disconnected tier: the probes push snapshots, nothing here reads a database.
/// </para>
/// </summary>
public class HealthPillTests : RenderTestBase
{
    private readonly HealthState _health = new();

    public HealthPillTests() => Services.AddSingleton<IHealthState>(_health);

    private static HealthSnapshot With(HealthTileState state) => HealthSnapshot.Pending with
    {
        Storage = new StorageTile { State = state, Summary = "Postgres" },
    };

    /// <summary>
    /// The pill's dot reports how the install is doing in three buckets and nothing else. The hue
    /// that is missing is the point: `--color-danger` belongs to Delete and orphaned Provenance
    /// (#86), so a probe that is merely unhappy must not borrow the vocabulary of a hard delete.
    /// Read across every state the contract has, so a fifth one added tomorrow either falls into a
    /// bucket or lands here.
    /// </summary>
    [Fact]
    public void TheDot_ReportsInThreeBucketsAndNeverBorrowsTheDangerHue()
    {
        var classes = Enum.GetValues<HealthTileState>()
            .Select(state =>
            {
                _health.Update(_ => With(state));
                return Render<HealthPill>().Find("span.health-dot").ClassList
                    .Single(c => c.StartsWith("dot-", StringComparison.Ordinal));
            })
            .ToArray();

        classes.Distinct(StringComparer.Ordinal)
            .ShouldBe(["dot-working", "dot-degraded", "dot-neutral"], ignoreOrder: true);
    }

    /// <summary>
    /// A probe publishes on whatever background thread it ran on, never the circuit's dispatcher,
    /// so the base class hops onto it before re-rendering. Pushed from a genuine thread-pool
    /// thread rather than inline, because that is where the hop is either made or missed — the
    /// renderer refuses a render it was not dispatched onto, so a base class that dropped the hop
    /// fails here rather than in production a week later.
    /// </summary>
    [Fact]
    public async Task AProbePushFromABackgroundThread_ReachesTheCircuit()
    {
        var pill = Render<HealthPill>();
        pill.Find("button.health-pill").TextContent.ShouldContain("Starting up");

        await Task.Run(
            () => _health.Update(_ => With(HealthTileState.Degraded)),
            TestContext.Current.CancellationToken);

        pill.WaitForAssertion(
            () => pill.Find("button.health-pill").TextContent.ShouldContain("Needs attention"));
    }

    /// <summary>
    /// And it lets go: a pill torn down with its circuit stops being re-rendered against, rather
    /// than leaving the probe holding a handle to a disposed component for the life of the process.
    /// </summary>
    [Fact]
    public void ADisposedPill_StopsBeingPushedTo()
    {
        var pill = Render<HealthPill>();

        pill.Instance.Dispose();
        _health.Update(_ => With(HealthTileState.Degraded));

        pill.Find("button.health-pill").TextContent.ShouldContain("Starting up");
    }

    /// <summary>
    /// ADR-0006, said on screen: a table's bytes do not tell you whether it holds anything, so the
    /// popover states occupancy in words and keeps "we could not tell" distinct from "there is
    /// nothing there". The two rows below carry the same byte figure precisely so the figure
    /// cannot be what tells them apart.
    /// </summary>
    [Fact]
    public void TheTableRows_StateEmptinessInWordsAndNeverInferItFromBytes()
    {
        _health.Update(_ => HealthSnapshot.Pending with
        {
            Storage = new StorageTile
            {
                State = HealthTileState.Ready,
                Summary = "Postgres",
                Tables =
                [
                    new TableFootprint("wisdom", 28 * 1024 * 1024, TableOccupancy.Empty),
                    new TableFootprint("events", 28 * 1024 * 1024, TableOccupancy.Unknown),
                ],
            },
        });

        var pill = Render<HealthPill>();

        var rows = pill.FindAll("div.health-detail-row")
            .ToDictionary(r => r.QuerySelector("span.health-detail-name")!.TextContent,
                          r => r.QuerySelector("span.health-detail-value")!.TextContent);
        rows["wisdom"].ShouldEndWith("· empty");
        rows["events"].ShouldEndWith("· occupancy unknown");
    }

    /// <summary>
    /// The popover is the platform's, not the circuit's: the pill is a <c>popovertarget</c>
    /// invoker with no Blazor handler on it, so opening and closing — light dismiss and Escape
    /// included — costs no round trip and works while the circuit is reconnecting. What the
    /// popover then *looks* like is CSS and stays with the stylesheet scans (#130).
    /// </summary>
    [Fact]
    public void OpeningThePopover_NeverTouchesTheCircuit()
    {
        var pill = Render<HealthPill>();

        var invoker = pill.Find("button.health-pill");
        invoker.GetAttribute("popovertarget").ShouldBe("health-popover");
        invoker.Attributes.Select(a => a.Name)
            .ShouldNotContain(name => name.StartsWith("blazor:onclick", StringComparison.Ordinal));
        pill.Find("section#health-popover").HasAttribute("popover").ShouldBeTrue();
    }
}
