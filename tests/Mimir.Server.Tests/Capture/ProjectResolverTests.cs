using Microsoft.EntityFrameworkCore;
using Mimir.Server.Capture;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Capture;

public sealed class ProjectResolverTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task AnUnseenIdentity_CreatesTheProjectAtItsRoot()
    {
        var leaf = $"fresh-{Guid.NewGuid():N}";
        var identity = $"github.com/test/{leaf}";

        var project = await Resolve(identity, @"C:\git\fresh");

        project.Identity.ShouldBe(identity);
        project.RootPaths.ShouldBe([@"C:\git\fresh"]);
        project.DisplayName.ShouldBe(leaf);
    }

    [Fact]
    public async Task TheSameIdentityTwice_IsOneProject()
    {
        var identity = Identity("same");

        var first = await Resolve(identity, @"C:\git\same");
        var second = await Resolve(identity, @"C:\git\same");

        second.Id.ShouldBe(first.Id);
        (await Count(identity)).ShouldBe(1);
    }

    [Fact]
    public async Task ANewRootForAKnownIdentity_IsAppendedNotDuplicated()
    {
        var identity = Identity("clones");

        var first = await Resolve(identity, @"C:\git\clones");
        var second = await Resolve(identity, @"D:\work\clones");

        second.Id.ShouldBe(first.Id);
        second.RootPaths.ShouldBe([@"C:\git\clones", @"D:\work\clones"]);
    }

    [Fact]
    public async Task AKnownRootWithADifferentRemoteIdentity_MatchesByRoot_AndKeepsItsStoredIdentity()
    {
        var root = @"C:\git\pathborn";
        var remoteIdentity = Identity("pathborn-root");
        var born = await Resolve(remoteIdentity, root);

        var found = await Resolve(Identity("pathborn-remote"), root);

        found.Id.ShouldBe(born.Id);
        found.Identity.ShouldBe(remoteIdentity);
    }

    [Fact]
    public async Task ARootAppendedByAnotherContext_SurvivesThisContextsAppend()
    {
        var identity = Identity("racing");
        await Resolve(identity, @"C:\git\racing");

        await using (var other = CreateContext())
        {
            var raced = await other.Projects.SingleAsync(p => p.Identity == identity, Token);
            raced.RootPaths = [.. raced.RootPaths, @"D:\work\racing"];
            await other.SaveChangesAsync(Token);
        }

        var resolved = await Resolve(identity, @"E:\mirror\racing");

        resolved.RootPaths.ShouldBe(
            [@"C:\git\racing", @"D:\work\racing", @"E:\mirror\racing"],
            "the returned Project must reflect the merged array");
        var persisted = await FromDb(db => db.Projects.SingleAsync(p => p.Identity == identity, Token));
        persisted.RootPaths.ShouldBe([@"C:\git\racing", @"D:\work\racing", @"E:\mirror\racing"]);
    }

    [Fact]
    public async Task APathIdentityProject_ReportingARemote_IsUpgradedInPlace()
    {
        var root = $@"C:\git\upgrade-{Guid.NewGuid():N}";
        var born = await Resolve(root, root);
        var remote = Identity("upgraded");

        var upgraded = await Resolve(remote, root);

        upgraded.Id.ShouldBe(born.Id);
        upgraded.Identity.ShouldBe(remote);
        upgraded.RootPaths.ShouldBe([root]);
        upgraded.DisplayName.ShouldBe(remote.Split('/')[^1], "the display name follows the identity");
    }

    [Fact]
    public async Task APathIdentityProject_SeenAtASecondRootWithoutARemote_KeepsItsPathIdentity()
    {
        var rootA = $@"C:\git\stay-{Guid.NewGuid():N}";
        var rootB = $@"D:\work\stay-{Guid.NewGuid():N}";
        var born = await Resolve(rootA, rootA);
        await Resolve(rootA, rootB);

        var found = await Resolve(rootB, rootB);

        found.Id.ShouldBe(born.Id);
        found.Identity.ShouldBe(rootA);
    }

    [Fact]
    public async Task APathIdentityProject_GetsItsDisplayNameFromTheLastSegment()
    {
        var project = await Resolve(@"C:\somewhere\deep\toolbox", @"C:\somewhere\deep\toolbox");

        project.DisplayName.ShouldBe("toolbox");
    }

    [Fact]
    public async Task TwoIdentitiesFromTheHelper_ResolveToTwoProjects()
    {
        var first = await Resolve(Identity("same"), @"C:\git\first");
        var second = await Resolve(Identity("same"), @"C:\git\second");

        second.Id.ShouldNotBe(
            first.Id,
            "one test resolves several identities against one another, so the helper answers a "
            + "fresh one each call — a repeated identity would be §3.1 matching them onto one row");
    }

    private async Task<Project> Resolve(string identity, string root)
        => await new ProjectResolver(Context).ResolveAsync(identity, root, Token);

    private async Task<int> Count(string identity)
        => await Context.Projects.CountAsync(p => p.Identity == identity, Token);

    private static string Identity(string name) => $"github.com/test/{name}-{Guid.NewGuid():N}";
}
