using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Storage;

public sealed class ProvenanceDeletionTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task HardDeletingAnEvent_RemovesItsProvenanceRows_AndOnlyThose()
    {
        var referenced = await AddReferencedRecordsAsync();
        var wisdom = await AddDurableWisdomAsync();
        await AddProvenanceAsync(wisdom.Id, eventId: referenced.EventId);
        await AddProvenanceAsync(wisdom.Id, episodeId: referenced.EpisodeId);
        await AddProvenanceAsync(wisdom.Id, harvestedItemId: referenced.HarvestedItemId);

        await Context.Events.Where(e => e.Id == referenced.EventId).ExecuteDeleteAsync(Token);

        var remaining = await FromDb(db => db.Provenance.ToListAsync(Token));
        remaining.Count.ShouldBe(2);
        remaining.ShouldAllBe(p => p.EventId == null);
        (await FromDb(db => db.Wisdom.CountAsync(w => w.Id == wisdom.Id, Token))).ShouldBe(1);
    }

    [Fact]
    public async Task HardDeletingAnEpisode_TakesItsEventsProvenanceWithIt_TheWisdomSurvives()
    {
        var referenced = await AddReferencedRecordsAsync();
        var wisdom = await AddDurableWisdomAsync();
        await AddProvenanceAsync(wisdom.Id, referenced.EpisodeId, referenced.EventId);
        await AddProvenanceAsync(wisdom.Id, episodeId: referenced.EpisodeId);

        await Context.Episodes.Where(e => e.Id == referenced.EpisodeId).ExecuteDeleteAsync(Token);

        (await FromDb(db => db.Provenance.CountAsync(Token)))
            .ShouldBe(0, "the Episode cascade reaches Provenance directly and through its Events");
        var survivor = await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == wisdom.Id, Token));
        survivor.Text.ShouldNotBeEmpty("Wisdom whose Provenance empties survives, orphaned (§3)");
        (await FromDb(db => db.WisdomVersions.CountAsync(v => v.WisdomId == wisdom.Id, Token)))
            .ShouldBe(1, "the version chain belongs to the Wisdom, not to the records it references");
    }

    [Fact]
    public async Task DeletingAWisdom_CascadesItsVersionChainAndProvenance_ReferencedRecordsUntouched()
    {
        var referenced = await AddReferencedRecordsAsync();
        var wisdom = await AddDurableWisdomAsync();
        await AddProvenanceAsync(wisdom.Id, eventId: referenced.EventId);
        await AddProvenanceAsync(wisdom.Id, harvestedItemId: referenced.HarvestedItemId);

        await Context.Wisdom.Where(w => w.Id == wisdom.Id).ExecuteDeleteAsync(Token);

        (await FromDb(db => db.WisdomVersions.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.Provenance.CountAsync(Token))).ShouldBe(0);
        (await FromDb(db => db.Events.CountAsync(e => e.Id == referenced.EventId, Token))).ShouldBe(1);
        (await FromDb(db => db.HarvestedItems.CountAsync(i => i.Id == referenced.HarvestedItemId, Token)))
            .ShouldBe(1);
    }

    private sealed record ReferencedRecords(Guid EpisodeId, Guid EventId, Guid HarvestedItemId);

    private async Task<ReferencedRecords> AddReferencedRecordsAsync()
    {
        var project = await AddProjectAsync("provenance");
        var episode = await AddEpisodeAsync(project.Id);
        var evt = await AddEventAsync(episode.Id, seq: 1);
        var item = await AddHarvestedItemAsync(project.Id);
        return new ReferencedRecords(episode.Id, evt.Id, item.Id);
    }

    private async Task<Wisdom> AddDurableWisdomAsync()
        => await AddWisdomAsync(Project.GlobalId, "a durable fact");
}
