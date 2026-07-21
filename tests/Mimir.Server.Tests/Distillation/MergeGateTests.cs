using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// The mechanical Merge Gate (§6) against a real Postgres: no match inserts new Wisdom at
/// reinforcement 1 / version 1 with Provenance; a cosine at or above 0.80 reinforces the match
/// instead — text kept, Provenance unioned. Thresholds read the vector leg's cosine, never the
/// fused score (§3).
/// </summary>
public sealed class MergeGateTests(CaptureDatabaseFixture fixture)
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
    public async Task NoMatch_InsertsNewWisdom_AtReinforcementOneVersionOne()
    {
        await ResetWisdomAsync();
        var item = await AddHarvestedItemAsync();
        var text = $"Prefers tabs over spaces {Guid.NewGuid():N}";

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Preference, item.ProjectId, text, HarvestedItemId: item.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == item.ProjectId, Token));
        wisdom.Kind.ShouldBe(WisdomKind.Preference);
        wisdom.Text.ShouldBe(text);
        wisdom.Reinforcement.ShouldBe(1);
        wisdom.LastConfirmedAt.ShouldBe(Now);
        wisdom.RetiredAt.ShouldBeNull();

        var version = await FromDb(db => db.WisdomVersions.SingleAsync(v => v.WisdomId == wisdom.Id, Token));
        version.Version.ShouldBe(1);
        version.Text.ShouldBe(text);
        version.Cause.ShouldBe(WisdomVersionCause.Distilled);

        var provenance = await FromDb(db => db.Provenance.SingleAsync(p => p.WisdomId == wisdom.Id, Token));
        provenance.HarvestedItemId.ShouldBe(item.Id);
        provenance.EpisodeId.ShouldBeNull();
        provenance.EventId.ShouldBeNull();
    }

    [Fact]
    public async Task ANearDuplicate_Reinforces_KeepingTheExistingText()
    {
        await ResetWisdomAsync();
        var first = await AddHarvestedItemAsync();
        var second = await AddHarvestedItemAsync(first.ProjectId);
        var originalText = $"Original wording {Guid.NewGuid():N}";
        var nearDuplicate = $"Equivalent wording {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(nearDuplicate, TestVectors.WithCosine(0.85));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, first.ProjectId, originalText, HarvestedItemId: first.Id));
        _clock.Advance(TimeSpan.FromHours(1));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, second.ProjectId, nearDuplicate, HarvestedItemId: second.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == first.ProjectId, Token));
        wisdom.Text.ShouldBe(originalText, "the mechanical gate keeps the existing text (§6)");
        wisdom.Reinforcement.ShouldBe(2);
        wisdom.LastConfirmedAt.ShouldBe(Now.AddHours(1));

        (await FromDb(db => db.WisdomVersions.CountAsync(v => v.WisdomId == wisdom.Id, Token)))
            .ShouldBe(1, "unchanged text means no new version");
        var provenance = await FromDb(db => db.Provenance
            .Where(p => p.WisdomId == wisdom.Id)
            .Select(p => p.HarvestedItemId)
            .ToListAsync(Token));
        provenance.ShouldBe([first.Id, second.Id], ignoreOrder: true);
    }

    [Fact]
    public async Task JustBelowTheThreshold_InsertsASecondWisdom()
    {
        await ResetWisdomAsync();
        var item = await AddHarvestedItemAsync();
        var originalText = $"First fact {Guid.NewGuid():N}";
        var similarText = $"Nearly related fact {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(similarText, TestVectors.WithCosine(0.79));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, originalText, HarvestedItemId: item.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, similarText, HarvestedItemId: item.Id));

        var texts = await FromDb(db => db.Wisdom
            .Where(w => w.ScopeProjectId == item.ProjectId)
            .Select(w => w.Text)
            .ToListAsync(Token));
        texts.ShouldBe([originalText, similarText], ignoreOrder: true);
    }

    [Fact]
    public async Task AWordForWordFtsMatch_WithADistantEmbedding_DoesNotReinforce()
    {
        await ResetWisdomAsync();
        // The §3 score-scale rule at the gate: identical wording makes the FTS leg rank the pair
        // as hard as it can, but the threshold reads cosine — a distant embedding means no match.
        var item = await AddHarvestedItemAsync();
        var suffix = Guid.NewGuid().ToString("N");
        var originalText = $"the deploy pipeline needs manual approval {suffix}";
        var sameWords = $"needs the manual deploy approval pipeline {suffix}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(sameWords, TestVectors.WithCosine(0.0));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, originalText, HarvestedItemId: item.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, sameWords, HarvestedItemId: item.Id));

        (await FromDb(db => db.Wisdom.CountAsync(w => w.ScopeProjectId == item.ProjectId, Token)))
            .ShouldBe(2);
    }

    [Fact]
    public async Task ReinforcingFromTheSameHarvestedItem_DoesNotDuplicateProvenance()
    {
        await ResetWisdomAsync();
        var item = await AddHarvestedItemAsync();
        var originalText = $"One fact {Guid.NewGuid():N}";
        var nearDuplicate = $"Same fact again {Guid.NewGuid():N}";
        _embeddings.Map(originalText, TestVectors.Basis);
        _embeddings.Map(nearDuplicate, TestVectors.WithCosine(0.9));

        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, originalText, HarvestedItemId: item.Id));
        await AdmitAsync(new WisdomCandidate(
            WisdomKind.Fact, item.ProjectId, nearDuplicate, HarvestedItemId: item.Id));

        var wisdom = await FromDb(db => db.Wisdom.SingleAsync(w => w.ScopeProjectId == item.ProjectId, Token));
        wisdom.Reinforcement.ShouldBe(2);
        (await FromDb(db => db.Provenance.CountAsync(p => p.WisdomId == wisdom.Id, Token)))
            .ShouldBe(1, "Provenance is unioned (§6): the same link is recorded once");
    }

    /// <summary>
    /// Tests map embeddings into the same 0/1 plane, so a prior test's Wisdom could out-match a
    /// later test's candidate. Each test starts from clean Wisdom tables instead.
    /// </summary>
    private async Task ResetWisdomAsync()
    {
        await Context.Provenance.ExecuteDeleteAsync(Token);
        await Context.WisdomVersions.ExecuteDeleteAsync(Token);
        await Context.Wisdom.ExecuteDeleteAsync(Token);
    }

    private async Task AdmitAsync(WisdomCandidate candidate)
    {
        var gate = new MergeGate(
            Context,
            _embeddings,
            new WisdomSearch(Context, Options.Create(new SearchOptions())),
            Options.Create(new DistillationOptions()),
            _clock);
        await gate.AdmitAsync(candidate, Token);
        await Context.SaveChangesAsync(Token);
    }

    /// <summary>An item on its own fresh Project, so per-scope assertions see only this test.</summary>
    private async Task<HarvestedItem> AddHarvestedItemAsync(Guid? projectId = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        if (projectId is null)
        {
            var project = new Project
            {
                Id = Guid.CreateVersion7(),
                Identity = $"github.com/test/gate-{suffix}",
                DisplayName = $"gate-{suffix}",
            };
            Context.Projects.Add(project);
            projectId = project.Id;
        }

        var item = new HarvestedItem
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId.Value,
            Path = $"slug-{suffix}/memory/MEMORY.md",
            ContentHash = suffix,
            Content = "unused by the gate",
            FirstSeen = Now,
            LastChanged = Now,
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
