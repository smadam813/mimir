using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Storage;

/// <summary>
/// The ambient Candidate Universe as a §3 search mode: both legs restrict to the session's
/// Project plus Global, non-Retired, minus the native-content exclusion — before the per-leg
/// LIMIT. The parity test pins the hand-written SQL to <see cref="AmbientCandidates"/>, the EF
/// owner of the rule, so the universe can never drift into two rules; the argument checks keep
/// contradictory narrowings out at the seam.
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
    public async Task AmbientUniverse_SqlAndEfExpression_AgreeOnTheFullEligibilityMatrix()
    {
        await Context.ResetWisdomAsync(Token);
        var (project, foreign) = (await AddProjectAsync(), await AddProjectAsync());

        var projectScoped = await AddWisdomAsync(project.Id, "yak of the session project");
        var global = await AddWisdomAsync(Project.GlobalId, "yak of the global scope");
        var foreignScoped = await AddWisdomAsync(foreign.Id, "yak of a foreign project");
        var retired = await AddWisdomAsync(project.Id, "yak retired long ago", retiredAt: Now);
        var harvestOnly = await AddWisdomAsync(project.Id, "yak harvested natively");
        await AddHarvestProvenanceAsync(harvestOnly.Id, project.Id);
        var foreignHarvest = await AddWisdomAsync(Project.GlobalId, "yak harvested elsewhere");
        await AddHarvestProvenanceAsync(foreignHarvest.Id, foreign.Id);
        var orphaned = await AddWisdomAsync(project.Id, "yak with orphaned provenance");
        await AddThenOrphanEventProvenanceAsync(orphaned.Id, project.Id);
        var mixed = await AddWisdomAsync(project.Id, "yak harvested but also distilled");
        await AddHarvestProvenanceAsync(mixed.Id, project.Id);
        await AddEventProvenanceAsync(mixed.Id, project.Id);

        var hits = await Search().SearchAsync(
            new Vector(TestVectors.Basis),
            "yak",
            new WisdomSearchFilter { AmbientProjectId = project.Id },
            Token);

        // The per-leg top-N (50) far exceeds the eight seeded rows, so nothing truncates and the
        // hit set is exactly the SQL universe — comparable row-for-row to the EF expression.
        var expected = await AmbientCandidates.Of(Context, project.Id)
            .Select(w => w.Id)
            .ToListAsync(Token);
        hits.Select(h => h.WisdomId).ShouldBe(expected, ignoreOrder: true);
        expected.ShouldBe(
            [projectScoped.Id, global.Id, foreignHarvest.Id, orphaned.Id, mixed.Id],
            ignoreOrder: true);
        Guid[] excluded = [foreignScoped.Id, retired.Id, harvestOnly.Id];
        hits.ShouldAllBe(h => !excluded.Contains(h.WisdomId));
    }

    [Fact]
    public async Task AmbientUniverse_RejectsIncludeRetired()
    {
        var filter = new WisdomSearchFilter
        {
            AmbientProjectId = Guid.CreateVersion7(),
            IncludeRetired = true,
        };

        await Should.ThrowAsync<ArgumentException>(
            () => Search().SearchAsync(new Vector(TestVectors.Basis), "yak", filter, Token));
    }

    [Fact]
    public async Task AmbientUniverse_RejectsAScopeNarrowing()
    {
        var filter = new WisdomSearchFilter
        {
            AmbientProjectId = Guid.CreateVersion7(),
            ScopeProjectId = Guid.CreateVersion7(),
        };

        await Should.ThrowAsync<ArgumentException>(
            () => Search().SearchAsync(new Vector(TestVectors.Basis), "yak", filter, Token));
    }

    private WisdomSearch Search()
        => new(Context, Options.Create(new SearchOptions { PerLegTopN = 50 }));

    private async Task<Project> AddProjectAsync()
    {
        var project = TestData.NewProject("ambient");
        Context.Projects.Add(project);
        await Context.SaveChangesAsync(Token);
        return project;
    }

    private async Task<Wisdom> AddWisdomAsync(
        Guid scopeProjectId, string text, DateTimeOffset? retiredAt = null)
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Fact,
            ScopeProjectId = scopeProjectId,
            Text = text,
            Embedding = new Vector(TestVectors.Basis),
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
