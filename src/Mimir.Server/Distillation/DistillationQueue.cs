using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mimir.Server.Configuration;
using Mimir.Server.Storage;
using Mimir.Server.Storage.Entities;

namespace Mimir.Server.Distillation;

/// <summary>
/// The §6 Distillation Queue: Sealed Episodes owed distillation, queued as state on the Episode
/// row rather than in a broker. This is its keeper — every transition after an Episode's creation
/// (<c>pending → running → done | failed</c> and both ways back to <c>pending</c>) and both of the
/// queue's reads are stated here once, so the legal moves are readable in one place instead of
/// inferred from writes in four modules.
/// </summary>
/// <remarks>
/// Enqueueing is not an operation here: an Episode is created at <c>pending</c> — the §3 state
/// set's starting value — and only a claim moves it off, which no unsealed row can be given. So
/// Sealing is what enqueues, and the starting value is the whole mechanism. That leaves exactly
/// two <c>pending</c> writes outside this class, both riding along atomically with an update
/// guarded on <c>sealed_at IS NULL</c>: the Seal's own in <see cref="Capture.CaptureService"/>
/// (first-seal-wins) and the crash-Seal's in <see cref="DistillationSweep"/>. That guard is what
/// makes each provably a no-op restate — an unsealed row is already <c>pending</c> — and they are
/// deliberate, because a reader of §6 expects Sealing to say what it does to the queue.
///
/// <para>The claim and depth queries are the consumers of the partial index over
/// <c>distillation</c> (<c>sealed_at IS NOT NULL AND distillation &lt;&gt; 'Done'</c>) declared in
/// <see cref="MimirDbContext"/>: both restrict on a Seal and neither can match a <c>done</c> row.
/// Changing a predicate here means revisiting that filter.</para>
/// </remarks>
internal sealed class DistillationQueue(
    MimirDbContext db,
    TimeProvider clock,
    IOptions<DistillationOptions> options)
{
    /// <summary>
    /// Boot's freshness cutoff: nothing is fresh, because no live worker exists to be holding a
    /// claim. Every real stamp sorts below it, so the shared re-queue matches every Running row —
    /// which is what "every claim a dead process left" has to mean, a claim stamped at or after
    /// this instant included. That is not hypothetical: a clock stepped backwards across the crash
    /// leaves a stamp in this process's future, and <em>now</em> as the cutoff would strand it
    /// Running until the stale window caught up.
    /// </summary>
    private static readonly DateTimeOffset NothingIsFresh = DateTimeOffset.MaxValue;

    /// <summary>
    /// Takes the oldest-Sealed pending Episode for the worker (<c>pending → running</c>, stamped).
    /// No claim race exists to guard against — §6's single-worker rule is what lets concurrent
    /// gate admissions be ignored.
    /// </summary>
    /// <returns>The claimed Episode — tracked on the caller's own scoped context, since the queue
    /// shares it, so the caller can read the Episode's stream and reload the instance after the
    /// gate's batch has moved the row underneath it — or null when the queue is empty.</returns>
    public async Task<Episode?> ClaimNextAsync(CancellationToken cancellationToken)
    {
        // An unsealed row is a live session sitting at the starting state, not work.
        var episode = await db.Episodes
            .Where(e => e.SealedAt != null && e.Distillation == DistillationState.Pending)
            .OrderBy(e => e.SealedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (episode is null)
        {
            return null;
        }

        episode.Distillation = DistillationState.Running;
        episode.DistillationStartedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return episode;
    }

    /// <summary>
    /// Parks a claim the worker could not finish (<c>running → failed</c>) for the sweep to
    /// re-queue. State-guarded and deliberately not a tracked write: the claim is queue state, and
    /// this must not fire on an Episode some other turn has moved on.
    /// </summary>
    public async Task FailAsync(Guid episodeId, CancellationToken cancellationToken)
        => await db.Episodes
            .Where(e => e.Id == episodeId && e.Distillation == DistillationState.Running)
            .ExecuteUpdateAsync(
                update => update.SetProperty(e => e.Distillation, DistillationState.Failed),
                cancellationToken);

    /// <summary>
    /// The <c>done</c> marker as the finalizer <see cref="MergeGate.AdmitAllAsync"/> takes, so the
    /// write text stays here while executing on the gate's batch context — the marker then commits
    /// with the Wisdom the Episode produced or not at all (#66), and a failure anywhere leaves the
    /// Episode still owed for the sweep to re-queue without inflating Reinforcement.
    /// </summary>
    public Func<MimirDbContext, CancellationToken, Task> CompleteMarker(Guid episodeId)
        => async (batch, cancellationToken) =>
        {
            // Read into a local: EF cannot translate a TimeProvider call inside SetProperty.
            var distilledAt = clock.GetUtcNow();
            await batch.Episodes
                .Where(e => e.Id == episodeId)
                .ExecuteUpdateAsync(
                    update => update
                        .SetProperty(e => e.Distillation, DistillationState.Done)
                        .SetProperty(e => e.DistilledAt, distilledAt),
                    cancellationToken);
        };

    /// <summary>
    /// Boot recovery: a Running claim found at start-up is a previous process's — §6's single
    /// worker means no one else can hold one — so every one goes back on the queue now instead of
    /// waiting out the sweep's stale window.
    /// </summary>
    /// <returns>How many claims were re-queued.</returns>
    public async Task<int> RequeueAbandonedAsync(CancellationToken cancellationToken)
        => await RequeueRunningAsync(NothingIsFresh, cancellationToken);

    /// <summary>
    /// The sweep's leg: only claims gone quiet past
    /// <see cref="DistillationOptions.StaleRunningAfter"/>, since the live worker is entitled to
    /// the rest. Same implementation as <see cref="RequeueAbandonedAsync"/>; the cutoff is the
    /// whole of the difference.
    /// </summary>
    /// <returns>How many claims were re-queued.</returns>
    public async Task<int> RequeueStaleAsync(CancellationToken cancellationToken)
        => await RequeueRunningAsync(clock.GetUtcNow() - options.Value.StaleRunningAfter, cancellationToken);

    /// <summary>The sweep's other leg: a parked failure gets another turn (<c>failed → pending</c>).</summary>
    /// <returns>How many Episodes were re-queued.</returns>
    public async Task<int> RequeueFailedAsync(CancellationToken cancellationToken)
        => await db.Episodes
            .Where(e => e.Distillation == DistillationState.Failed)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(e => e.Distillation, DistillationState.Pending)
                    .SetProperty(e => e.DistillationStartedAt, (DateTimeOffset?)null),
                cancellationToken);

    /// <summary>The §8 tile figure: Sealed Episodes still owed distillation.</summary>
    public async Task<int> QueueDepthAsync(CancellationToken cancellationToken)
        => await db.Episodes.CountAsync(
            e => e.SealedAt != null
                && (e.Distillation == DistillationState.Pending || e.Distillation == DistillationState.Running),
            cancellationToken);

    /// <summary>
    /// The one <c>running → pending</c>: a claim stamped before <paramref name="cutoff"/> — or not
    /// stamped at all, which cannot prove itself fresh when only the live worker's own claim is
    /// ever entitled to Running — goes back on the queue with its stamp cleared.
    /// </summary>
    private async Task<int> RequeueRunningAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        => await db.Episodes
            .Where(e => e.Distillation == DistillationState.Running
                && (e.DistillationStartedAt == null || e.DistillationStartedAt < cutoff))
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(e => e.Distillation, DistillationState.Pending)
                    .SetProperty(e => e.DistillationStartedAt, (DateTimeOffset?)null),
                cancellationToken);
}
