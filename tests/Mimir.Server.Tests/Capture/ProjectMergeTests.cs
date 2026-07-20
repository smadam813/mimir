using Microsoft.EntityFrameworkCore;
using Mimir.Server.Capture;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Capture;

/// <summary>
/// Spec §3.1 clone merge: when an identity upgrade collides with an existing Project already
/// holding the same remote identity, the two rows merge — every reference re-pointed to the
/// survivor, <c>root_paths</c> unioned, loser removed. Two clones of one repository are one
/// Project.
/// </summary>
public sealed class ProjectMergeTests(CaptureDatabaseFixture fixture)
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
    public async Task ACollidingUpgrade_MergesTheClones_RePointingEpisodes()
    {
        var remote = Identity("clones");
        var rootA = Root("A", "clone-a");
        var rootB = Root("B", "clone-b");
        var survivor = await Resolve(remote, rootA);
        var loser = await Resolve(rootB, rootB);
        var episode = await AddEpisodeTo(loser, rootB);

        var merged = await Resolve(remote, rootB);

        merged.Id.ShouldBe(survivor.Id);
        merged.Identity.ShouldBe(remote);
        merged.RootPaths.ShouldBe([rootA, rootB]);

        await using var fresh = fixture.CreateContext();
        (await fresh.Projects.AnyAsync(p => p.Id == loser.Id, TestContext.Current.CancellationToken))
            .ShouldBeFalse("the loser row is removed by the merge");
        var repointed = await fresh.Episodes.SingleAsync(
            e => e.Id == episode.Id, TestContext.Current.CancellationToken);
        repointed.ProjectId.ShouldBe(survivor.Id);
    }

    [Fact]
    public async Task TheMerge_RePointsReferencesFromTablesThisCodeHasNeverHeardOf()
    {
        // A referencing table that exists only in this test — the stand-in for HarvestedItem,
        // Wisdom scope, Injection and GoldenCase, which arrive with later tickets. It is covered
        // because re-pointing enumerates foreign keys from the database catalog at merge time,
        // not from a hand-list of today's tables. No drop: the fixture database is throwaway, and
        // later merges re-pointing through an emptied extra table is exactly the production shape.
        await Context.Database.ExecuteSqlAsync(
            $"""
            CREATE TABLE IF NOT EXISTS test_future_references (
                id uuid PRIMARY KEY,
                project_id uuid NOT NULL REFERENCES projects (id)
            )
            """,
            TestContext.Current.CancellationToken);

        var remote = Identity("future");
        var rootA = Root("A", "future-a");
        var rootB = Root("B", "future-b");
        var survivor = await Resolve(remote, rootA);
        var loser = await Resolve(rootB, rootB);
        var rowId = Guid.CreateVersion7();
        await Context.Database.ExecuteSqlAsync(
            $"INSERT INTO test_future_references (id, project_id) VALUES ({rowId}, {loser.Id})",
            TestContext.Current.CancellationToken);

        await Resolve(remote, rootB);

        var pointedAt = await Context.Database
            .SqlQuery<Guid>($"""
                SELECT project_id AS "Value" FROM test_future_references WHERE id = {rowId}
                """)
            .SingleAsync(TestContext.Current.CancellationToken);
        pointedAt.ShouldBe(survivor.Id);
    }

    [Fact]
    public async Task TheMerge_KeepsEveryRootOfBothClones()
    {
        var remote = Identity("roots");
        var rootA = Root("A", "roots-a");
        var rootB = Root("B", "roots-b");
        var rootB2 = Root("B", "roots-b2");
        var survivor = await Resolve(remote, rootA);
        await Resolve(rootB, rootB);
        await Resolve(rootB, rootB2);

        var merged = await Resolve(remote, rootB);

        merged.Id.ShouldBe(survivor.Id);
        merged.RootPaths.ShouldBe([rootA, rootB, rootB2]);
    }

    private async Task<Project> Resolve(string identity, string root)
        => await new ProjectResolver(Context).ResolveAsync(identity, root, TestContext.Current.CancellationToken);

    private async Task<Episode> AddEpisodeTo(Project project, string cwd)
    {
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"session-{Guid.NewGuid():N}",
            ProjectId = project.Id,
            StartedAt = DateTimeOffset.UtcNow,
            Cwd = cwd,
            Distillation = DistillationState.Pending,
        };
        Context.Episodes.Add(episode);
        await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return episode;
    }

    /// <summary>Unique per run: the fixture database is shared by every test in this class.</summary>
    private static string Identity(string name) => $"github.com/test/{name}-{Guid.NewGuid():N}";

    private static string Root(string drive, string name) => $@"{drive}:\git\{name}-{Guid.NewGuid():N}";

    private MimirDbContext Context
    {
        get
        {
            if (fixture.UnavailableReason is { } reason)
            {
                Assert.Skip(TestPostgres.SkipMessage(reason));
            }

            return _context ??= fixture.CreateContext();
        }
    }
}
