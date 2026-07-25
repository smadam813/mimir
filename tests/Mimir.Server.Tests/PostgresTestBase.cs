using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Recall;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Distillation;
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
    /// The §7 query ranking over the fixture's database, the fake embedder and the base clock —
    /// the four consumers that replay a query through it all want the same graph.
    /// </summary>
    private protected QueryRanking CreateQueryRanking(SearchOptions? search = null)
        => new(
            Context,
            Embeddings,
            new WisdomSearch(Context, Options.Create(search ?? new SearchOptions())),
            Options.Create(new RecallOptions()),
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
        bool? salient = null)
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
            Salient = salient ?? type == EventType.Remember,
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

    /// <summary>
    /// Empties the database before each test. A derived override runs its own setup around
    /// <c>base.InitializeAsync()</c>.
    /// </summary>
    public virtual async ValueTask InitializeAsync()
    {
        SkipIfUnavailable();
        await using var context = fixture.CreateContext();
        await ResetAsync(context);
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (_context is not null)
        {
            await _context.DisposeAsync();
        }
    }

    /// <summary>
    /// Truncates every mapped table — CASCADE, so it reaches unmapped tables referencing one — and
    /// puts the §3 Global pseudo-project back, carried over rather than fabricated: the migration's
    /// <c>HasData</c> is what first put that row there, so a harness re-inserting a hand-built copy
    /// would keep passing after the seed was dropped from the model. The table list comes from the
    /// EF model rather than a hand-list, so a later entity is emptied the day it is mapped.
    /// </summary>
    private static async Task ResetAsync(MimirDbContext context)
    {
        var global = await context.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(project => project.Id == Project.GlobalId, Token);

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

        if (global is not null)
        {
            context.Projects.Add(global);
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
