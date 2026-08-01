---
paths:
  - "src/Mimir.Server/Distillation/**"
---

# Distillation: the queue's keeper, and the §6 worker loop

## Every queue transition is `DistillationQueue`'s

Enqueueing is not an operation there: an Episode is created at `Pending` — the §3 state set's starting value — and only a claim moves it off, which no unsealed row can be given. So Sealing is what enqueues, and the starting value is the whole mechanism. That leaves exactly **two** `Pending` writes outside that class, both riding along atomically with an update guarded on `sealed_at IS NULL`: the Seal's own in `CaptureService` (first-seal-wins) and the crash-Seal's in `DistillationSweep`. That guard is what makes each provably a no-op restate — an unsealed row is already `Pending` — and they are deliberate, because a reader of §6 expects Sealing to say what it does to the queue. Every other transition (`pending → running → done | failed`, and both ways back to `pending`) is stated in `DistillationQueue` once, so the legal moves are readable in one place instead of inferred from writes in four modules. A new queue-state write — a UI re-queue, a recovery path — goes there, not at its own call site.

No test can pin this. The two outside writes are provable no-ops, so removing either, or dropping its `sealed_at IS NULL` guard, changes nothing observable; the closure is a rule about where writes may *live*, which nothing at runtime can see.

## The worker loop

`DistillerService.WorkAsync` is meant to end the loop only on a shutdown cancellation — anything else (Postgres still migrating, say) degrades the tile and retries after `FailureRetryInterval`, and the null figures `UpdateTile` then receives keep the tile's last known depth and `LastRunAt` rather than zeroing them. Neither statement is pinned, and neither can be until [#148](https://github.com/smadam813/mimir/issues/148) is fixed: today a pass that cannot reach storage escapes that catch and faults `ExecuteTask`, so a test asserting the intent goes red and one asserting the behaviour would pin the defect. Treat both as the intended contract, not as what the loop does.

`DistillationSweepService.SweepAsync` states the same intent for the sweep — a failed pass is logged and retried after `SweepInterval`, the loop surviving — and does not hold it either, for what looks like the same reason. Against storage nothing can reach, no warning is logged at all and `ExecuteTask` reaches `Faulted`, ending the sweep for the life of the process; the throwing line is inside the `try` and the filter reads as though it must match. It surfaces later than the worker's does, because EF's transient-failure retry runs first — a check made 3 s in sees a loop still running and reads as healthy, which is why this went unnoticed. Doc-only for the same reason and under the same issue.

The branch above them *is* pinned, and is the one to keep working: a queue turn that succeeded looks again immediately rather than waiting on the timer or the trigger (`DistillerServiceTests.AfterASuccess_TheWorkerLooksAgainImmediately_DrainingTheQueue`).
