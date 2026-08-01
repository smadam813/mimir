---
paths:
  - "src/Mimir.Server/Harvest/**"
  - "src/Mimir.Server/Evaluation/**"
---

# Harvest and the golden runner

`HarvestScanner` loads every path's latest version whole and reduces "latest row per group" in memory, because that shape has no reliable EF Core translation. It is affordable only because the projection excludes `Content` and the version count grows at memory-file edit pace — keep both true if the query is touched.

`HarvestOptions.Root` is the read-only bind mount of the host's `~/.claude/projects` (ADR-0002: one-way, Mimir never writes back). An on-host run without Compose points it at that directory directly.

`HarvesterService` catches cancellation in two places whose filters are deliberate inverses: `ScanAsync` degrades-and-retries every `OperationCanceledException` *except* a genuine host shutdown, which it lets past for `ExecuteAsync` to catch and end the loop on. Weaken `ScanAsync`'s and a shutdown gets logged as a retryable failure with a zombie retry scheduled behind it; weaken `ExecuteAsync`'s and a shutdown escapes the loop uncaught, silently ending all harvesting until the process restarts. Only `ScanAsync`'s half is pinned (`TheHostsOwnShutdown_EndsTheLoopWithoutDegradingTheTile`, alongside `ACancellationThatIsNotTheShutdowns_…` for the other branch). `ExecuteAsync`'s half is not, and cannot cheaply be: `BackgroundService.StopAsync` awaits through `Task.WhenAny`, which swallows a faulted execute task, so the branch's only observable is an Information log line — and `CapturedLog` drops everything below Warning by design. Change either filter and re-read the other by hand.

`GoldenRunner` replays its cases sequentially on purpose: the runner and the `QueryRanking` it drives share one `MimirDbContext`, so concurrent cases would interleave on a context that does not allow it. That the runner carries no DI registration at all is pinned by `ModuleRegistrationTests`.
