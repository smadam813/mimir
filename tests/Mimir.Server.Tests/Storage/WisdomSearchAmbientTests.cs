using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Storage;

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

        Guid[] eligible = [projectScoped.Id, global.Id, foreignHarvest.Id, orphaned.Id, mixed.Id];
        hits.Select(h => h.WisdomId).ShouldBe(eligible, ignoreOrder: true);
        listed.ShouldBe(eligible, ignoreOrder: true);
    }

    [Fact]
    public async Task AmbientUniverse_RestrictsBeforeThePerLegLimit_NotAfterFusion()
    {
        var (project, foreign) = (await AddProjectAsync("ambient"), await AddProjectAsync("ambient"));
        foreach (var cosine in (double[])[0.99, 0.97, 0.95])
        {
            await AddWisdomAsync(foreign.Id, "ibex ibex ibex ibex", cosine);
        }

        var projectScoped = await AddWisdomAsync(project.Id, "ibex sighting", cosine: 0.5);
        var global = await AddWisdomAsync(Project.GlobalId, "ibex report", cosine: 0.4);

        var hits = await Search(perLegTopN: 2).SearchAmbientAsync(
            new Vector(TestVectors.Basis), "ibex", project.Id, Token);

        hits.Select(h => h.WisdomId).ShouldBe(
            [projectScoped.Id, global.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task TheQuerylessListing_IsUnlimited_HoweverSmallTheSearchLegsCapIs()
    {
        var project = await AddProjectAsync("unbounded");
        var universe = new List<Guid>();
        for (var row = 0; row < 5; row++)
        {
            universe.Add((await AddWisdomAsync(project.Id, $"ibex number {row}")).Id);
        }

        var ids = await Search(perLegTopN: 2).ListAmbientAsync(project.Id, Token);

        ids.ShouldBe(
            universe,
            ignoreOrder: true,
            "the lanes with no query rank the whole universe themselves, so a cap here could only "
            + "truncate arbitrarily — brief_score is not computable in this query (#72)");
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

    private async Task AddThenOrphanEventProvenanceAsync(Guid wisdomId, Guid projectId)
    {
        var episodeId = await AddEventProvenanceAsync(wisdomId, projectId);
        await Context.Episodes.Where(e => e.Id == episodeId).ExecuteDeleteAsync(Token);
    }
}
