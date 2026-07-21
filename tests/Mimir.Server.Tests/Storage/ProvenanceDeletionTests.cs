using Microsoft.EntityFrameworkCore;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Storage;

/// <summary>
/// The §3 deletion contract, declared by the Provenance schema and exercised against a real
/// Postgres: hard-deleting an Event or Episode (§8.2) is the sole operation that removes
/// Provenance rows — and Wisdom whose Provenance empties survives, as the orphaned-provenance
/// case the UI flags. Deleting Wisdom cascades its own version chain and Provenance (§10).
/// </summary>
public sealed class ProvenanceDeletionTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

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
    public async Task HardDeletingAnEvent_RemovesItsProvenanceRows_AndOnlyThose()
    {
        var referenced = await AddReferencedRecordsAsync();
        var wisdom = await AddWisdomAsync();
        await AddProvenanceAsync(wisdom.Id, eventId: referenced.EventId);
        await AddProvenanceAsync(wisdom.Id, episodeId: referenced.EpisodeId);
        await AddProvenanceAsync(wisdom.Id, harvestedItemId: referenced.HarvestedItemId);

        await Context.Events.Where(e => e.Id == referenced.EventId).ExecuteDeleteAsync(Token);

        var remaining = await FromDb(db => db.Provenance
            .Where(p => p.WisdomId == wisdom.Id).ToListAsync(Token));
        remaining.Count.ShouldBe(2);
        remaining.ShouldAllBe(p => p.EventId == null);
        (await FromDb(db => db.Wisdom.CountAsync(w => w.Id == wisdom.Id, Token))).ShouldBe(1);
    }

    [Fact]
    public async Task HardDeletingAnEpisode_TakesItsEventsProvenanceWithIt_TheWisdomSurvives()
    {
        var referenced = await AddReferencedRecordsAsync();
        var wisdom = await AddWisdomAsync();
        await AddProvenanceAsync(wisdom.Id, episodeId: referenced.EpisodeId, eventId: referenced.EventId);
        await AddProvenanceAsync(wisdom.Id, episodeId: referenced.EpisodeId);

        await Context.Episodes.Where(e => e.Id == referenced.EpisodeId).ExecuteDeleteAsync(Token);

        (await FromDb(db => db.Provenance.CountAsync(p => p.WisdomId == wisdom.Id, Token)))
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
        var wisdom = await AddWisdomAsync();
        await AddProvenanceAsync(wisdom.Id, eventId: referenced.EventId);
        await AddProvenanceAsync(wisdom.Id, harvestedItemId: referenced.HarvestedItemId);

        await Context.Wisdom.Where(w => w.Id == wisdom.Id).ExecuteDeleteAsync(Token);

        (await FromDb(db => db.WisdomVersions.CountAsync(v => v.WisdomId == wisdom.Id, Token))).ShouldBe(0);
        (await FromDb(db => db.Provenance.CountAsync(p => p.WisdomId == wisdom.Id, Token))).ShouldBe(0);
        (await FromDb(db => db.Events.CountAsync(e => e.Id == referenced.EventId, Token))).ShouldBe(1);
        (await FromDb(db => db.HarvestedItems.CountAsync(i => i.Id == referenced.HarvestedItemId, Token)))
            .ShouldBe(1);
    }

    private sealed record ReferencedRecords(Guid EpisodeId, Guid EventId, Guid HarvestedItemId);

    /// <summary>A fresh Project with one Episode, one Event on it, and one HarvestedItem.</summary>
    private async Task<ReferencedRecords> AddReferencedRecordsAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Identity = $"github.com/test/provenance-{suffix}",
            DisplayName = $"provenance-{suffix}",
        };
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{suffix}",
            ProjectId = project.Id,
            StartedAt = Now,
            Cwd = $@"C:\git\provenance-{suffix}",
        };
        var evt = new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episode.Id,
            Seq = 1,
            Type = EventType.UserPromptSubmit,
            At = Now,
            Payload = """{"prompt":"remember this"}""",
            PayloadFullSize = 28,
        };
        var item = new HarvestedItem
        {
            Id = Guid.CreateVersion7(),
            ProjectId = project.Id,
            Path = $"prov-{suffix}/memory/MEMORY.md",
            ContentHash = suffix,
            Content = "a memory",
            FirstSeen = Now,
            LastChanged = Now,
        };
        Context.AddRange(project, episode, evt, item);
        await Context.SaveChangesAsync(Token);
        return new ReferencedRecords(episode.Id, evt.Id, item.Id);
    }

    private async Task<Wisdom> AddWisdomAsync()
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Fact,
            ScopeProjectId = Project.GlobalId,
            Text = $"a durable fact {Guid.NewGuid():N}",
            Embedding = new Vector(TestVectors.Basis),
            Reinforcement = 1,
            LastConfirmedAt = Now,
        };
        Context.Wisdom.Add(wisdom);
        Context.WisdomVersions.Add(new WisdomVersion
        {
            WisdomId = wisdom.Id,
            Version = 1,
            Text = wisdom.Text,
            CreatedAt = Now,
            Cause = WisdomVersionCause.Distilled,
        });
        await Context.SaveChangesAsync(Token);
        return wisdom;
    }

    private async Task AddProvenanceAsync(
        Guid wisdomId, Guid? episodeId = null, Guid? eventId = null, Guid? harvestedItemId = null)
    {
        Context.Provenance.Add(new Provenance
        {
            Id = Guid.CreateVersion7(),
            WisdomId = wisdomId,
            EpisodeId = episodeId,
            EventId = eventId,
            HarvestedItemId = harvestedItemId,
        });
        await Context.SaveChangesAsync(Token);
    }

    private async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var context = fixture.CreateContext();
        return await query(context);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

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
