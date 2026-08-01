using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage.Entities;
using Npgsql;

namespace Mimir.Server.Tests.Storage;

/// <summary>
/// What the schema itself enforces, against a real Postgres — the delete behaviours and the one
/// partial unique index that no C# guard restates. Provenance's own deletion contract has its
/// own suite in <see cref="ProvenanceDeletionTests"/>.
/// </summary>
public sealed class SchemaConstraintTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task TheEmbeddingIndex_IsHnsw_NotAMethodNeedingTrainingRows()
    {
        var method = await FromDb(db => db.Database
            .SqlQueryRaw<string>("""
                SELECT am.amname AS "Value"
                FROM pg_class c
                JOIN pg_am am ON am.oid = c.relam
                WHERE c.relname = 'IX_wisdom_embedding'
                """)
            .SingleAsync(Token));

        method.ShouldBe(
            "hnsw",
            "ivfflat needs training rows and returns nothing useful until it has them, so the "
            + "first Wisdom of an install would not be findable");
    }

    [Fact]
    public async Task DeletingASuperseder_LeavesTheRetiredLoserRetired_JustUnlinked()
    {
        var project = await AddProjectAsync("schema");
        var loser = await AddWisdomAsync(project.Id, "the superseded position", retiredAt: Now);
        var winner = await AddWisdomAsync(project.Id, "the position that won");
        await Context.Wisdom.Where(w => w.Id == loser.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.SupersededBy, winner.Id), Token);

        await Context.Wisdom.Where(w => w.Id == winner.Id).ExecuteDeleteAsync(Token);

        var survivor = await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == loser.Id, Token));
        survivor.RetiredAt.ShouldBe(Now, "retirement is the loser's own standing, not the link's");
        survivor.SupersededBy.ShouldBeNull();
    }

    [Fact]
    public async Task AHarvestedItemAProvenanceRowPointsAt_CannotBeHardDeleted()
    {
        var project = await AddProjectAsync("schema");
        var wisdom = await AddWisdomAsync(project.Id, "born of a harvested file");
        var item = await AddHarvestedItemAsync(project.Id);
        await AddProvenanceAsync(wisdom.Id, harvestedItemId: item.Id);

        await Should.ThrowAsync<PostgresException>(
            Context.HarvestedItems.Where(i => i.Id == item.Id).ExecuteDeleteAsync(Token),
            "HarvestedItems are never hard-deleted (§5), so theirs restricts rather than cascades");
    }

    [Fact]
    public async Task DeletingAWisdom_TakesTheGoldenCasesExpectingIt()
    {
        var project = await AddProjectAsync("schema");
        var wisdom = await AddWisdomAsync(project.Id, "the expected answer");
        var other = await AddWisdomAsync(project.Id, "some other answer");
        await AddGoldenCaseAsync(project.Id, wisdom.Id);
        await AddGoldenCaseAsync(project.Id, other.Id);

        await Context.Wisdom.Where(w => w.Id == wisdom.Id).ExecuteDeleteAsync(Token);

        var remaining = await FromDb(db => db.GoldenCases.ToListAsync(Token));
        remaining.ShouldHaveSingleItem().ExpectedWisdomId.ShouldBe(
            other.Id, "a case expecting deleted Wisdom could never pass again, so it goes with it");
    }

    [Fact]
    public async Task DeletingThePromotingInjection_LeavesTheCaseWithNoBreadcrumb()
    {
        var project = await AddProjectAsync("schema");
        var wisdom = await AddWisdomAsync(project.Id, "the expected answer");
        var injection = await AddInjectionAsync(project.Id, items: [(wisdom.Id, 1.0)]);
        var promoted = await AddGoldenCaseAsync(project.Id, wisdom.Id, createdFromInjectionId: injection.Id);

        await Context.Injections.Where(i => i.Id == injection.Id).ExecuteDeleteAsync(Token);

        var survivor = await FromDb(db => db.GoldenCases.SingleAsync(g => g.Id == promoted.Id, Token));
        survivor.CreatedFromInjectionId.ShouldBeNull(
            "the promotion link is a breadcrumb, not the case's substance");
    }

    [Fact]
    public async Task AtMostOneCaseMayBePromotedFromOneInjection_ButHandInsertedCasesAreUnconstrained()
    {
        var project = await AddProjectAsync("schema");
        var wisdom = await AddWisdomAsync(project.Id, "the expected answer");
        var injection = await AddInjectionAsync(project.Id, items: [(wisdom.Id, 1.0)]);
        await AddGoldenCaseAsync(project.Id, wisdom.Id, createdFromInjectionId: injection.Id);

        await Should.ThrowAsync<DbUpdateException>(
            AddGoldenCaseAsync(project.Id, wisdom.Id, createdFromInjectionId: injection.Id),
            "the partial unique index is what makes PromoteAsync's idempotency survive two clicks");

        Context.ChangeTracker.Clear();
        await AddGoldenCaseAsync(project.Id, wisdom.Id);
        await AddGoldenCaseAsync(project.Id, wisdom.Id);
        (await FromDb(db => db.GoldenCases.CountAsync(g => g.CreatedFromInjectionId == null, Token)))
            .ShouldBe(2, "hand-inserted cases carry no breadcrumb and stay unconstrained");
    }

    [Fact]
    public async Task AnInjectionOutlivesAHardDeletedEpisode_BecauseItsSessionIdIsNoForeignKey()
    {
        var project = await AddProjectAsync("schema");
        var episode = await AddEpisodeAsync(project.Id);
        var logged = await AddInjectionAsync(project.Id, sessionId: episode.SessionId);

        await Context.Episodes.Where(e => e.Id == episode.Id).ExecuteDeleteAsync(Token);

        var survivor = await FromDb(db => db.Injections.SingleAsync(i => i.Id == logged.Id, Token));
        survivor.SessionId.ShouldBe(
            episode.SessionId,
            "an Episode hard-delete purges captured content, not the record that an injection happened");
    }
}
