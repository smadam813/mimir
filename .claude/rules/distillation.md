---
paths:
  - "src/Mimir.Server/Distillation/**"
---

# Distillation: the §6 worker loop

`DistillerService.WorkAsync` is meant to end the loop only on a shutdown cancellation — anything else (Postgres still migrating, say) degrades the tile and retries after `FailureRetryInterval`, and the null figures `UpdateTile` then receives keep the tile's last known depth and `LastRunAt` rather than zeroing them. Neither statement is pinned, and neither can be until [#148](https://github.com/smadam813/mimir/issues/148) is fixed: today a pass that cannot reach storage escapes that catch and faults `ExecuteTask`, so a test asserting the intent goes red and one asserting the behaviour would pin the defect. Treat both as the intended contract, not as what the loop does.

The branch above them *is* pinned, and is the one to keep working: a queue turn that succeeded looks again immediately rather than waiting on the timer or the trigger (`DistillerServiceTests.AfterASuccess_TheWorkerLooksAgainImmediately_DrainingTheQueue`).
