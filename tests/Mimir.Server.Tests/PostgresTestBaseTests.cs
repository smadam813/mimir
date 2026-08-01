using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests;

/// <summary>
/// The harness pinning itself. The two seeding tests are a deliberate pollution pair: each seeds
/// one row in every mutable table and asserts the whole table holds exactly that, so with the
/// per-test reset gone whichever runs second goes red — on every machine, in either order. Without
/// this pair a broken reset would not fail here at all; it would resurface as somebody else's
/// order-dependent flake on CI, which is the #20/#22 failure mode the harness exists to end.
/// </summary>
public sealed class PostgresTestBaseTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task EveryTableHoldsOnlyThisTestsRows_First()
        => await SeedOneOfEverythingAsync();

    [Fact]
    public async Task EveryTableHoldsOnlyThisTestsRows_Second()
        => await SeedOneOfEverythingAsync();

    [Fact]
    public async Task AMutatedGlobalRow_DoesNotOutliveItsTest_First()
        => await AssertGlobalIsPristineThenMutateItAsync();

    [Fact]
    public async Task AMutatedGlobalRow_DoesNotOutliveItsTest_Second()
        => await AssertGlobalIsPristineThenMutateItAsync();

    [Fact]
    public async Task AfterTheReset_TheGlobalPseudoProjectIsTheOnlyProject()
    {
        var projects = await FromDb(db => db.Projects.ToListAsync(Token));

        var global = projects.ShouldHaveSingleItem();
        global.Id.ShouldBe(Project.GlobalId, "§3's Global scope is a fixed id every ticket relies on");
        global.Identity.ShouldBe(Project.GlobalIdentity);
        global.DisplayName.ShouldBe("Global");
        global.RootPaths.ShouldBeEmpty();
    }

    /// <summary>
    /// The second pollution pair, for the one row the reset restores rather than deletes. Global is
    /// the single survivor of the truncate, so it is the single way a test's writes could still
    /// reach the next one: assert it pristine, then rename it and hand it a root — exactly what a
    /// resolver or merger path would do to a Project — and let the sibling assert first.
    /// </summary>
    private async Task AssertGlobalIsPristineThenMutateItAsync()
    {
        var global = await Context.Projects.SingleAsync(p => p.Id == Project.GlobalId, Token);
        global.DisplayName.ShouldBe("Global", "a sibling's rename must not have survived its test");
        global.RootPaths.ShouldBeEmpty("nor a sibling's appended root");

        global.DisplayName = "renamed by a test";
        global.RootPaths = [@"C:\git\somewhere"];
        await Context.SaveChangesAsync(Token);
    }

    /// <summary>
    /// One row in each mutable table, then the whole-table counts. Shared by both halves of the
    /// pollution pair so the two are provably the same test twice — the property under test is
    /// that running it twice changes nothing.
    /// </summary>
    private async Task SeedOneOfEverythingAsync()
    {
        var project = await AddProjectAsync();
        var episode = await AddEpisodeAsync(project.Id, sealedAt: Now.AddHours(-1));
        var evt = await AddEventAsync(episode.Id, seq: 1);
        var item = await AddHarvestedItemAsync(project.Id);
        var wisdom = await AddWisdomAsync(project.Id, "the only Wisdom in the database");
        await AddProvenanceAsync(wisdom.Id, episode.Id, evt.Id, item.Id);
        var injection = await AddInjectionAsync(
            project.Id, episode.SessionId, items: [(wisdom.Id, 0.03)]);
        await AddGoldenCaseAsync(
            project.Id,
            wisdom.Id,
            createdFromInjectionId: injection.Id,
            note: "the only case in the database");

        (await FromDb(db => db.Projects.CountAsync(Token)))
            .ShouldBe(2, "this test's Project and the Global pseudo-project, and nothing else");
        (await FromDb(db => db.Episodes.CountAsync(Token))).ShouldBe(1);
        (await FromDb(db => db.Events.CountAsync(Token))).ShouldBe(1);
        (await FromDb(db => db.HarvestedItems.CountAsync(Token))).ShouldBe(1);
        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(1);
        (await FromDb(db => db.WisdomVersions.CountAsync(Token))).ShouldBe(1);
        (await FromDb(db => db.Provenance.CountAsync(Token))).ShouldBe(1);
        (await FromDb(db => db.Injections.CountAsync(Token))).ShouldBe(1);
        (await FromDb(db => db.GoldenCases.CountAsync(Token))).ShouldBe(1);
    }

    /// <summary>
    /// Every §8 service a surface can <c>@inject</c> resolves from the render tier's container
    /// (#130). Written as a resolution sweep over <c>AddMimirUi</c>'s own descriptors rather than a
    /// hand-list, because a hand-list is what went wrong: the tier shipped registering neither
    /// <see cref="TimeProvider"/> nor <c>MergeGate</c>, so three of the four browsers threw at
    /// first render while the only surface pinned so far — the Episode list — resolved fine and
    /// said nothing. The failure would have landed on whoever wrote the next render test, against
    /// a rules file promising it could not happen.
    ///
    /// It reads the descriptors, so a §8 service added to <c>AddMimirUi</c> tomorrow is swept the
    /// day it is registered. The clock is asserted to be this class's <see cref="Clock"/> and not
    /// merely present: <c>TimeProvider.System</c> resolves just as well and would leave a surface
    /// reading a different "now" from every other SUT the harness composes, which is a wrong
    /// timestamp in a render assertion rather than an exception anybody would trace back here.
    /// </summary>
    [Fact]
    public void TheRenderTier_ResolvesEverySection8ServiceASurfaceCanInject()
    {
        var render = CreateRenderContext();

        foreach (var serviceType in new ServiceCollection().AddMimirUi().Select(d => d.ServiceType))
        {
            Should.NotThrow(
                () => render.Services.GetRequiredService(serviceType),
                $"a surface injecting {serviceType.Name} must render on this tier");
        }

        render.Services.GetRequiredService<TimeProvider>().ShouldBeSameAs(Clock);
    }
}
