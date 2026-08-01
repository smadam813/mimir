using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Modules;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;
using Mimir.Server.Ui;
using Pgvector;

namespace Mimir.Server.Tests;

/// <summary>
/// The one Postgres harness. Members are <c>private protected</c> throughout: several of the types
/// handed out here (<see cref="MergeGate"/> and the fakes standing in for its collaborators) are
/// internal to their module, and the whole suite is one assembly, so that is exactly the reach
/// they need.
/// </summary>
public abstract class PostgresTestBase(ThrowawayDatabaseFixture fixture)
    : IClassFixture<ThrowawayDatabaseFixture>, IAsyncLifetime
{
    private protected static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private MimirDbContext? _context;

    private readonly List<BunitContext> _renderContexts = [];

    private protected FakeEmbeddings Embeddings { get; } = new();

    private protected FakeArbiter Arbiter { get; } = new();

    private protected FakeChatClient Chat { get; } = new();

    private protected FakeTimeProvider Clock { get; } = new(Now);

    private protected static CancellationToken Token => TestContext.Current.CancellationToken;

    private protected MimirDbContext Context
    {
        get
        {
            SkipIfUnavailable();
            return _context ??= fixture.CreateContext();
        }
    }

    private protected IDbContextFactory<MimirDbContext> Contexts { get; } = new FixtureContextFactory(fixture);

    private protected string ConnectionString
    {
        get
        {
            SkipIfUnavailable();
            return fixture.ConnectionString;
        }
    }

    private protected async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var context = Contexts.CreateDbContext();
        return await query(context);
    }

    private protected MimirDbContext CreateContext() => Contexts.CreateDbContext();

    private protected async Task<Episode> EpisodeAsync(Guid id)
        => await FromDb(db => db.Episodes.SingleAsync(e => e.Id == id, Token));

    private protected MergeGate CreateMergeGate(DistillationOptions? distillation = null)
        => new(
            Contexts,
            Embeddings,
            Options.Create(new SearchOptions()),
            Arbiter,
            Options.Create(distillation ?? new DistillationOptions()),
            Clock);

    private protected QueryRanking CreateQueryRanking(
        SearchOptions? search = null, RecallOptions? recall = null)
        => new(
            Context,
            Embeddings,
            new WisdomSearch(Context, Options.Create(search ?? new SearchOptions())),
            Options.Create(recall ?? new RecallOptions()),
            Clock);

    private protected void AddThrowawayStorage(IServiceCollection services)
    {
        // Read here, on the test's thread: inside the options callback the no-Postgres skip would
        // be an unobserved exception on the service's, and the test would hang rather than skip.
        var connectionString = ConnectionString;
        void Configure(DbContextOptionsBuilder options) =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector());
        services.AddDbContextFactory<MimirDbContext>(Configure);
        services.AddDbContext<MimirDbContext>(Configure, optionsLifetime: ServiceLifetime.Singleton);
    }

    private protected BunitContext CreateRenderContext()
    {
        // Before the context exists: this reads ConnectionString, and skipping out of a
        // half-constructed renderer would leave it unregistered for disposal.
        SkipIfUnavailable();

        var context = new BunitContext();
        AddThrowawayStorage(context.Services);
        context.Services.AddMimirUi();
        new CaptureModule().AddServices(context.Services, new ConfigurationBuilder().Build());
        context.Services.AddSingleton<TimeProvider>(Clock);
        context.Services.AddSingleton(CreateMergeGate());
        context.Services.AddLogging();
        _renderContexts.Add(context);
        return context;
    }

    private protected async Task<Project> AddProjectAsync(string name = "project")
    {
        var suffix = Guid.NewGuid().ToString("N");
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Identity = $"github.com/test/{name}-{suffix}",
            RootPaths = [$@"C:\git\{name}-{suffix}"],
            DisplayName = name,
        };
        Context.Projects.Add(project);
        await Context.SaveChangesAsync(Token);
        return project;
    }

    private protected async Task<Episode> AddEpisodeAsync(
        Guid projectId,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? sealedAt = null,
        string? sealReason = null,
        DistillationState distillation = DistillationState.Pending,
        DateTimeOffset? distillationStartedAt = null)
    {
        var episode = new Episode
        {
            Id = Guid.CreateVersion7(),
            SessionId = $"sess-{Guid.NewGuid():N}",
            ProjectId = projectId,
            StartedAt = startedAt ?? (sealedAt ?? Now).AddHours(-1),
            SealedAt = sealedAt,
            SealReason = sealedAt is null ? null : sealReason ?? "clear",
            Cwd = @"C:\git\mimir-tests",
            Distillation = distillation,
            DistillationStartedAt = distillationStartedAt,
        };
        Context.Episodes.Add(episode);
        await Context.SaveChangesAsync(Token);
        return episode;
    }

    private protected async Task<Event> AddEventAsync(
        Guid episodeId,
        int seq,
        EventType type = EventType.UserPromptSubmit,
        DateTimeOffset? at = null,
        string? payload = null,
        bool salient = false)
    {
        var body = payload ?? """{"prompt":"remember this"}""";
        var evt = new Event
        {
            Id = Guid.CreateVersion7(),
            EpisodeId = episodeId,
            Seq = seq,
            Type = type,
            At = at ?? Now,
            Payload = body,
            PayloadFullSize = body.Length,
            // Taken from the caller, never derived from the type: deriving it would restate
            // CaptureService's salience rule in shared test infrastructure.
            Salient = salient,
        };
        Context.Events.Add(evt);
        await Context.SaveChangesAsync(Token);
        return evt;
    }

    private protected async Task<Wisdom> AddWisdomAsync(
        Guid scopeProjectId,
        string text,
        double cosine = 1.0,
        float[]? embedding = null,
        WisdomKind kind = WisdomKind.Fact,
        int reinforcement = 1,
        DateTimeOffset? lastConfirmedAt = null,
        DateTimeOffset? contestedAt = null,
        DateTimeOffset? retiredAt = null)
    {
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = kind,
            ScopeProjectId = scopeProjectId,
            Text = text,
            Embedding = new Vector(embedding ?? TestVectors.WithCosine(cosine)),
            Reinforcement = reinforcement,
            LastConfirmedAt = lastConfirmedAt ?? Now,
            ContestedAt = contestedAt,
            RetiredAt = retiredAt,
        };
        Context.Wisdom.Add(wisdom);
        Context.WisdomVersions.Add(new WisdomVersion
        {
            WisdomId = wisdom.Id,
            Version = 1,
            Text = wisdom.Text,
            CreatedAt = wisdom.LastConfirmedAt,
            Cause = WisdomVersionCause.Distilled,
        });
        await Context.SaveChangesAsync(Token);
        return wisdom;
    }

    private protected async Task<HarvestedItem> AddHarvestedItemAsync(
        Guid projectId,
        string? path = null,
        string content = "a memory",
        DateTimeOffset? lastChanged = null)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var item = new HarvestedItem
        {
            Id = Guid.CreateVersion7(),
            ProjectId = projectId,
            Path = path ?? $"C--git-{suffix}/memory/MEMORY.md",
            ContentHash = suffix,
            Content = content,
            FirstSeen = Now,
            LastChanged = lastChanged ?? Now,
        };
        Context.HarvestedItems.Add(item);
        await Context.SaveChangesAsync(Token);
        return item;
    }

    private protected async Task<Provenance> AddProvenanceAsync(
        Guid wisdomId, Guid? episodeId = null, Guid? eventId = null, Guid? harvestedItemId = null)
    {
        var provenance = new Provenance
        {
            Id = Guid.CreateVersion7(),
            WisdomId = wisdomId,
            EpisodeId = episodeId,
            EventId = eventId,
            HarvestedItemId = harvestedItemId,
        };
        Context.Provenance.Add(provenance);
        await Context.SaveChangesAsync(Token);
        return provenance;
    }

    private protected async Task<Provenance> AddHarvestProvenanceAsync(Guid wisdomId, Guid projectId)
    {
        var item = await AddHarvestedItemAsync(projectId, content: "harvested content");
        return await AddProvenanceAsync(wisdomId, harvestedItemId: item.Id);
    }

    private protected async Task<Injection> AddInjectionAsync(
        Guid projectId,
        string sessionId = "sess-injection",
        InjectionLane lane = InjectionLane.Prompt,
        string? queryContext = "a prompt",
        DateTimeOffset? at = null,
        int chars = 240,
        IReadOnlyList<(Guid WisdomId, double Score)>? items = null,
        InjectionVerdict? verdict = null)
    {
        var injection = new Injection
        {
            Id = Guid.CreateVersion7(),
            SessionId = sessionId,
            ProjectId = projectId,
            At = at ?? Now,
            Lane = lane,
            QueryContext = queryContext,
            Chars = chars,
            Items = [.. (items ?? []).Select(
                i => new InjectionItem { WisdomId = i.WisdomId, Score = i.Score })],
            Verdict = verdict,
            VerdictAt = verdict is null ? null : at ?? Now,
        };
        Context.Injections.Add(injection);
        await Context.SaveChangesAsync(Token);
        return injection;
    }

    private protected async Task<GoldenCase> AddGoldenCaseAsync(
        Guid projectId,
        Guid expectedWisdomId,
        string queryContext = "a prompt",
        Guid? createdFromInjectionId = null,
        string note = "test case")
    {
        var goldenCase = new GoldenCase
        {
            Id = Guid.CreateVersion7(),
            QueryContext = queryContext,
            ProjectId = projectId,
            ExpectedWisdomId = expectedWisdomId,
            CreatedFromInjectionId = createdFromInjectionId,
            Note = note,
        };
        Context.GoldenCases.Add(goldenCase);
        await Context.SaveChangesAsync(Token);
        return goldenCase;
    }

    public virtual async ValueTask InitializeAsync()
    {
        SkipIfUnavailable();
        await using var context = fixture.CreateContext();
        await ResetAsync(context, fixture.GlobalSeed);
    }

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var render in _renderContexts)
        {
            // Each inside its own try: a renderer still tearing down under an abandoned lifecycle
            // query must not take the remaining renderers, or the context below, with it.
            try
            {
                await render.DisposeAsync();
            }
            catch (Exception ex)
            {
                TestContext.Current.SendDiagnosticMessage($"Renderer teardown failed: {ex}");
            }
        }

        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    private static async Task ResetAsync(MimirDbContext context, Project? globalSeed)
    {
        var tables = context.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(table => $"\"{table}\"");
        // Raw, not interpolated: every name comes from the EF model and TRUNCATE takes no parameters.
        var truncate = $"TRUNCATE TABLE {string.Join(", ", tables)} CASCADE";
        await context.Database.ExecuteSqlRawAsync(truncate, Token);

        if (globalSeed is not null)
        {
            context.Projects.Add(new Project
            {
                Id = globalSeed.Id,
                Identity = globalSeed.Identity,
                RootPaths = [.. globalSeed.RootPaths],
                DisplayName = globalSeed.DisplayName,
            });
            await context.SaveChangesAsync(Token);
        }
    }

    private void SkipIfUnavailable()
    {
        if (fixture.UnavailableReason is { } reason)
        {
            Assert.Skip(TestPostgres.SkipMessage(reason));
        }
    }
}
