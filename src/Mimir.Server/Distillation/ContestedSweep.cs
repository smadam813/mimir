using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;

namespace Mimir.Server.Distillation;

/// <summary>
/// §6.4's second half: <c>contested_at</c> is cleared once it has stood for
/// <see cref="DistillationOptions.ContestedDuration"/> — the flag marks *recently* adjudicated
/// Wisdom for the UI, not a permanent stain. Runs inside the §6 sweep
/// (<see cref="DistillationSweep"/>) — 6 h resolution is plenty for a flag that lives 14 days.
/// </summary>
internal sealed class ContestedSweep(MimirDbContext db, IOptions<DistillationOptions> options, TimeProvider clock)
{
    /// <returns>How many Wisdom rows had their expired Contested flag cleared.</returns>
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
