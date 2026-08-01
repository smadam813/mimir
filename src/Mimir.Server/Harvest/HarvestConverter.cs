using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;

namespace Mimir.Server.Harvest;

internal sealed class HarvestConverter(
    MimirDbContext db,
    MergeGate gate,
    IOptions<HarvestOptions> options,
    TimeProvider clock,
    ILogger<HarvestConverter> logger)
{
    public async Task<int> ConvertPendingAsync(CancellationToken cancellationToken)
    {
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
                logger.LogWarning(ex, "Converting harvested item {ItemId} failed; continuing with the rest", itemId);
                firstFailure ??= ExceptionDispatchInfo.Capture(ex);
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
        var item = await db.HarvestedItems.AsNoTracking().FirstAsync(i => i.Id == itemId, cancellationToken);
        var candidates = HarvestCandidates.Of(item.Content, options.Value.CandidateCap);

        await gate.AdmitAllAsync(
            candidates
                .Select(c => new WisdomCandidate(c.Kind, item.ProjectId, c.Text, HarvestedItemId: item.Id))
                .ToList(),
            async (batch, ct) =>
            {
                // Read into a local: EF cannot translate a TimeProvider call inside SetProperty.
                var convertedAt = clock.GetUtcNow();
                await batch.HarvestedItems
                    .Where(i => i.Id == itemId)
                    .ExecuteUpdateAsync(update => update.SetProperty(i => i.ConvertedAt, convertedAt), ct);
            },
            cancellationToken);
    }
}
