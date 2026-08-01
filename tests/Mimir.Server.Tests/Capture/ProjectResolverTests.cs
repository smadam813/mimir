using Microsoft.EntityFrameworkCore;
using Mimir.Server.Capture;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// Spec §3.1 server side: match a Project by identity, else by a known root, create it when new,
/// remember every root it has been seen at, and upgrade a path-born Project in place when hook
/// traffic first reports its remote identity. (The collision case — clone merge — is
/// <see cref="ProjectMergeTests"/>.)
/// </summary>
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
        // Only a path-born Project is ever upgraded (§3.1). A Project that already carries a
        // remote identity keeps it when a hook reports a different remote from a shared root —
        // re-identifying an established repository on one stray hook would be irreversible.
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
        // Two sessions can race the same Project with different new roots. This context still
        // tracks the stale array; its append must not overwrite the other's (§3.1 — roots
        // accumulate, they are how a Project is found again).
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
        // §3.1 identity upgrade: identity follows the repository. A Project born without a remote
        // carries its root as identity; the first hook that knows the real remote fixes the row —
        // same row, id stable, roots kept.
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
        // A hook that knows no remote sends its root as identity (§3.1 fallback). That reveals
        // nothing about the repository — a path-born Project keeps its birth root as identity.
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
    public async Task APathBornRivalAtAnAlreadyKnownRoot_IsStillHealed()
    {
        // The Harvester can mint a path-born Project at a root a remote-identity Project already
        // lists, so the resolve that heals it appends nothing. Nothing else catches the duplicate:
        // root_paths carries no unique index.
        var remote = Identity("known-root");
        var root = Root("C", "known-root");
        var survivor = await Resolve(remote, root);
        var rival = await AddProjectAsync(root, [root]);

        var resolved = await Resolve(remote, root);

        resolved.Id.ShouldBe(survivor.Id);
        (await FromDb(db => db.Projects.AnyAsync(p => p.Id == rival.Id, Token)))
            .ShouldBeFalse("the rival at an already-known root is merged away too");
    }

    [Fact]
    public async Task ARemoteIdentityHolderOfTheRoot_NeverMasksThePathBornRival()
    {
        // Three Projects list the root: the one being resolved, another remote-identity clone, and
        // the path-born row. Only the path-born one is the merge's loser.
        var remote = Identity("masking");
        var root = Root("C", "masking");
        var survivor = await AddProjectAsync(remote, [Root("C", "masking-home"), root]);
        var remoteHolder = await AddProjectAsync(Identity("masking-other"), [Root("D", "masking-other"), root]);
        var pathBorn = await AddProjectAsync(root, [root]);

        var resolved = await Resolve(remote, root);

        resolved.Id.ShouldBe(survivor.Id);
        (await FromDb(db => db.Projects.AnyAsync(p => p.Id == pathBorn.Id, Token)))
            .ShouldBeFalse("the path-born rival is the loser");
        (await FromDb(db => db.Projects.AnyAsync(p => p.Id == remoteHolder.Id, Token)))
            .ShouldBeTrue("a Project carrying its own remote identity is never merged away");
    }

    [Fact]
    public async Task ALostCreateRace_ResumesTheWinnersProject()
    {
        var identity = Identity("create-race");
        var root = Root("C", "create-race");

        var winning = new Project
        {
            Id = Guid.CreateVersion7(),
            Identity = identity,
            RootPaths = [Root("D", "create-race-winner")],
            DisplayName = "winner",
        };

        // Neither query sees the uncommitted row, so the losing resolve inserts and blocks on the
        // unique identity index until the winner commits.
        var resolved = await RaceAsync(
            winner =>
            {
                winner.Projects.Add(winning);
                return winner.SaveChangesAsync(Token);
            },
            () => Resolve(identity, root));

        resolved.Id.ShouldBe(winning.Id);
        (await Count(identity)).ShouldBe(1);
    }

    [Fact]
    public async Task AnUpgradeCollidingOnTheRemoteIdentity_ResolvesToTheSurvivor()
    {
        var remote = Identity("collide");
        var root = Root("C", "collide");
        await Resolve(root, root);

        var survivor = new Project
        {
            Id = Guid.CreateVersion7(),
            Identity = remote,
            RootPaths = [Root("D", "collide-survivor")],
            DisplayName = "survivor",
        };

        // The identity query misses the uncommitted row, so the losing resolve matches by root and
        // tries the upgrade, blocking on the unique index until the survivor commits.
        var resolved = await RaceAsync(
            winner =>
            {
                winner.Projects.Add(survivor);
                return winner.SaveChangesAsync(Token);
            },
            () => Resolve(remote, root));

        resolved.Id.ShouldBe(survivor.Id);
        resolved.RootPaths.ShouldContain(root, "the re-read resolves onto the survivor, clones and all");
        (await FromDb(db => db.Projects.CountAsync(p => p.RootPaths.Contains(root), Token)))
            .ShouldBe(1, "the losing upgrade leaves one Project holding the root");
    }

    [Fact]
    public async Task AnUpgradeThatARivalAlreadyWon_ChangesNothing()
    {
        // This resolver still tracks the Project as path-born while another context upgrades it.
        // First upgrade wins: the stale one must not re-identify an established repository.
        var root = Root("C", "first-wins");
        var stale = new ProjectResolver(Context);
        await stale.ResolveAsync(root, root, Token);
        var winning = Identity("first-wins-winner");
        await using (var other = CreateContext())
        {
            await new ProjectResolver(other).ResolveAsync(winning, root, Token);
        }

        var resolved = await stale.ResolveAsync(Identity("first-wins-loser"), root, Token);

        resolved.Identity.ShouldBe(winning);
        var persisted = await FromDb(db => db.Projects.SingleAsync(p => p.Id == resolved.Id, Token));
        persisted.Identity.ShouldBe(winning);
        persisted.DisplayName.ShouldBe(winning.Split('/')[^1]);
    }

    [Fact]
    public async Task AMergeRacingAConcurrentReference_RollsBackAndRetries()
    {
        var remote = Identity("fk-race");
        var rootA = Root("C", "fk-race-a");
        var rootB = Root("D", "fk-race-b");
        var survivor = await Resolve(remote, rootA);
        var loser = await Resolve(rootB, rootB);

        var straggler = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{Guid.NewGuid():N}",
            ProjectId = loser.Id,
            StartedAt = Now,
            Cwd = rootB,
            Distillation = DistillationState.Pending,
        };

        // The insert holds a key-share lock on the loser's row, so the merge's DELETE blocks; once
        // it commits the delete sees a referencing Episode and the whole merge rolls back.
        var merged = await RaceAsync(
            concurrent =>
            {
                concurrent.Episodes.Add(straggler);
                return concurrent.SaveChangesAsync(Token);
            },
            () => Resolve(remote, rootB));

        merged.Id.ShouldBe(survivor.Id);
        (await FromDb(db => db.Projects.AnyAsync(p => p.Id == loser.Id, Token)))
            .ShouldBeFalse("the retried merge removes the loser");
        var repointed = await FromDb(db => db.Episodes.SingleAsync(e => e.Id == straggler.Id, Token));
        repointed.ProjectId.ShouldBe(survivor.Id, "the retry re-points the Episode that caused it");
    }

    private async Task<Project> Resolve(string identity, string root)
        => await new ProjectResolver(Context).ResolveAsync(identity, root, Token);

    private async Task<int> Count(string identity)
        => await Context.Projects.CountAsync(p => p.Identity == identity, Token);
}
