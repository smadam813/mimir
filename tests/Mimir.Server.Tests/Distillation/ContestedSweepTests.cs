using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Tests.Distillation;

/// <summary>
/// §6.4's flag lifetime against a real Postgres: a Contested flag standing 14 days is cleared by
/// the sweep; a younger one — and everything about the Wisdom besides the flag — is untouched.
/// </summary>
public sealed class ContestedSweepTests(ThrowawayDatabaseFixture fixture) : PostgresTestBase(fixture)
{
    [Fact]
    public async Task OnlyFlagsPastTheContestedDuration_AreCleared()
    {
        var project = await AddProjectAsync("contested");
        var expired = await AddWisdomAsync(
            project.Id, "an expired contest", kind: WisdomKind.Lesson, contestedAt: Now.AddDays(-15));
        var standing = await AddWisdomAsync(
            project.Id, "a standing contest", kind: WisdomKind.Lesson, contestedAt: Now.AddDays(-13));
        var uncontested = await AddWisdomAsync(project.Id, "never contested", kind: WisdomKind.Lesson);

        var sweep = new ContestedSweep(Context, Options.Create(new DistillationOptions()), Clock);
        (await sweep.ClearExpiredAsync(Token)).ShouldBe(1);

        (await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == expired.Id, Token)))
            .ContestedAt.ShouldBeNull();
        (await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == standing.Id, Token)))
            .ContestedAt.ShouldBe(Now.AddDays(-13));
        (await FromDb(db => db.Wisdom.SingleAsync(w => w.Id == uncontested.Id, Token)))
            .ContestedAt.ShouldBeNull();
    }
}
