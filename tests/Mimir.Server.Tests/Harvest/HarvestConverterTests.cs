using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Harvest;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Harvest;

/// <summary>
/// The §5 handoff against a real Postgres: every HarvestedItem version with a null conversion
/// marker — new or backfilled — flows through the Merge Gate exactly once, and re-harvested
/// equivalent content reinforces the Wisdom its earlier version produced (the #20 demo).
/// </summary>
public sealed class HarvestConverterTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task PendingVersions_FlowThroughTheGateExactlyOnce()
    {
        // Rows born without the marker are exactly what the Backfill left behind before this
        // ticket shipped — the first run must carry them to the gate, the second must not.
        var project = await AddProjectAsync("convert");
        await AddHarvestedItemAsync(project.Id, "a/memory/MEMORY.md", "Fact alpha");
        await AddHarvestedItemAsync(project.Id, "a/memory/beta.md", "Fact beta");

        (await Converter().ConvertPendingAsync(Token)).ShouldBe(2);

        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(2);
        (await FromDb(db => db.HarvestedItems.CountAsync(i => i.ConvertedAt == null, Token))).ShouldBe(0);

        (await Converter().ConvertPendingAsync(Token)).ShouldBe(0);
        // The count first: the return value above counts conversions, not rows, so a second pass
        // that *removed* Wisdom would still report zero — and ShouldAllBe passes over an empty set.
        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(2);
        (await FromDb(db => db.Wisdom.ToListAsync(Token)))
            .ShouldAllBe(w => w.ScopeProjectId == project.Id && w.Reinforcement == 1);
    }

    [Fact]
    public async Task ReharvestedEquivalentContent_BumpsReinforcement()
    {
        var project = await AddProjectAsync("convert");
        const string original = "The build needs Postgres running";
        const string reworded = "Postgres must be up for the build";
        Embeddings.Map(original, TestVectors.Basis);
        Embeddings.Map(reworded, TestVectors.WithCosine(0.9));
        var v1 = await AddHarvestedItemAsync(project.Id, "b/memory/MEMORY.md", original);
        await Converter().ConvertPendingAsync(Token);

        var v2 = await AddHarvestedItemAsync(
            project.Id, "b/memory/MEMORY.md", reworded, Now.AddHours(1));
        await Converter().ConvertPendingAsync(Token);

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.ScopeProjectId.ShouldBe(project.Id);
        wisdom.Reinforcement.ShouldBe(2);
        wisdom.Text.ShouldBe(original);
        var provenance = await FromDb(db => db.Provenance
            .Select(p => p.HarvestedItemId)
            .ToListAsync(Token));
        provenance.ShouldBe([v1.Id, v2.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task Sections_BecomeCandidates_WithTheFrontmatterKindAndTheFilesProjectScope()
    {
        var project = await AddProjectAsync("convert");
        await AddHarvestedItemAsync(project.Id, "c/memory/prefs.md", """
            ---
            metadata:
              type: user
            ---
            ## Editor
            Tabs, not spaces.
            ## Shell
            Prefers PowerShell.
            """);

        await Converter().ConvertPendingAsync(Token);

        var wisdom = await FromDb(db => db.Wisdom.ToListAsync(Token));
        wisdom.Count.ShouldBe(2);
        wisdom.ShouldAllBe(w => w.ScopeProjectId == project.Id && w.Kind == WisdomKind.Preference);
        wisdom.Select(w => w.Text).ShouldBe(
        [
            "## Editor\nTabs, not spaces.",
            "## Shell\nPrefers PowerShell.",
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task OversizedSections_ArriveAtTheGateCapped()
    {
        var project = await AddProjectAsync("convert");
        await AddHarvestedItemAsync(project.Id, "d/memory/MEMORY.md", new string('y', 500));

        await Converter(new HarvestOptions { CandidateCap = 64 }).ConvertPendingAsync(Token);

        (await FromDb(db => db.Wisdom.SingleAsync(Token))).Text.Length.ShouldBe(64);
    }

    [Fact]
    public async Task AFailingItem_DoesNotBlockTheItemsBehindIt()
    {
        var project = await AddProjectAsync("convert");
        const string unembeddable = "unembeddable";
        Embeddings.Poison(unembeddable);
        var poisoned = await AddHarvestedItemAsync(project.Id, "e/memory/poisoned.md", unembeddable);
        var healthy = await AddHarvestedItemAsync(
            project.Id, "e/memory/healthy.md", "Fact gamma", Now.AddMinutes(1));

        // The failure still surfaces — that is what degrades the tile — but only after the
        // items ordered behind the poisoned one got their turn at the gate.
        await Should.ThrowAsync<InvalidOperationException>(() => Converter().ConvertPendingAsync(Token));

        (await FromDb(db => db.HarvestedItems.SingleAsync(i => i.Id == healthy.Id, Token)))
            .ConvertedAt.ShouldNotBeNull();
        (await FromDb(db => db.HarvestedItems.SingleAsync(i => i.Id == poisoned.Id, Token)))
            .ConvertedAt.ShouldBeNull("the still-null marker is what retries it next tick");
        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(1);
    }

    private HarvestConverter Converter(HarvestOptions? options = null)
        => new(
            Context,
            CreateMergeGate(),
            Options.Create(options ?? new HarvestOptions()),
            Clock,
            NullLogger<HarvestConverter>.Instance);
}
