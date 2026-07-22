using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;
using Mimir.Server.Tests.Capture;
using Pgvector;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// §6.4's flag lifetime against a real Postgres: a Contested flag standing 14 days is cleared by
/// the sweep; a younger one — and everything about the Wisdom besides the flag — is untouched.
/// </summary>
public sealed class ContestedSweepTests(CaptureDatabaseFixture fixture)
    : IClassFixture<CaptureDatabaseFixture>, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

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
    public async Task OnlyFlagsPastTheContestedDuration_AreCleared()
    {
        var expired = await AddWisdomAsync(contestedAt: Now.AddDays(-15));
        var standing = await AddWisdomAsync(contestedAt: Now.AddDays(-13));
        var uncontested = await AddWisdomAsync(contestedAt: null);

        var sweep = new ContestedSweep(
            Context, Options.Create(new DistillationOptions()), new FakeTimeProvider(Now));
        (await sweep.ClearExpiredAsync(Token)).ShouldBe(1);

        (await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == expired, Token)))
            .ContestedAt.ShouldBeNull();
        (await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == standing, Token)))
            .ContestedAt.ShouldBe(Now.AddDays(-13));
        (await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == uncontested, Token)))
            .ContestedAt.ShouldBeNull();
    }

    private async Task<Guid> AddWisdomAsync(DateTimeOffset? contestedAt)
    {
        var project = TestData.NewProject("contested");
        var wisdom = new Wisdom
        {
            Id = Guid.CreateVersion7(),
            Kind = WisdomKind.Lesson,
            ScopeProjectId = project.Id,
            Text = $"Contested lesson {Guid.NewGuid():N}",
            Embedding = new Vector(TestVectors.WithCosine(0.5)),
            Reinforcement = 1,
            LastConfirmedAt = Now,
            ContestedAt = contestedAt,
        };
        Context.AddRange(project, wisdom);
        await Context.SaveChangesAsync(Token);
        return wisdom.Id;
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
