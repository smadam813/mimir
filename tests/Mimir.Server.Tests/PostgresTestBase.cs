using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Capture;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Health;
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

    /// <summary>
    /// Runs a race whose winner has to be uncommitted for the loser to collide with it: opens a
    /// transaction, lets <paramref name="seedUncommittedWinner"/> write into it, starts
    /// <paramref name="losing"/>, waits for it to block, then commits and answers with what the
    /// loser resolved to. The winner's entities are the caller's locals, so a test asserts the
    /// loser reached them by closure rather than by anything handed back here.
    /// </summary>
    private protected async Task<T> RaceAsync<T>(
        Func<MimirDbContext, Task> seedUncommittedWinner, Func<Task<T>> losing)
    {
        await using var winner = CreateContext();
        await using var transaction = await winner.Database.BeginTransactionAsync(Token);
        await seedUncommittedWinner(winner);

        var racing = Task.Run(losing, Token);
        await WaitForABlockedSessionAsync(Token, racing);
        await transaction.CommitAsync(Token);
        return await racing;
    }

    /// <summary>
    /// Polls until some other session on this database is blocked on one of the two locks a racing
    /// write can collide on. Both are named rather than accepting any <c>Lock</c> wait for two
    /// reasons: an unrelated waiter — autovacuum, a stray backend — would otherwise release the
    /// test early and leave the collision it is named for unexercised, and a mutant that skips a
    /// lock usually still blocks on the unique index behind it, so the check goes red on that
    /// collision instead of timing out here waiting for a lock it never takes.
    /// <c>pg_stat_activity</c>, not <c>pg_locks</c>, because the <c>transactionid</c> lock carries
    /// no database oid to filter this class's throwaway database by — and an unfiltered
    /// <c>pg_locks</c> would see other classes' databases (#70).
    /// </summary>
    /// <param name="racing">
    /// The write expected to block, where the caller holds it. Observed on every poll so a racer
    /// that faults, or that finishes without ever colliding, fails here with its own exception
    /// instead of burning the whole budget and dying as an unexplained timeout. Omitted where the
    /// racer is started after this call and there is no handle to pass.
    /// </param>
    private protected async Task WaitForABlockedSessionAsync(
        CancellationToken cancellationToken, Task? racing = null)
        => await PollUntilAnyAsync(
            """
            SELECT count(*)::int AS "Value"
            FROM pg_stat_activity
            WHERE datname = current_database()
              AND wait_event_type = 'Lock'
              AND wait_event IN ('advisory', 'transactionid')
              AND pid <> pg_backend_pid()
            """,
            "no session ever blocked behind the uncommitted winner",
            cancellationToken,
            racing);

    /// <summary>Runs <paramref name="countingSql"/> every 25 ms until it counts something.</summary>
    private protected async Task PollUntilAnyAsync(
        string countingSql,
        string timeoutMessage,
        CancellationToken cancellationToken,
        Task? racing = null)
    {
        await using var context = CreateContext();
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (racing is { IsCompleted: true })
            {
                await racing;
                throw new InvalidOperationException(
                    $"The racing write finished without ever blocking, so {timeoutMessage} — "
                    + "the collision this test is named for did not happen.");
            }

            var found = await context.Database.SqlQueryRaw<int>(countingSql).SingleAsync(cancellationToken);
            if (found > 0)
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException(timeoutMessage);
    }

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

    /// <summary>
    /// The §4 hook keeper — wanted by its own tests, by both endpoint-method classes, and by the
    /// MCP remember lane that appends through it.
    /// </summary>
    /// <param name="feed">
    /// Only where a test subscribes to what the service announces; the default is a live feed with
    /// no listeners, which is what every other caller wants.
    /// </param>
    private protected CaptureService CreateCaptureService(IEpisodeFeed? feed = null)
        => new(
            Context,
            new ProjectResolver(Context),
            Options.Create(new CaptureOptions()),
            Clock,
            feed ?? new EpisodeFeed());

    /// <summary>
    /// Storage's §7 universe keeper over the fixture's database — the ranking below, the Brief's
    /// own graph, and a test asserting against the ambient universe itself all want the same one.
    /// </summary>
    private protected WisdomSearch CreateWisdomSearch(SearchOptions? search = null)
        => new(Context, Options.Create(search ?? new SearchOptions()));

    private protected QueryRanking CreateQueryRanking(
        SearchOptions? search = null, RecallOptions? recall = null)
        => new(
            Context,
            Embeddings,
            CreateWisdomSearch(search),
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
        context.Services.AddMimirHealth();
        new CaptureModule().AddServices(context.Services, new ConfigurationBuilder().Build());
        context.Services.AddSingleton<TimeProvider>(Clock);
        context.Services.AddSingleton(CreateMergeGate());
        context.Services.AddLogging();
        _renderContexts.Add(context);
        return context;
    }

    /// <summary>
    /// The same renderer, with one of its own services handed back beside it — the circuit-scoped
    /// <c>SurfaceSearch</c> a surface claims through, or the <c>NavigationManager</c> a route-aware
    /// component reads. Resolved from the renderer rather than constructed, because the whole point
    /// of this tier is that the test drives the same instance the component was injected with.
    /// </summary>
    private protected BunitContext CreateRenderContext<TService>(out TService service)
        where TService : notnull
    {
        var context = CreateRenderContext();
        service = context.Services.GetRequiredService<TService>();
        return context;
    }

    /// <summary>
    /// A §3.1 remote identity, unique per call — for the resolver tests, which hand identities and
    /// roots in by hand rather than through <see cref="AddProjectAsync"/> because what they are
    /// pinning is how two of them resolve against each other.
    /// </summary>
    private protected static string Identity(string name) => $"github.com/test/{name}-{Guid.NewGuid():N}";

    /// <inheritdoc cref="Identity"/>
    private protected static string Root(string drive, string name)
        => $@"{drive}:\git\{name}-{Guid.NewGuid():N}";

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

    /// <summary>
    /// A Project at exactly the <paramref name="identity"/> and <paramref name="rootPaths"/> named,
    /// for the resolver cases that turn on two rows sharing one root — which the minting overload
    /// above cannot set up. Seeded on its own context so the row arrives untracked, the way a
    /// rival written by another process does.
    /// </summary>
    /// <param name="displayName">
    /// Only where a test reads it. It deliberately does not re-derive
    /// <c>ProjectResolver.DisplayNameOf</c>: a fixture that hand-copies a production derivation
    /// pins yesterday's version of it the first time it changes.
    /// </param>
    private protected async Task<Project> AddProjectAsync(
        string identity, string[] rootPaths, string displayName = "seeded")
    {
        await using var seeding = CreateContext();
        var project = new Project
        {
            Id = Guid.CreateVersion7(),
            Identity = identity,
            RootPaths = rootPaths,
            DisplayName = displayName,
        };
        seeding.Projects.Add(project);
        await seeding.SaveChangesAsync(Token);
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
        DateTimeOffset? retiredAt = null,
        Guid? id = null)
    {
        var wisdom = new Wisdom
        {
            Id = id ?? Guid.CreateVersion7(),
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
        DateTimeOffset? lastChanged = null,
        DateTimeOffset? goneAt = null)
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
            GoneAt = goneAt,
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
