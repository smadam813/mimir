using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Storage;

/// <summary>
/// The ambient Candidate Universe as a §3 search mode: the session's Project plus Global,
/// non-Retired, minus the native-content exclusion — restricted inside both legs before the
/// per-leg LIMIT. The eligibility matrix is the pin: one seeding, hand-computed in-set and
/// out-of-set rows, asserted against <em>both</em> methods that reach the universe, so a future
/// fork of the shared clause cannot leave the two disagreeing.
/// </summary>
public sealed class WisdomSearchAmbientTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

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
    public async Task AmbientUniverse_SearchAndList_AgreeOnTheFullEligibilityMatrix()
    {
        await Context.ResetWisdomAsync(Token);
        var (project, foreign) = (await AddProjectAsync(), await AddProjectAsync());

        var projectScoped = await AddWisdomAsync(project.Id, "yak of the session project");
        var global = await AddWisdomAsync(Project.GlobalId, "yak of the global scope");
        await AddWisdomAsync(foreign.Id, "yak of a foreign project");
        await AddWisdomAsync(project.Id, "yak retired long ago", retiredAt: Now);
        var harvestOnly = await AddWisdomAsync(project.Id, "yak harvested natively");
        await AddHarvestProvenanceAsync(harvestOnly.Id, project.Id);
        var foreignHarvest = await AddWisdomAsync(Project.GlobalId, "yak harvested elsewhere");
        await AddHarvestProvenanceAsync(foreignHarvest.Id, foreign.Id);
        var orphaned = await AddWisdomAsync(project.Id, "yak with orphaned provenance");
        await AddThenOrphanEventProvenanceAsync(orphaned.Id, project.Id);
        var mixed = await AddWisdomAsync(project.Id, "yak harvested but also distilled");
        await AddHarvestProvenanceAsync(mixed.Id, project.Id);
        await AddEventProvenanceAsync(mixed.Id, project.Id);

        var hits = await Search().SearchAmbientAsync(
            new Vector(TestVectors.Basis), "yak", project.Id, Token);
        var listed = await Search().ListAmbientAsync(project.Id, Token);

        // The per-leg top-N (50) far exceeds the eight seeded rows, so nothing truncates: each
        // method returns exactly the universe. Equality is the whole matrix in both directions —
        // the three ineligible rows seeded above (foreign scope, Retired, harvest-only) are out
        // by their absence from it, so no separate exclusion assertion could add anything.
        Guid[] eligible = [projectScoped.Id, global.Id, foreignHarvest.Id, orphaned.Id, mixed.Id];
        hits.Select(h => h.WisdomId).ShouldBe(eligible, ignoreOrder: true);
        listed.ShouldBe(eligible, ignoreOrder: true);
    }

    [Fact]
    public async Task AmbientUniverse_RestrictsBeforeThePerLegLimit_NotAfterFusion()
    {
        await Context.ResetWisdomAsync(Token);
        var (project, foreign) = (await AddProjectAsync(), await AddProjectAsync());
        // Three foreign rows outrank the ambient two on both legs: nearer vectors, denser matches.
        foreach (var cosine in (double[])[0.99, 0.97, 0.95])
        {
            await AddWisdomAsync(foreign.Id, "ibex ibex ibex ibex", cosine: cosine);
        }

        var projectScoped = await AddWisdomAsync(project.Id, "ibex sighting", cosine: 0.5);
        var global = await AddWisdomAsync(Project.GlobalId, "ibex report", cosine: 0.4);

        var hits = await Search(perLegTopN: 2).SearchAmbientAsync(
            new Vector(TestVectors.Basis), "ibex", project.Id, Token);

        // Applied after the per-leg LIMIT, the universe would be the filtered residue of an
        // unfiltered top-2 — both legs full of foreign rows, ambient recall empty while eligible
        // matches sit deeper. Applied before it, both ambient rows fill the legs and rank.
        hits.Select(h => h.WisdomId).ShouldBe(
            [projectScoped.Id, global.Id], ignoreOrder: true);
    }

    private WisdomSearch Search(int perLegTopN = 50)
        => new(Context, Options.Create(new SearchOptions { PerLegTopN = perLegTopN }));

    private async Task<Project> AddProjectAsync()
    {
        var project = TestData.NewProject("ambient");
        Context.Projects.Add(project);
        await Context.SaveChangesAsync(Token);
        return project;
    }

    private async Task<Wisdom> AddWisdomAsync(
        Guid scopeProjectId, string text, DateTimeOffset? retiredAt = null, double cosine = 1.0)
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Fact,
            ScopeProjectId = scopeProjectId,
            Text = text,
            Embedding = new Vector(TestVectors.WithCosine(cosine)),
            Reinforcement = 1,
            LastConfirmedAt = Now,
            RetiredAt = retiredAt,
        };
        Context.Wisdom.Add(wisdom);
        await Context.SaveChangesAsync(Token);
        return wisdom;
    }

    private async Task AddHarvestProvenanceAsync(Guid wisdomId, Guid projectId)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var item = new HarvestedItem
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Path = $"ambient-{suffix}/memory/MEMORY.md",
            ContentHash = suffix,
            Content = "harvested content",
            FirstSeen = Now,
            LastChanged = Now,
        };
        Context.HarvestedItems.Add(item);
        Context.Provenance.Add(new Provenance
        {
            Id = Guid.CreateVersion7(),
            WisdomId = wisdomId,
            HarvestedItemId = item.Id,
        });
        await Context.SaveChangesAsync(Token);
    }

    private async Task<Guid> AddEventProvenanceAsync(Guid wisdomId, Guid projectId)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{suffix}",
            ProjectId = projectId,
            StartedAt = Now,
            Cwd = $@"C:\git\ambient-{suffix}",
        };
        var evt = new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episode.Id,
            Seq = 1,
            Type = EventType.UserPromptSubmit,
            At = Now,
            Payload = """{"content":"distilled from a session"}""",
            PayloadFullSize = 40,
            Salient = false,
        };
        Context.AddRange(episode, evt);
        Context.Provenance.Add(new Provenance
        {
            Id = Guid.CreateVersion7(),
            WisdomId = wisdomId,
            EpisodeId = episode.Id,
            EventId = evt.Id,
        });
        await Context.SaveChangesAsync(Token);
        return episode.Id;
    }

    /// <summary>
    /// The §8.2 orphaning path for real: hard-deleting the Episode cascades the Provenance rows
    /// away at the database, leaving the Wisdom provenance-less — which the universe keeps in.
    /// </summary>
    private async Task AddThenOrphanEventProvenanceAsync(Guid wisdomId, Guid projectId)
    {
        var episodeId = await AddEventProvenanceAsync(wisdomId, projectId);
        await Context.Episodes.Where(e => e.Id == episodeId).ExecuteDeleteAsync(Token);
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
