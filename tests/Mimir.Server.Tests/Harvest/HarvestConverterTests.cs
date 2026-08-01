using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Harvest;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Harvest;

public sealed class HarvestConverterTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task PendingVersions_FlowThroughTheGateExactlyOnce()
    {
        var project = await AddProjectAsync("convert");
        await AddHarvestedItemAsync(project.Id, "a/memory/MEMORY.md", "Fact alpha");
        await AddHarvestedItemAsync(project.Id, "a/memory/beta.md", "Fact beta");

        (await Converter().ConvertPendingAsync(Token)).ShouldBe(2);

        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(2);
        (await FromDb(db => db.HarvestedItems.CountAsync(i => i.ConvertedAt == null, Token))).ShouldBe(0);

        (await Converter().ConvertPendingAsync(Token)).ShouldBe(0);
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

        await Should.ThrowAsync<InvalidOperationException>(() => Converter().ConvertPendingAsync(Token));

        (await FromDb(db => db.HarvestedItems.SingleAsync(i => i.Id == healthy.Id, Token)))
            .ConvertedAt.ShouldNotBeNull();
        (await FromDb(db => db.HarvestedItems.SingleAsync(i => i.Id == poisoned.Id, Token)))
            .ConvertedAt.ShouldBeNull("the still-null marker is what retries it next tick");
        (await FromDb(db => db.Wisdom.CountAsync(Token))).ShouldBe(1);
    }

    [Fact]
    public async Task PendingItems_ConvertOldestFirst_SoReharvestedContentMeetsWhatItProduced()
    {
        var project = await AddProjectAsync("convert");
        const string original = "The build needs Postgres running";
        const string reworded = "Postgres must be up for the build";
        Embeddings.Map(original, TestVectors.Basis);
        Embeddings.Map(reworded, TestVectors.WithCosine(0.9));

        // Seeded newest-first on purpose: insertion order would otherwise hand the heap back in
        // the order asserted and a dropped ORDER BY would be invisible.
        await AddHarvestedItemAsync(project.Id, "f/memory/MEMORY.md", reworded, Now.AddHours(1));
        await AddHarvestedItemAsync(project.Id, "f/memory/MEMORY.md", original);

        (await Converter().ConvertPendingAsync(Token)).ShouldBe(2);

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(
            original, "the older version reached the gate first, so the newer merged into it");
        wisdom.Reinforcement.ShouldBe(2);
    }

    [Fact]
    public async Task AnItemWhoseFileIsGone_StillConverts()
    {
        var project = await AddProjectAsync("convert");
        await AddHarvestedItemAsync(
            project.Id, "g/memory/deleted.md", "Fact delta", goneAt: Now.AddMinutes(-5));

        (await Converter().ConvertPendingAsync(Token)).ShouldBe(1);

        (await FromDb(db => db.Wisdom.SingleAsync(Token))).Text.ShouldBe("Fact delta");
    }

    [Fact]
    public async Task AFailedBatch_LeavesNothingStagedOnTheConvertersOwnContext()
    {
        var project = await AddProjectAsync("convert");
        const string unembeddable = "unembeddable";
        Embeddings.Poison(unembeddable);
        await AddHarvestedItemAsync(project.Id, "h/memory/poisoned.md", unembeddable);
        // The seeder tracked what it inserted; anything present afterwards is the converter's.
        Context.ChangeTracker.Clear();

        await Should.ThrowAsync<InvalidOperationException>(() => Converter().ConvertPendingAsync(Token));

        Context.ChangeTracker.Entries<HarvestedItem>().ShouldBeEmpty(
            "the converter reads no-tracking, so a failed batch leaves nothing to clear");
    }

    [Fact]
    public async Task NearIdenticalSectionsOfOneFile_MergeInsteadOfDuplicating()
    {
        var project = await AddProjectAsync("convert");
        const string first = "## One\nThe build needs Postgres running";
        const string second = "## Two\nPostgres must be up for the build";
        Embeddings.Map(first, TestVectors.Basis);
        Embeddings.Map(second, TestVectors.WithCosine(0.9));
        await AddHarvestedItemAsync(project.Id, "i/memory/MEMORY.md", $"{first}\n{second}");

        await Converter().ConvertPendingAsync(Token);

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(Token));
        wisdom.Text.ShouldBe(first);
        wisdom.Reinforcement.ShouldBe(
            2, "the gate saves after each candidate, so the second one can see the first");
    }

    private HarvestConverter Converter(HarvestOptions? options = null)
        => new(
            Context,
            CreateMergeGate(),
            Options.Create(options ?? new HarvestOptions()),
            Clock,
            NullLogger<HarvestConverter>.Instance);
}
