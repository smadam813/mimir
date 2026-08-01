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
/// The one Postgres harness: every Postgres-backed test class inherits it and writes no plumbing.
/// Before each test the whole database is emptied, so a test's assertions see its own rows and
/// nothing else — the clean slate is a property of the harness, not a convention each class has to
/// remember (the #20/#22 ordering failures). The class fixture still owns a throwaway database per
/// class; xUnit builds the class once per test and runs a class's tests serially, so the reset
/// races nothing.
///
/// Members are <c>private protected</c> throughout: several of the types handed out here
/// (<see cref="MergeGate"/> and the fakes standing in for its collaborators) are internal to their
/// module, and the whole suite is one assembly, so that is exactly the reach they need.
/// </summary>
public abstract class PostgresTestBase(ThrowawayDatabaseFixture fixture)
    : IClassFixture<ThrowawayDatabaseFixture>, IAsyncLifetime
{
    /// <summary>
    /// The suite's fixed clock reading. One value across every class: assertions that render a
    /// timestamp (<c>"sealed 2026-07-22 10:00Z"</c>) are then reading the same "now" everywhere.
    /// </summary>
    private protected static readonly DateTimeOffset Now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

    private MimirDbContext? _context;

    /// <summary>Every renderer <see cref="CreateRenderContext"/> handed out, torn down with the class.</summary>
    private readonly List<BunitContext> _renderContexts = [];

    /// <summary>The deterministic stand-in for qwen3-embedding; see <see cref="TestVectors"/>.</summary>
    private protected FakeEmbeddings Embeddings { get; } = new();

    /// <summary>The scripted merge arbiter: Agreement-on-the-existing-text unless a test says otherwise.</summary>
    private protected FakeArbiter Arbiter { get; } = new();

    /// <summary>The scripted chat model the distiller and the real arbiter talk to.</summary>
    private protected FakeChatClient Chat { get; } = new();

    /// <summary>The clock every SUT built here reads, started at <see cref="Now"/>.</summary>
    private protected FakeTimeProvider Clock { get; } = new(Now);

    /// <summary>The ambient test cancellation token.</summary>
    private protected static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The test's own long-lived context on the throwaway database — the one a SUT taking a scoped
    /// <see cref="MimirDbContext"/> is built over. Skips the test when no Postgres is reachable.
    /// </summary>
    private protected MimirDbContext Context
    {
        get
        {
            SkipIfUnavailable();
            return _context ??= fixture.CreateContext();
        }
    }

    /// <summary>The factory a SUT that opens its own contexts (the gate, the Ui browsers) takes.</summary>
    private protected IDbContextFactory<MimirDbContext> Contexts { get; } = new FixtureContextFactory(fixture);

    /// <summary>The throwaway database's connection string, for tests that build their own DI graph.</summary>
    private protected string ConnectionString
    {
        get
        {
            SkipIfUnavailable();
            return fixture.ConnectionString;
        }
    }

    /// <summary>
    /// Reads back through a separate context, so assertions see what Postgres persisted rather
    /// than the entities a service still tracks.
    /// </summary>
    private protected async Task<T> FromDb<T>(Func<MimirDbContext, Task<T>> query)
    {
        await using var context = Contexts.CreateDbContext();
        return await query(context);
    }

    /// <summary>A fresh context on the throwaway database; the caller disposes it.</summary>
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

    /// <summary>
    /// One seeded Episode read back through a separate context — the assertion counterpart to
    /// <see cref="AddEpisodeAsync"/>, and what every class asserting on Episode state was writing
    /// for itself.
    /// </summary>
    private protected async Task<Episode> EpisodeAsync(Guid id)
        => await FromDb(db => db.Episodes.SingleAsync(e => e.Id == id, Token));

    /// <summary>
    /// The real Merge Gate over the fixture's database and this class's fakes. Every harness-backed
    /// caller composes it here, so its six-dependency graph is a one-file edit; the one gate built
    /// by hand is <c>MergeGateGuardTests</c>, which needs a factory that never connects.
    /// </summary>
    private protected MergeGate CreateMergeGate(DistillationOptions? distillation = null)
        => new(
            Contexts,
            Embeddings,
            Options.Create(new SearchOptions()),
            Arbiter,
            Options.Create(distillation ?? new DistillationOptions()),
            Clock);

    /// <summary>
    /// Storage's §7 universe keeper over the fixture's database — the ranking below, the Brief's
    /// own graph, and a test asserting against the ambient universe itself all want the same one.
    /// </summary>
    private protected WisdomSearch CreateWisdomSearch(SearchOptions? search = null)
        => new(Context, Options.Create(search ?? new SearchOptions()));

    /// <summary>
    /// The §7 query ranking over the fixture's database, the fake embedder and the base clock —
    /// the four consumers that replay a query through it all want the same graph.
    /// </summary>
    private protected QueryRanking CreateQueryRanking(
        SearchOptions? search = null, RecallOptions? recall = null)
        => new(
            Context,
            Embeddings,
            CreateWisdomSearch(search),
            // Takes its own RecallOptions rather than always the defaults: the ranking reads the
            // scoring knobs (AffinityBoost, SalienceBoost, RecencyHalfLifeDays), so a caller that
            // overrides one for the service under test has to be able to hand the same instance
            // here — otherwise the test pins a value the ranked rows were never scored with.
            Options.Create(recall ?? new RecallOptions()),
            Clock);

    /// <summary>
    /// Registers the throwaway database the way <c>AddMimirStorage</c> does — both the factory and
    /// the scoped context, with Singleton options (#23) — for tests that boot a hosted service over
    /// their own DI graph. The connection string is read here, on the test's thread: inside the
    /// options callback it would be read on the service's thread, where the no-Postgres skip is an
    /// unobserved exception and the test sits out its patience instead of skipping.
    /// </summary>
    private protected void AddThrowawayStorage(IServiceCollection services)
    {
        var connectionString = ConnectionString;
        void Configure(DbContextOptionsBuilder options) =>
            options.UseNpgsql(connectionString, npgsql => npgsql.UseVector());
        services.AddDbContextFactory<MimirDbContext>(Configure);
        services.AddDbContext<MimirDbContext>(Configure, optionsLifetime: ServiceLifetime.Singleton);
    }

    /// <summary>
    /// A bUnit renderer over this test's throwaway database — the Postgres render tier (#130). A
    /// §8 surface injects the <c>Ui/</c> browsers, so pinning what it renders means seeding rows,
    /// and this is where the two halves meet: the seeders and the per-test truncation are the
    /// class's own, and <see cref="AddThrowawayStorage"/> registers storage the way
    /// <c>AddMimirStorage</c> does, so the surface resolves what it resolves in production.
    /// Registered on top of that is what a §8 surface actually takes, and every registration comes
    /// from the app's own composition rather than a copy of it: <c>AddMimirUi</c> for the four
    /// browsers and the header's per-circuit <c>SurfaceSearch</c>, <c>AddMimirHealth</c> for the
    /// snapshot the header's pill and pull chip read, and <c>CaptureModule</c> for the Episode
    /// feed. The module is constructed and asked, not restated — its <c>AddServices</c>
    /// ignores the configuration it takes and its two other registrations are inert here, which is
    /// a small price for a line that cannot drift the day Capture decorates the feed or changes its
    /// lifetime. That drift is the class this tier exists to close (#94/#108), so the harness must
    /// not open a fresh one.
    /// <para>
    /// The fakes come too, and must: three of the four browsers take <see cref="TimeProvider"/> and
    /// <c>WisdomBrowser</c> takes <see cref="MergeGate"/>, so without them only
    /// <c>EpisodeBrowser</c> resolves and the Wisdom and Injection surfaces throw at first render.
    /// <see cref="Clock"/> is registered as the <c>TimeProvider</c> rather than
    /// <c>TimeProvider.System</c> — a real clock here would read a different "now" from every other
    /// SUT this class composes — and the gate arrives through <see cref="CreateMergeGate"/>, so the
    /// embedder and the arbiter behind it are the class's scripted ones.
    /// </para>
    /// <para>
    /// Disposed with the test class. Skips when no Postgres is reachable, like every other member
    /// here — a component whose whole behaviour arrives through its parameters wants
    /// <c>RenderTestBase</c>'s disconnected tier instead, so its pins still run on a machine
    /// without Docker.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// A Project displayed under <paramref name="name"/>, at an identity and root unique to this
    /// call so a test seeding two of them gets two rows rather than a unique-index violation.
    /// Name them apart when a test filters by display name — nothing makes that column unique.
    /// </summary>
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
            // CaptureService's salience rule in shared test infrastructure, and the day that rule
            // changes every seeded row here would go on asserting the old one.
            Salient = salient,
        };
        Context.Events.Add(evt);
        await Context.SaveChangesAsync(Token);
        return evt;
    }

    /// <summary>
    /// A Wisdom and the version-1 row every Wisdom carries in production, so a chain assertion
    /// counts from the same floor whether the row was seeded or admitted through the gate.
    /// </summary>
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

    /// <summary>
    /// A Wisdom sourced from <paramref name="projectId"/>'s auto-memory and nothing else — the
    /// shape §7's native-content exclusion turns on, and the one provenance helper two suites
    /// wrote identically.
    /// </summary>
    private protected async Task<Provenance> AddHarvestProvenanceAsync(Guid wisdomId, Guid projectId)
    {
        var item = await AddHarvestedItemAsync(projectId, content: "harvested content");
        return await AddProvenanceAsync(wisdomId, harvestedItemId: item.Id);
    }

    /// <summary>
    /// One logged injection (§3). <paramref name="items"/> are the injected Wisdom and the scores
    /// that ordered them, in rank order. <paramref name="verdict"/> seeds the §9 mark a curator
    /// would have left, stamped at the base clock — every figure read off the mark wants entries
    /// already carrying one, and marking them through the browser afterwards is the same row
    /// written twice.
    /// </summary>
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

    /// <summary>One golden-set regression case (§9); hand-inserted unless a promotion is named.</summary>
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

    /// <summary>
    /// Empties the database before each test. A derived override runs its own setup around
    /// <c>base.InitializeAsync()</c>.
    /// </summary>
    public virtual async ValueTask InitializeAsync()
    {
        SkipIfUnavailable();
        await using var context = fixture.CreateContext();
        await ResetAsync(context, fixture.GlobalSeed);
    }

    /// <summary>
    /// Tears the class down, renderers first. Each is disposed inside its own try: a renderer can
    /// still be tearing a container down under a lifecycle query the test returned without
    /// awaiting, and one throwing there must not take the remaining renderers — or the context
    /// below — down with it, turning one teardown failure into a silent leak of the rest.
    /// <see cref="BunitContext.DisposeAsync"/> rather than <c>Dispose</c>, for the async
    /// provider-teardown path; it still does not await pending lifecycle tasks, so a test whose
    /// component is mid-query when it returns is relying on that query being harmless to abandon.
    /// </summary>
    public virtual async ValueTask DisposeAsync()
    {
        foreach (var render in _renderContexts)
        {
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

    /// <summary>
    /// Truncates every mapped table — CASCADE, so it reaches unmapped tables referencing one — and
    /// puts the §3 Global pseudo-project back from <see cref="ThrowawayDatabaseFixture.GlobalSeed"/>:
    /// the pristine row, read once before any test ran, rather than whatever the outgoing test left
    /// in that row. Restoring what was just read would carry a rename or an appended RootPath into
    /// every later test in the class — the one row that could still reintroduce #20/#22 ordering.
    /// It stays migration-sourced, so dropping the <c>HasData</c> seed leaves nothing to restore and
    /// the harness's own pin fails, which a hand-built copy here would hide. A fresh instance each
    /// time: re-adding the snapshot itself would hand every reset the same tracked object.
    ///
    /// The table list comes from the EF model rather than a hand-list, so a later entity is emptied
    /// the day it is mapped — but by the same token a second <c>HasData</c> seed, in any table,
    /// gets truncated and is not restored here. Adding one means extending this.
    /// </summary>
    private static async Task ResetAsync(MimirDbContext context, Project? globalSeed)
    {
        var tables = context.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(table => $"\"{table}\"");
        // Raw, not interpolated: every name comes from the EF model, and TRUNCATE takes no
        // parameters — a parameterized overload could not carry a table list at all.
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
