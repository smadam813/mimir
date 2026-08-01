using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;

namespace Mimir.Server.Distillation;

internal sealed class ContestedSweep(MimirDbContext db, IOptions<DistillationOptions> options, TimeProvider clock)
{
    public async Task<int> ClearExpiredAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - options.Value.ContestedDuration;
        return await db.Wisdom
            .Where(w => w.ContestedAt != null && w.ContestedAt <= cutoff)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(w => w.ContestedAt, (DateTimeOffset?)null),
                cancellationToken);
    }
}
