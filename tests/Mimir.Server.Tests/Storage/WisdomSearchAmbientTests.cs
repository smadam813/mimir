using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
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
public sealed class WisdomSearchAmbientTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task AmbientUniverse_SearchAndList_AgreeOnTheFullEligibilityMatrix()
    {
        var (project, foreign) = (await AddProjectAsync("ambient"), await AddProjectAsync("ambient"));

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
        var (project, foreign) = (await AddProjectAsync("ambient"), await AddProjectAsync("ambient"));
        // Three foreign rows outrank the ambient two on both legs: nearer vectors, denser matches.
        foreach (var cosine in (double[])[0.99, 0.97, 0.95])
        {
            await AddWisdomAsync(foreign.Id, "ibex ibex ibex ibex", cosine);
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
        => CreateWisdomSearch(new SearchOptions { PerLegTopN = perLegTopN });

    private async Task<Guid> AddEventProvenanceAsync(Guid wisdomId, Guid projectId)
    {
        var episode = await AddEpisodeAsync(projectId);
        var evt = await AddEventAsync(
            episode.Id, seq: 1, payload: """{"content":"distilled from a session"}""");
        await AddProvenanceAsync(wisdomId, episode.Id, evt.Id);
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
}
