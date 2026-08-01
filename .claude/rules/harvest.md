---
paths:
  - "src/Mimir.Server/Harvest/**"
  - "src/Mimir.Server/Evaluation/**"
---

# Harvest and the golden runner

`HarvestScanner` loads every path's latest version whole and reduces "latest row per group" in memory, because that shape has no reliable EF Core translation. It is affordable only because the projection excludes `Content` and the version count grows at memory-file edit pace — keep both true if the query is touched.

`HarvestOptions.Root` is the read-only bind mount of the host's `~/.claude/projects` (ADR-0002: one-way, Mimir never writes back). An on-host run without Compose points it at that directory directly.

`GoldenRunner` replays its cases sequentially on purpose: the runner and the `QueryRanking` it drives share one `MimirDbContext`, so concurrent cases would interleave on a context that does not allow it. That the runner carries no DI registration at all is pinned by `ModuleRegistrationTests`.
