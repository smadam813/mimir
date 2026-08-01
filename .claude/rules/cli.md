---
paths:
  - "src/Mimir.Cli/**"
  - "src/Mimir.Contracts/**"
---

# CLI: the host companion, and the contracts it shares

`mimir hook` exits 0 on every path — argument mistakes and a malformed `MIMIR_URL` included — because a hook that fails must never break or slow the session that invoked it (§1, §4). `mimir mcp` is the opposite lane and may fail loudly: Claude Code surfaces a dead MCP server, so silence there would be the worse answer. That asymmetry is why the MCP request timeout is a generous 30 s against the hooks' `HookLimits.RoundTripCap` — nothing on the MCP path blocks a session.

`HookLimits.RoundTripCap` lives in `Mimir.Contracts` because both assemblies quote it: the CLI bounds its round-trip with it, and `BriefTripwire` formats it into the warning line a Brief carries. It used to be a `const string "3s"` restated server-side. Its value is pinned twice — `HookCommandTests` on the constant, `BriefTripwireTests` on the rendered line — so a retune has to be deliberate at both ends.

`ProjectLocator` runs git under a 2 s per-call ceiling inside the hook's overall cap, and starts the toplevel and remote lookups together: they read the same cwd with no data dependency, and one round-trip rather than two is what fits the prompt hook's 500 ms budget (§11). Every git invocation is fire-and-kill — the remote task is always awaited, both pipes are drained during the wait, and any failure kills a still-running process — because a hook that leaves git behind has outlived its own cap.
