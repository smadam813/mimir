using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Storage;

/// <summary>
/// The signal the optimistic capture path retries on, read off failures Postgres actually raised
/// rather than hand-built exceptions — the point is which SQLSTATE arrives wrapped in what.
/// </summary>
public sealed class DbRacesTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task ALostUniqueKeyRace_IsRetryable()
    {
        var project = await AddProjectAsync("races");
        var episode = await AddEpisodeAsync(project.Id);
        await AddEventAsync(episode.Id, seq: 1);
        Context.ChangeTracker.Clear();

        var collision = await Should.ThrowAsync<DbUpdateException>(AddEventAsync(episode.Id, seq: 1));

        collision.IsUniqueViolation().ShouldBeTrue();
        collision.IsForeignKeyViolation().ShouldBeFalse();
    }

    [Fact]
    public async Task AForeignKeyViolation_IsNotRetryableAsALostSlot()
    {
        var project = await AddProjectAsync("races");
        var wisdom = await AddWisdomAsync(project.Id, "some wisdom");
        Context.ChangeTracker.Clear();

        var violation = await Should.ThrowAsync<DbUpdateException>(
            AddProvenanceAsync(wisdom.Id, episodeId: Guid.CreateVersion7()));

        violation.IsUniqueViolation().ShouldBeFalse(
            "only a unique-key collision means someone else won the same slot; anything else must surface");
        violation.IsForeignKeyViolation().ShouldBeTrue();
    }

    [Fact]
    public async Task OnRawSql_TheSameSignalsArriveUnwrapped()
    {
        var project = await AddProjectAsync("races");
        var duplicate = await Should.ThrowAsync<Npgsql.PostgresException>(
            Context.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO projects (id, identity, root_paths, display_name)
                 VALUES ({Guid.CreateVersion7()}, {project.Identity}, ARRAY[]::text[], 'clone')
                 """,
                Token));

        duplicate.IsUniqueViolation().ShouldBeTrue();
        duplicate.IsForeignKeyViolation().ShouldBeFalse();
    }
}
