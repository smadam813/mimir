## Architecture

.NET 10, three projects (`Mimir.slnx`): `src/Mimir.Server` is the modular monolith (pipeline modules under `Capture/`, `Harvest/`, `Distillation/`, `Recall/`, `Storage/`, plus the Blazor UI in `Components/`+`Ui/`), `src/Mimir.Cli` is the host companion (`mimir hook`, `mimir mcp`), `src/Mimir.Contracts` holds the DTOs between them. Postgres+pgvector is the single store.

`docs/spec/mimir-v1.md` is the buildable spec and is normative — the "§11", "§12" citations scattered through the code, CI comments, and config point into it.

Run it: `docker compose up -d postgres ollama`, then `dotnet run --project src/Mimir.Server` (port 6464; the `Development` config points at the compose-published services).

Service visibility follows the module, not the surface: taking an internal type in a public constructor is CS0051, so an internal service makes its consumers internal too (`MergeGate` → `WisdomBrowser`, #66). Blazor doesn't object — `@inject` generates a *private* property, so a public component injects an internal service from the same assembly fine.

## Build & test

- `dotnet test Mimir.slnx --filter "requires!=ollama"` — matches CI's filter (the golden suite needs Ollama, which CI deliberately lacks). Postgres-backed tests skip themselves when no Postgres is reachable (`docker compose up -d postgres`, or set `MIMIR_TEST_POSTGRES`). CI fails on any skip, so check the skip count locally before trusting green.
- Postgres test classes share one throwaway database per class, and xUnit's test order differs across machines: a test that queries beyond its own rows (counts, "oldest pending" claims) must first park or clean other tests' leftovers. This has broken CI twice (#20, #22) while passing locally.
- A test that pins a mechanism (a lock, a rollback, a cleanup) gets mutation-checked before review: remove the mechanism, confirm the test fails, restore. Review rounds reject vacuous assertions (#61). Restore from a copy of the file — `git checkout -- <path>` reverts it to HEAD and takes the uncommitted work with it.
- Prefer opening a transaction on a context you created (`IDbContextFactory<MimirDbContext>`) over borrowing the shared scoped one: rollback is then a dispose, and the failure cannot detach entities the caller had staged (MergeGate did this in #66). Borrow the scoped context and you inherit the discipline — `ChangeTracker.Clear()` on failure, or rolled-back `Added` entities re-insert on the caller's next save. `ProjectMerger`/`ProjectResolver` is the one place left that does.
- EF migrations: `dotnet restore` first in a fresh worktree, then from `src/Mimir.Server`: `dotnet ef migrations add <Name> --output-dir Storage/Migrations`.
- A test that never issues SQL (argument checks, pure validation) must not reach the code through a fixture's skip-gated context — build it over a never-connected `MimirDbContext` (plain `UseNpgsql("Host=...")`) so it runs, and fails, without Postgres.
- A test that pins a structural property (filter-before-LIMIT, SQL/EF parity) proves nothing until mutation-checked: apply the regression temporarily, confirm the test goes red, revert.
- Observing a lock wait: `pg_locks` rows for `transactionid` carry a NULL `database`, so a probe joining `pg_database` sees advisory waits only — and a mutation check that drops a lock then times out instead of going red, because the racer blocks on the row conflict rather than the lock it no longer takes. Poll `pg_stat_activity` filtered to `datname = current_database()` and `wait_event IN ('advisory','transactionid')` (#70).
- A hand-built DI graph in a test must mirror `AddMimirStorage`: both `AddDbContextFactory<MimirDbContext>` and `AddDbContext<MimirDbContext>(..., optionsLifetime: ServiceLifetime.Singleton)`. Register only one and a service taking the other won't resolve; drop the singleton options and the factory poisons every singleton resolving from the root (#23).
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
