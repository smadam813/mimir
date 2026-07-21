using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Distillation;
using Mimir.Server.Storage;
using Pgvector;

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
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
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
                // The scoped context outlives the whole run, and no entity is needed across
                // items — a failed item's rolled-back rows especially must not stay tracked.
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

        // Embeddings depend only on the text, so the whole item embeds in one batched
        // round-trip up front — no model calls while the transaction below holds the connection.
        var vectors = candidates.Count == 0
            ? []
            : (await embeddings.GenerateAsync(
                    candidates.Select(c => c.Text), cancellationToken: cancellationToken))
                .Select(e => new Vector(e.Vector))
                .ToList();

        // One transaction per item: the marker commits with the item's Wisdom or not at all. The
        // gate's save after each candidate stays inside it — flushed rows are visible to the next
        // candidate's search on this connection, so near-identical sections of one file merge
        // instead of duplicating.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (var (candidate, embedding) in candidates.Zip(vectors))
        {
            await gate.AdmitAsync(
                new WisdomCandidate(candidate.Kind, item.ProjectId, candidate.Text, HarvestedItemId: item.Id),
                embedding,
                cancellationToken);
        }

        item.ConvertedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
