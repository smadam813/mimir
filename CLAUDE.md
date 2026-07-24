## Agent skills

### Issue tracker

Issues live in this repo's GitHub Issues (smadam813/mimir), managed via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-role vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`), each matching its GitHub label 1:1. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout — `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.

## Build & test

- `dotnet test Mimir.slnx` — Postgres-backed tests skip themselves when no Postgres is reachable (`docker compose up -d postgres`, or set `MIMIR_TEST_POSTGRES`). CI fails on any skip, so check the skip count locally before trusting green.
- Postgres test classes share one throwaway database per class, and xUnit's test order differs across machines: a test that queries beyond its own rows (counts, "oldest pending" claims) must first park or clean other tests' leftovers. This has broken CI twice (#20, #22) while passing locally.
- A test that pins a mechanism (a lock, a rollback, a cleanup) gets mutation-checked before review: remove the mechanism, confirm the test fails, restore. Review rounds reject vacuous assertions (#61).
- Whoever owns a transaction on the shared scoped DbContext also owns `ChangeTracker.Clear()` on failure — rolled-back `Added` entities re-insert on the caller's next save (see MergeGate.AdmitAllAsync, HarvestConverter, DistillationRun).
- EF migrations: `dotnet restore` first in a fresh worktree, then from `src/Mimir.Server`: `dotnet ef migrations add <Name> --output-dir Storage/Migrations`.
