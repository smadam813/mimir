using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Mimir.Server.Tests.Distillation;
using Pgvector;

namespace Mimir.Server.Tests.Recall;

/// <summary>
/// The Prompt lane (§7) against a real Postgres: the cosine gate over the ambient candidate
/// universe, query-ranked injection within the 1,500-char budget, and the §3 logging rule — every
/// actual injection logs a row with the prompt as <c>query_context</c>, an empty decision leaves
/// no trace.
/// </summary>
public sealed class PromptRecallServiceTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A prompt with no word overlap with any test Wisdom, so only the vector leg ranks.</summary>
    private const string Prompt = "how do I deploy the pipeline?";

    private readonly FakeEmbeddings _embeddings = new();

    private MimirDbContext? _context;

    public ValueTask InitializeAsync()
    {
        _embeddings.Map(Prompt, TestVectors.Basis);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    [Fact]
    public async Task OnTopicPrompt_InjectsLabeledWisdomWithinBudget_AndLogsTheInjection()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var scoped = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.90);
        var global = await AddWisdomAsync(Project.GlobalId, "unrelated filler two", cosine: 0.91);
        var sessionId = NewSessionId();

        var injection = await ComposeAsync(project.Id, sessionId);

        injection.ShouldStartWith("<mimir-memory>");
        injection.Length.ShouldBeLessThanOrEqualTo(1500);
        injection.ShouldContain(scoped.Text);
        injection.ShouldContain(global.Text);
        // Affinity (1.5×) dwarfs the nearer global row's fused-rank edge — project Wisdom leads.
        injection.IndexOf(scoped.Text, StringComparison.Ordinal)
            .ShouldBeLessThan(injection.IndexOf(global.Text, StringComparison.Ordinal));

        var logged = await FromDb(db => db.Injections.SingleAsync(i => i.SessionId == sessionId, Token));
        logged.ProjectId.ShouldBe(project.Id);
        logged.Lane.ShouldBe(InjectionLane.Prompt);
        logged.QueryContext.ShouldBe(Prompt, customMessage: "injection rows carry the prompt (§3)");
        logged.At.ShouldBe(Now);
        logged.Chars.ShouldBe(injection.Length);
        logged.Items.Select(i => i.WisdomId).ShouldBe([scoped.Id, global.Id]);
        logged.Items[0].Score.ShouldBeGreaterThan(logged.Items[1].Score);
    }

    [Fact]
    public async Task OffTopicPrompt_InjectsNothing_AndLogsNothing()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.5);
        var sessionId = NewSessionId();

        var injection = await ComposeAsync(project.Id, sessionId);

        injection.ShouldBeEmpty();
        (await FromDb(db => db.Injections.CountAsync(i => i.SessionId == sessionId, Token)))
            .ShouldBe(0, "empty Prompt-lane decisions are not logged (§7)");
    }

    [Fact]
    public async Task TheGateReadsCosine_ATopFusedRankBelowTheGateStaysShut()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        // Both legs surface this row — the best possible fused rank (≈ 2/61, far above any
        // single-leg fusion) — yet its cosine sits below 0.75, so nothing injects (§3).
        await AddWisdomAsync(project.Id, "deploy the pipeline notes", cosine: 0.6);

        var injection = await ComposeAsync(project.Id);

        injection.ShouldBeEmpty();
    }

    [Fact]
    public async Task ZeroNormEmbeddingsNaNCosine_NeverOpensTheGate()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        // pgvector computes a literal NaN cosine for a zero-magnitude embedding (no zero-norm
        // guard in its distance function). The gate's affirmative >= must hold shut for NaN —
        // a `< threshold` reading would let the degenerate row slip through.
        await AddWisdomAsync(
            project.Id, "unrelated filler one", vector: new float[TestVectors.Dimensions]);
        var sessionId = NewSessionId();

        var injection = await ComposeAsync(project.Id, sessionId);

        injection.ShouldBeEmpty();
        (await FromDb(db => db.Injections.CountAsync(i => i.SessionId == sessionId, Token)))
            .ShouldBe(0);
    }

    [Fact]
    public async Task AnotherProjectsWisdom_NeverOpensTheGate_NorInjects()
    {
        await Context.ResetWisdomAsync(Token);
        var (project, other) = (await AddProjectAsync(), await AddProjectAsync());
        await AddWisdomAsync(other.Id, "unrelated filler one", cosine: 0.95);
        var sessionId = NewSessionId();

        var injection = await ComposeAsync(project.Id, sessionId);

        injection.ShouldBeEmpty();
        (await FromDb(db => db.Injections.CountAsync(i => i.SessionId == sessionId, Token)))
            .ShouldBe(0);
    }

    [Fact]
    public async Task NativeHarvestOnlyWisdom_NeverOpensTheGate_NorInjects()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var native = await AddWisdomAsync(project.Id, "unrelated filler one", cosine: 0.95);
        await AddHarvestProvenanceAsync(native.Id, project.Id);

        var injection = await ComposeAsync(project.Id);

        injection.ShouldBeEmpty(
            "the built-in already loads the current Project's auto-memory natively (§7)");
    }

    [Fact]
    public async Task Injection_FillsToTheBudget_AndLogsOnlyWhatMadeItIn()
    {
        await Context.ResetWisdomAsync(Token);
        var project = await AddProjectAsync();
        var injected = await AddWisdomAsync(project.Id, new string('a', 200), cosine: 0.9);
        await AddWisdomAsync(project.Id, new string('b', 200), cosine: 0.8);
        var sessionId = NewSessionId();

        // A budget with room for the header and one 200-char entry, not two.
        var injection = await ComposeAsync(
            project.Id, sessionId, new RecallOptions { PromptBudgetChars = 450 });

        injection.Length.ShouldBeLessThanOrEqualTo(450);
        var logged = await FromDb(db => db.Injections.SingleAsync(i => i.SessionId == sessionId, Token));
        logged.Items.Select(i => i.WisdomId).ShouldBe([injected.Id]);
    }

    private async Task<string> ComposeAsync(
        Guid projectId, string? sessionId = null, RecallOptions? options = null)
    {
        var recallOptions = Options.Create(options ?? new RecallOptions());
        var clock = new FakeTimeProvider(Now);
        var service = new PromptRecallService(
            Context,
            new QueryRanking(
                Context,
                _embeddings,
                new WisdomSearch(Context, Options.Create(new SearchOptions())),
                recallOptions,
                clock),
            recallOptions,
            clock);
        return await service.ComposeInjectionAsync(
            sessionId ?? NewSessionId(), projectId, Prompt, Token);
    }

    private static string NewSessionId() => $"sess-{Guid.NewGuid():N}";

    private async Task<Project> AddProjectAsync()
    {
        var project = TestData.NewProject("prompt");
        Context.Projects.Add(project);
        await Context.SaveChangesAsync(Token);
        return project;
    }

    private async Task<Wisdom> AddWisdomAsync(
        Guid scopeProjectId, string text, double cosine = 0.0, float[]? vector = null)
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Fact,
            ScopeProjectId = scopeProjectId,
            Text = text,
            Embedding = new Vector(vector ?? TestVectors.WithCosine(cosine)),
            Reinforcement = 1,
            LastConfirmedAt = Now,
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
            Path = $"prompt-{suffix}/memory/MEMORY.md",
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
