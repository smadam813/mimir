using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;

namespace Mimir.Server.Harvest;

/// <summary>
/// The §5 handoff from HarvestedItems to the Merge Gate. Works the conversion marker: every
/// version with <c>converted_at IS NULL</c> — new, or stored before the Wisdom tier shipped (the
/// Backfill's memory) — is split into candidates, gated, and marked, all in one transaction per
/// item. A crash mid-run redoes at most the in-flight item; everything committed is marked and
/// never gated again, which is what "exactly once" means across restarts.
/// </summary>
internal sealed class HarvestConverter(
    MimirDbContext db,
    MergeGate gate,
    IOptions<HarvestOptions> options,
    TimeProvider clock,
    ILogger<HarvestConverter> logger)
{
    /// <returns>How many pending HarvestedItem versions went through the gate.</returns>
    public async Task<int> ConvertPendingAsync(CancellationToken cancellationToken)
    {
        // Oldest first, so re-harvested content meets the Wisdom its earlier version produced.
        // Deliberately no gone_at filter: a file deleted before its conversion caught up was
        // still real memory, and §5 keeps derived Wisdom untouched by deletion either way.
        var pending = await db.HarvestedItems
            .Where(i => i.ConvertedAt == null)
            .OrderBy(i => i.LastChanged).ThenBy(i => i.Id)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        foreach (var itemId in pending)
        {
            await ConvertAsync(itemId, cancellationToken);
        }

        if (pending.Count > 0)
        {
            logger.LogInformation("Converted {Items} harvested item(s) through the Merge Gate", pending.Count);
        }

        return pending.Count;
    }

    private async Task ConvertAsync(Guid itemId, CancellationToken cancellationToken)
    {
        // One transaction per item: the marker commits with the item's Wisdom or not at all. The
        // save after each candidate stays inside it — flushed rows are visible to the next
        // candidate's search on this connection, so near-identical sections of one file merge
        // instead of duplicating.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var item = await db.HarvestedItems.FirstAsync(i => i.Id == itemId, cancellationToken);

        foreach (var (kind, text) in HarvestCandidates.Of(item.Content, options.Value.CandidateCap))
        {
            var candidate = new WisdomCandidate(kind, item.ProjectId, text, HarvestedItemId: item.Id);
            await gate.AdmitAsync(candidate, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        item.ConvertedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
