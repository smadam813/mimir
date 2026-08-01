using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Ui;

namespace Mimir.Server.Tests;

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
    public async Task TheProjectSeeder_GivesEachCallItsOwnIdentityAndRoot_ButNotItsOwnDisplayName()
    {
        var first = await AddProjectAsync();
        var second = await AddProjectAsync();

        second.Identity.ShouldNotBe(
            first.Identity, "§3.1 identity-matching would weld a test's second Project onto its first");
        second.RootPaths.ShouldNotBe(
            first.RootPaths, "§3.1 root-matching would weld a test's second Project onto its first");
        second.DisplayName.ShouldBe(
            first.DisplayName, "nothing makes DisplayName unique: name them apart when filtering by it");
        (await FromDb(db => db.Projects.CountAsync(Token))).ShouldBe(3);
    }

    [Fact]
    public void TheIdentityAndRootHelpers_AnswerAFreshValueEachCall()
    {
        Identity("same").ShouldNotBe(
            Identity("same"), "a resolver test hands two identities in and pins how they resolve "
            + "against each other — a repeat would be §3.1 matching them onto one row instead");
        Root("C", "same").ShouldNotBe(Root("C", "same"), "and the same for the root they match on");
    }

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

    private async Task AssertGlobalIsPristineThenMutateItAsync()
    {
        var global = await Context.Projects.SingleAsync(p => p.Id == Project.GlobalId, Token);
        global.DisplayName.ShouldBe("Global", "a sibling's rename must not have survived its test");
        global.RootPaths.ShouldBeEmpty("nor a sibling's appended root");

        global.DisplayName = "renamed by a test";
        global.RootPaths = [@"C:\git\somewhere"];
        await Context.SaveChangesAsync(Token);
    }

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
