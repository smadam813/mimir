using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage.Entities;

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
        var injection = new Injection
        {
            Id = Guid.CreateVersion7(),
            SessionId = episode.SessionId,
            ProjectId = project.Id,
            At = Now,
            Lane = InjectionLane.Prompt,
            QueryContext = "a prompt",
            Chars = 240,
            Items = [new InjectionItem { WisdomId = wisdom.Id, Score = 0.03 }],
        };
        Context.Injections.Add(injection);
        Context.GoldenCases.Add(new GoldenCase
        {
            Id = Guid.CreateVersion7(),
            QueryContext = "a prompt",
            ProjectId = project.Id,
            ExpectedWisdomId = wisdom.Id,
            CreatedFromInjectionId = injection.Id,
            Note = "the only case in the database",
        });
        await Context.SaveChangesAsync(Token);

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
}
