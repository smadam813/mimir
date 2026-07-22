using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Harvest;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;

namespace Mimir.Server.Tests.Harvest;

/// <summary>
/// The §5 handoff against a real Postgres: every HarvestedItem version with a null conversion
/// marker — new or backfilled — flows through the Merge Gate exactly once, and re-harvested
/// equivalent content reinforces the Wisdom its earlier version produced (the #20 demo).
/// </summary>
public sealed class HarvestConverterTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeEmbeddings _embeddings = new();

    private readonly FakeTimeProvider _clock = new(Now);

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
    public async Task PendingVersions_FlowThroughTheGateExactlyOnce()
    {
        await Context.ResetWisdomAsync(Token);
        // Rows born without the marker are exactly what the Backfill left behind before this
        // ticket shipped — the first run must carry them to the gate, the second must not.
        var project = await AddProjectAsync();
        await AddItemAsync(project, "a/memory/MEMORY.md", $"Fact alpha {Guid.NewGuid():N}");
        await AddItemAsync(project, "a/memory/beta.md", $"Fact beta {Guid.NewGuid():N}");

        (await Converter().ConvertPendingAsync(Token)).ShouldBe(2);

        var wisdom = await FromDb(db => db.Wisdom.Where(w => w.ScopeProjectId == project).ToListAsync(Token));
        wisdom.Count.ShouldBe(2);
        (await FromDb(db => db.HarvestedItems.CountAsync(
            i => i.ProjectId == project && i.ConvertedAt == null, Token))).ShouldBe(0);

        (await Converter().ConvertPendingAsync(Token)).ShouldBe(0);
        (await FromDb(db => db.Wisdom.CountAsync(w => w.ScopeProjectId == project, Token))).ShouldBe(2);
        (await FromDb(db => db.Wisdom.Where(w => w.ScopeProjectId == project).ToListAsync(Token)))
            .ShouldAllBe(w => w.Reinforcement == 1);
    }

    [Fact]
    public async Task ReharvestedEquivalentContent_BumpsReinforcement()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var original = $"The build needs Postgres running {Guid.NewGuid():N}";
        var reworded = $"Postgres must be up for the build {Guid.NewGuid():N}";
        _embeddings.Map(original, TestVectors.Basis);
        _embeddings.Map(reworded, TestVectors.WithCosine(0.9));
        var v1 = await AddItemAsync(project, "b/memory/MEMORY.md", original);
        await Converter().ConvertPendingAsync(Token);

        var v2 = await AddItemAsync(project, "b/memory/MEMORY.md", reworded, Now.AddHours(1));
        await Converter().ConvertPendingAsync(Token);

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == project, Token));
        wisdom.Reinforcement.ShouldBe(2);
        wisdom.Text.ShouldBe(original);
        var provenance = await FromDb(db => db.Provenance
            .Where(p => p.WisdomId == wisdom.Id)
            .Select(p => p.HarvestedItemId)
            .ToListAsync(Token));
        provenance.ShouldBe([v1.Id, v2.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task Sections_BecomeCandidates_WithTheFrontmatterKindAndTheFilesProjectScope()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var suffix = Guid.NewGuid().ToString("N");
        await AddItemAsync(project, "c/memory/prefs.md", $"""
            ---
            metadata:
              type: user
            ---
            ## Editor {suffix}
            Tabs, not spaces.
            ## Shell {suffix}
            Prefers PowerShell.
            """);

        await Converter().ConvertPendingAsync(Token);

        var wisdom = await FromDb(db => db.Wisdom.Where(w => w.ScopeProjectId == project).ToListAsync(Token));
        wisdom.Count.ShouldBe(2);
        wisdom.ShouldAllBe(w => w.Kind == WisdomKind.Preference);
        wisdom.Select(w => w.Text).ShouldBe(
        [
            $"## Editor {suffix}\nTabs, not spaces.",
            $"## Shell {suffix}\nPrefers PowerShell.",
        ], ignoreOrder: true);
    }

    [Fact]
    public async Task OversizedSections_ArriveAtTheGateCapped()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        await AddItemAsync(project, "d/memory/MEMORY.md", new string('y', 500));

        await Converter(new HarvestOptions { CandidateCap = 64 }).ConvertPendingAsync(Token);

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == project, Token));
        wisdom.Text.Length.ShouldBe(64);
    }

    [Fact]
    public async Task AFailingItem_DoesNotBlockTheItemsBehindIt()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var unembeddable = $"unembeddable {Guid.NewGuid():N}";
        _embeddings.Poison(unembeddable);
        var poisoned = await AddItemAsync(project, "e/memory/poisoned.md", unembeddable);
        var healthy = await AddItemAsync(
            project, "e/memory/healthy.md", $"Fact gamma {Guid.NewGuid():N}", Now.AddMinutes(1));

        // The failure still surfaces — that is what degrades the tile — but only after the
        // items ordered behind the poisoned one got their turn at the gate.
        await Should.ThrowAsync<InvalidOperationException>(() => Converter().ConvertPendingAsync(Token));

        (await FromDb(db => db.HarvestedItems.SingleAsync(i => i.Id == healthy.Id, Token)))
            .ConvertedAt.ShouldNotBeNull();
        (await FromDb(db => db.HarvestedItems.SingleAsync(i => i.Id == poisoned.Id, Token)))
            .ConvertedAt.ShouldBeNull("the still-null marker is what retries it next tick");
        (await FromDb(db => db.Wisdom.CountAsync(w => w.ScopeProjectId == project, Token))).ShouldBe(1);

        // Leave nothing pending behind: the class shares one database, and the exactly-once
        // test counts every pending item — a leftover here would inflate it, order permitting.
        await Context.HarvestedItems.Where(i => i.Id == poisoned.Id).ExecuteDeleteAsync(Token);
    }

    private HarvestConverter Converter(HarvestOptions? options = null)
    {
        var gate = new MergeGate(
            Context,
            _embeddings,
            new WisdomSearch(Context, Options.Create(new SearchOptions())),
            Options.Create(new DistillationOptions()),
            _clock);
        return new HarvestConverter(
            Context,
            gate,
            _embeddings,
            Options.Create(options ?? new HarvestOptions()),
            _clock,
            NullLogger<HarvestConverter>.Instance);
    }

    private async Task<Guid> AddProjectAsync()
    {
        var project = TestData.NewProject("convert");
        Context.Projects.Add(project);
        await Context.SaveChangesAsync(Token);
        return project.Id;
    }

    private async Task<HarvestedItem> AddItemAsync(
        Guid projectId, string path, string content, DateTimeOffset? lastChanged = null)
    {
        var item = new HarvestedItem
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Path = path,
            ContentHash = Guid.NewGuid().ToString("N"),
            Content = content,
            FirstSeen = Now,
            LastChanged = lastChanged ?? Now,
        };
        Context.HarvestedItems.Add(item);
        await Context.SaveChangesAsync(Token);
        return item;
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
