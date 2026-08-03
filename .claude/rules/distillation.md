---
paths:
  - "src/Mimir.Server/Distillation/**"
---

# Distillation: the queue's keeper, and the §6 worker loop

## Every queue transition is `DistillationQueue`'s

Enqueueing is not an operation there: an Episode is created at `Pending` — the §3 state set's starting value — and only a claim moves it off, which no unsealed row can be given. So Sealing is what enqueues, and the starting value is the whole mechanism. That leaves exactly **two** `Pending` writes outside that class, both riding along atomically with an update guarded on `sealed_at IS NULL`: the Seal's own in `CaptureService` (first-seal-wins) and the crash-Seal's in `DistillationSweep`. That guard is what makes each provably a no-op restate — an unsealed row is already `Pending` — and they are deliberate, because a reader of §6 expects Sealing to say what it does to the queue. Every other transition (`pending → running → done | failed`, and both ways back to `pending`) is stated in `DistillationQueue` once, so the legal moves are readable in one place instead of inferred from writes in four modules. A new queue-state write — a UI re-queue, a recovery path — goes there, not at its own call site.

No test can pin this. The two outside writes are provable no-ops, so removing either, or dropping its `sealed_at IS NULL` guard, changes nothing observable; the closure is a rule about where writes may *live*, which nothing at runtime can see.

## The worker loop

`DistillerService.WorkAsync` ends the loop only on a shutdown cancellation — anything else (Postgres still migrating, say) degrades the tile and retries after `FailureRetryInterval`, and the null figures `UpdateTile` then receives keep the tile's last known depth and `LastRunAt` rather than zeroing them. `DistillationSweepService.SweepAsync` states the same contract for the sweep: a failed pass is logged and retried after `SweepInterval`, the loop surviving.

Both are pinned, by `DistillerServiceFailureTests` and `DistillationSweepServiceFailureTests`. Neither inherits `PostgresTestBase` and neither may: their subject is a pass that cannot reach storage at all, so they run over `UnreachableStorage` and have to be able to fail on the machine that has no Postgres, which is exactly where an escaping connection failure would bite. Each drives its retry off `LoopClock.StraddleAsync`, which takes the loop's park before it advances anything: the interval is read off the registered timer, then crossed in two steps so a *lengthened* wait fails as loudly as a shortened one. Advancing before the loop has parked is the failure mode that shape exists to rule out — the timer registered afterwards computes its due time from the already-advanced clock, so the advance is lost and the wait after it times out for no reason a reader can see.

The branch above them *is* pinned, and is the one to keep working: a queue turn that succeeded looks again immediately rather than waiting on the timer or the trigger (`DistillerServiceTests.AfterASuccess_TheWorkerLooksAgainImmediately_DrainingTheQueue`).
