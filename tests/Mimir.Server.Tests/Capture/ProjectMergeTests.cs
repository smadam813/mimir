using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mimir.Server.Capture;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Capture;

public sealed class ProjectMergeTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task ACollidingUpgrade_MergesTheClones_RePointingEpisodes()
    {
        var remote = Identity("clones");
        var rootA = Root("A", "clone-a");
        var rootB = Root("B", "clone-b");
        var survivor = await Resolve(remote, rootA);
        var loser = await Resolve(rootB, rootB);
        var episode = await AddEpisodeAsync(loser.Id);

        var merged = await Resolve(remote, rootB);

        merged.Id.ShouldBe(survivor.Id);
        merged.Identity.ShouldBe(remote);
        merged.RootPaths.ShouldBe([rootA, rootB]);

        await using var fresh = CreateContext();
        (await fresh.Projects.AnyAsync(p => p.Id == loser.Id, Token))
            .ShouldBeFalse("the loser row is removed by the merge");
        var repointed = await fresh.Episodes.SingleAsync(e => e.Id == episode.Id, Token);
        repointed.ProjectId.ShouldBe(survivor.Id);
    }

    [Fact]
    public async Task TheMerge_RePointsReferencesFromTablesThisCodeHasNeverHeardOf()
    {
        await Context.Database.ExecuteSqlAsync(
            $"""
            CREATE TABLE IF NOT EXISTS test_future_references (
                id uuid PRIMARY KEY,
                project_id uuid NOT NULL REFERENCES projects (id)
            )
            """,
            Token);

        var remote = Identity("future");
        var rootA = Root("A", "future-a");
        var rootB = Root("B", "future-b");
        var survivor = await Resolve(remote, rootA);
        var loser = await Resolve(rootB, rootB);
        var rowId = Guid.CreateVersion7();
        await Context.Database.ExecuteSqlAsync(
            $"INSERT INTO test_future_references (id, project_id) VALUES ({rowId}, {loser.Id})",
            Token);

        await Resolve(remote, rootB);

        var pointedAt = await Context.Database
            .SqlQuery<Guid>($"""
                SELECT project_id AS "Value" FROM test_future_references WHERE id = {rowId}
                """)
            .SingleAsync(Token);
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

    [Fact]
    public async Task AReferenceThatIsNotASingleColumnOnProjectsId_FailsTheMergeLoudly()
    {
        // A foreign key the merger cannot re-point — here one aimed at projects.identity rather
        // than the primary key. Silently stranding its rows would be worse than not merging.
        await using (var schema = CreateContext())
        {
            await schema.Database.ExecuteSqlAsync(
                $"""
                CREATE TABLE IF NOT EXISTS test_unmergeable_references (
                    id uuid PRIMARY KEY,
                    project_identity text NOT NULL REFERENCES projects (identity)
                )
                """,
                Token);
        }

        try
        {
            var remote = Identity("unmergeable");
            var rootA = Root("A", "unmergeable-a");
            var rootB = Root("B", "unmergeable-b");
            var survivor = await Resolve(remote, rootA);
            var loser = await Resolve(rootB, rootB);

            var thrown = await Should.ThrowAsync<InvalidOperationException>(
                async () => await Resolve(remote, rootB));

            thrown.Message.ShouldContain("test_unmergeable_references");
            await using var fresh = CreateContext();
            (await fresh.Projects.CountAsync(p => p.Id == survivor.Id || p.Id == loser.Id, Token))
                .ShouldBe(2, "a merge that cannot complete removes neither row");

            // Not the same claim as AFailedMerge_LeavesNoHalfRePointedRows: the root union that
            // precedes a merge is ProjectResolver's, not MergeAsync's, and it committed on its own
            // before the merge was ever attempted (ProjectResolver.ResolveAsync's array_append runs
            // outside any transaction). So a refused merge is not side-effect-free — both rows
            // claim rootB afterwards, and only the merge's own writes rolled back.
            var kept = await fresh.Projects.SingleAsync(p => p.Id == survivor.Id, Token);
            kept.RootPaths.ShouldContain(rootB, "the pre-merge append is already durable");
            var stillThere = await fresh.Projects.SingleAsync(p => p.Id == loser.Id, Token);
            stillThere.RootPaths.ShouldContain(rootB, "and the loser was never re-pointed off it");
        }
        finally
        {
            // On its own context, for the reason BlockProjectDeletesAsync gives: an FK the merger
            // refuses fails every sibling merge if the undo cannot run.
            await using var schema = CreateContext();
            await schema.Database.ExecuteSqlAsync(
                $"DROP TABLE IF EXISTS test_unmergeable_references", Token);
        }
    }

    [Fact]
    public async Task AFailedMerge_LeavesNoHalfRePointedRows()
    {
        // The merge re-points, unions and deletes in one transaction. Blocking the delete is the
        // only way to reach that last step failing, and what it proves is that the two writes
        // before it went back too.
        var survivor = await AddProjectAsync("atomic-survivor");
        var loser = await AddProjectAsync("atomic-loser");
        var episode = await AddEpisodeAsync(loser.Id);
        var rootsBefore = survivor.RootPaths;
        await BlockProjectDeletesAsync();
        try
        {
            await Should.ThrowAsync<PostgresException>(async () => await ProjectMerger.MergeAsync(
                Context, survivorId: survivor.Id, loserId: loser.Id, Token));

            await using var fresh = CreateContext();
            var stranded = await fresh.Episodes.SingleAsync(e => e.Id == episode.Id, Token);
            stranded.ProjectId.ShouldBe(loser.Id, "the re-point rolled back with the delete");
            var rolledBack = await fresh.Projects.SingleAsync(p => p.Id == survivor.Id, Token);
            rolledBack.RootPaths.ShouldBe(rootsBefore, "the root union rolled back too");
            (await fresh.Projects.AnyAsync(p => p.Id == loser.Id, Token)).ShouldBeTrue();
        }
        finally
        {
            await AllowProjectDeletesAsync();
        }
    }

    /// <summary>
    /// Makes any delete of a Project fail — the stand-in for the crash the merge's transaction
    /// exists for. On its own context, never the shared <c>Context</c> the merge under test just
    /// failed a transaction on: the undo has to run whatever state that one is left in, and a
    /// trigger that survives blocks every sibling test's merge — the per-test truncation fires no
    /// row triggers, so nothing else would take it back out.
    /// </summary>
    private async Task BlockProjectDeletesAsync()
    {
        await using var schema = CreateContext();
        await schema.Database.ExecuteSqlAsync(
            $"""
            CREATE OR REPLACE FUNCTION test_block_project_delete() RETURNS trigger
            LANGUAGE plpgsql AS $fn$
            BEGIN RAISE EXCEPTION 'project deletes are blocked by this test'; END
            $fn$
            """,
            Token);
        await schema.Database.ExecuteSqlAsync(
            $"""
            CREATE TRIGGER test_block_project_delete BEFORE DELETE ON projects
            FOR EACH ROW EXECUTE FUNCTION test_block_project_delete()
            """,
            Token);
    }

    /// <inheritdoc cref="BlockProjectDeletesAsync"/>
    private async Task AllowProjectDeletesAsync()
    {
        await using var schema = CreateContext();
        await schema.Database.ExecuteSqlAsync(
            $"DROP TRIGGER IF EXISTS test_block_project_delete ON projects", Token);
        await schema.Database.ExecuteSqlAsync(
            $"DROP FUNCTION IF EXISTS test_block_project_delete()", Token);
    }

    private async Task<Project> Resolve(string identity, string root)
        => await new ProjectResolver(Context).ResolveAsync(identity, root, Token);
}
