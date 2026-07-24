using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;

namespace Mimir.Server.Harvest;

/// <summary>
/// The §5 handoff from HarvestedItems to the Merge Gate. Works the conversion marker: every
/// version with <c>converted_at IS NULL</c> — new, or stored before the Wisdom tier shipped (the
/// Backfill's memory) — is split into candidates and handed to the gate as one Admission batch
/// per item, the marker riding along as the batch's finalizer. A crash mid-run redoes at most the
/// in-flight item; everything committed is marked and never gated again, which is what "exactly
/// once" means across restarts.
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

        var converted = 0;
        ExceptionDispatchInfo? firstFailure = null;
        foreach (var itemId in pending)
        {
            try
            {
                await ConvertAsync(itemId, cancellationToken);
                converted++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One item must not dam the queue: the rest still convert, the failure
                // resurfaces below so the tile degrades, and the still-null marker retries
                // this item next tick.
                logger.LogWarning(ex, "Converting harvested item {ItemId} failed; continuing with the rest", itemId);
                firstFailure ??= ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                // The scoped context outlives the whole run and no entity is needed across
                // items — the gate clears what its own rolled-back batch staged, this drops the
                // rest, item included, so nothing from one item rides another's admission.
                db.ChangeTracker.Clear();
            }
        }

        if (converted > 0)
        {
            logger.LogInformation("Converted {Items} harvested item(s) through the Merge Gate", converted);
        }

        firstFailure?.Throw();
        return converted;
    }

    private async Task ConvertAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.HarvestedItems.FirstAsync(i => i.Id == itemId, cancellationToken);
        var candidates = HarvestCandidates.Of(item.Content, options.Value.CandidateCap);

        // One Admission batch per item: the finalizer's marker commits with the item's Wisdom or
        // not at all, and the gate's save after each candidate keeps near-identical sections of
        // one file merging instead of duplicating.
        await gate.AdmitAllAsync(
            candidates
                .Select(c => new WisdomCandidate(c.Kind, item.ProjectId, c.Text, HarvestedItemId: item.Id))
                .ToList(),
            _ =>
            {
                item.ConvertedAt = clock.GetUtcNow();
                return Task.CompletedTask;
            },
            cancellationToken);
    }
}
