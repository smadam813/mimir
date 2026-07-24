## Architecture

.NET 10, three projects (`Mimir.slnx`): `src/Mimir.Server` is the modular monolith (pipeline modules under `Capture/`, `Harvest/`, `Distillation/`, `Recall/`, `Storage/`, plus the Blazor UI in `Components/`+`Ui/`), `src/Mimir.Cli` is the host companion (`mimir hook`, `mimir mcp`), `src/Mimir.Contracts` holds the DTOs between them. Postgres+pgvector is the single store.

`docs/spec/mimir-v1.md` is the buildable spec and is normative — the "§11", "§12" citations scattered through the code, CI comments, and config point into it.

Run it: `docker compose up -d postgres ollama`, then `dotnet run --project src/Mimir.Server` (port 6464; the `Development` config points at the compose-published services).

## Build & test

- `dotnet test Mimir.slnx --filter "requires!=ollama"` — matches CI's filter (the golden suite needs Ollama, which CI deliberately lacks). Postgres-backed tests skip themselves when no Postgres is reachable (`docker compose up -d postgres`, or set `MIMIR_TEST_POSTGRES`). CI fails on any skip, so check the skip count locally before trusting green.
- Postgres test classes share one throwaway database per class, and xUnit's test order differs across machines: a test that queries beyond its own rows (counts, "oldest pending" claims) must first park or clean other tests' leftovers. This has broken CI twice (#20, #22) while passing locally.
- A test that pins a mechanism (a lock, a rollback, a cleanup) gets mutation-checked before review: remove the mechanism, confirm the test fails, restore. Review rounds reject vacuous assertions (#61).
- Whoever owns a transaction on the shared scoped DbContext also owns `ChangeTracker.Clear()` on failure — rolled-back `Added` entities re-insert on the caller's next save (see MergeGate.AdmitAllAsync, HarvestConverter, DistillationRun).
- EF migrations: `dotnet restore` first in a fresh worktree, then from `src/Mimir.Server`: `dotnet ef migrations add <Name> --output-dir Storage/Migrations`.
- A test that never issues SQL (argument checks, pure validation) must not reach the code through a fixture's skip-gated context — build it over a never-connected `MimirDbContext` (plain `UseNpgsql("Host=...")`) so it runs, and fails, without Postgres.
- A test that pins a structural property (filter-before-LIMIT, SQL/EF parity) proves nothing until mutation-checked: apply the regression temporarily, confirm the test goes red, revert.
- After pushing a PR: `gh pr checks <n> --watch` — CI is the arbiter (fails on skips, runs on Linux); local green is not.
- Bumping `global.json`'s SDK version is a dependency bump: SDK-delivered packages are pinned in every `packages.lock.json`. Local restores are unlocked so it passes quietly; CI restores locked and fails. Regenerate all five lock files in the same commit.
- `appsettings.json` restates the §11 defaults baked into the `Configuration/` options classes, and `AppSettingsTests` fails on drift — change a default in both places.
- `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on; only the NuGet-audit codes (NU1900–NU1904) are exempt (ADR-0007). A style warning fails the build.

## Agent skills

### Issue tracker

Issues live in this repo's GitHub Issues (smadam813/mimir), managed via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-role vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`), each matching its GitHub label 1:1. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context layout — `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
