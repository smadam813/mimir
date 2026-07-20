using Microsoft.EntityFrameworkCore;
using Mimir.Server.Capture;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// Spec §3.1 server side: match a Project by identity, else by a known root, create it when new,
/// and remember every root it has been seen at. (Identity upgrade and clone merge are a follow-up
/// ticket — a root match deliberately leaves the stored identity alone.)
/// </summary>
public sealed class ProjectResolverTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private MimirDbContext? _context;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheGlobalPseudoProject_IsSeededByTheMigration()
    {
        var global = await Context.Projects.SingleAsync(
            p => p.Id == Project.GlobalId, TestContext.Current.CancellationToken);

        global.Identity.ShouldBe(Project.GlobalIdentity);
        global.DisplayName.ShouldBe("Global");
        global.RootPaths.ShouldBeEmpty("the Global pseudo-project holds no Episodes and lives at no root");
    }

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
    public async Task AKnownRootWithADifferentIdentity_MatchesByRoot_AndKeepsItsStoredIdentity()
    {
        // The §3.1 fallback order: a Project first seen without a remote carries its root path as
        // identity; when hook traffic later reports the real remote, the root still finds it.
        // Upgrading the identity in place is the follow-up ticket, not this resolver.
        var root = @"C:\git\pathborn";
        var pathIdentity = Identity("pathborn-root");
        var born = await Resolve(pathIdentity, root);

        var found = await Resolve(Identity("pathborn-remote"), root);

        found.Id.ShouldBe(born.Id);
        found.Identity.ShouldBe(pathIdentity);
    }

    [Fact]
    public async Task APathIdentityProject_GetsItsDisplayNameFromTheLastSegment()
    {
        var project = await Resolve(@"C:\somewhere\deep\toolbox", @"C:\somewhere\deep\toolbox");

        project.DisplayName.ShouldBe("toolbox");
    }

    private async Task<Project> Resolve(string identity, string root)
        => await new ProjectResolver(Context).ResolveAsync(identity, root, TestContext.Current.CancellationToken);

    private async Task<int> Count(string identity)
        => await Context.Projects.CountAsync(p => p.Identity == identity, TestContext.Current.CancellationToken);

    /// <summary>Unique per run: the fixture database is shared by every test in this class.</summary>
    private static string Identity(string name) => $"github.com/test/{name}-{Guid.NewGuid():N}";

    private MimirDbContext Context
    {
        get
        {
            if (fixture.UnavailableReason is { } reason)
            {
                Assert.Skip($"No Postgres reachable for integration tests ({reason}). "
                    + "Run `docker compose up -d postgres`, or set MIMIR_TEST_POSTGRES.");
            }

            return _context ??= fixture.CreateContext();
        }
    }
}
