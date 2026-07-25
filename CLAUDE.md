## Architecture

.NET 10, three projects (`Mimir.slnx`): `src/Mimir.Server` is the modular monolith (pipeline modules under `Capture/`, `Harvest/`, `Distillation/`, `Recall/`, `Storage/`, plus the Blazor UI in `Components/`+`Ui/`), `src/Mimir.Cli` is the host companion (`mimir hook`, `mimir mcp`), `src/Mimir.Contracts` holds the DTOs between them. Postgres+pgvector is the single store.

`docs/spec/mimir-v1.md` is the buildable spec and is normative — the "§11", "§12" citations scattered through the code, CI comments, and config point into it.

Run it: `docker compose up -d postgres ollama`, then `dotnet run --project src/Mimir.Server` (port 6464; the `Development` config points at the compose-published services).

Service visibility follows the module, not the surface: taking an internal type in a public constructor is CS0051, so an internal service makes its consumers internal too (`MergeGate` → `WisdomBrowser`, #66). Blazor doesn't object — `@inject` generates a *private* property, so a public component injects an internal service from the same assembly fine.

Doc comments carry normative rules, and the thin `Ui/` delegates restate their service's — `WisdomBrowser.EditAsync` restated the Merge Gate's no-op set and went stale the moment #71 named the full one. Change a rule stated in a comment and grep for the other statements of it.

Raw string literals carry the file's line endings, and the checkout decides those: the index is LF (`core.autocrlf=true`, no `.gitattributes`), so a Windows working copy reads CRLF while a Linux one — CI, and the Docker image that ships — reads LF. Every prompt assembled in a `"""…"""` block therefore has platform-dependent separators, and no prompt pin can see it: they assert with `ShouldContain` and `TrimEnd().ShouldEndWith`. So don't extract a text builder that supplies its own newline for anything sent verbatim to a model — `"\n"` would pin LF on Windows too, and `Environment.NewLine` only agrees by accident. Keep the text in one literal: #77 kept `/no_think` a `const` interpolated into each caller's own prompt for that reason.

## Build & test

- `dotnet test Mimir.slnx --filter "requires!=ollama"` — matches CI's filter (the golden suite needs Ollama, which CI deliberately lacks). Postgres-backed tests skip themselves when no Postgres is reachable (`docker compose up -d postgres`, or set `MIMIR_TEST_POSTGRES`). CI fails on any skip, so check the skip count locally before trusting green.
- Postgres-backed test classes inherit `PostgresTestBase` (tests root) and write no plumbing: it owns the skip-gated `Context`/`Contexts`/`CreateContext`/`ConnectionString`, `Token`, `FromDb`, the fakes (`Embeddings`, `Arbiter`, `Chat`, `Clock`), the composed SUTs (`CreateMergeGate`, `CreateQueryRanking`), `AddThrowawayStorage` for a test booting its own DI graph, and the `AddProjectAsync`/`AddEpisodeAsync`/`AddEventAsync`/`AddWisdomAsync`/`AddHarvestedItemAsync`/`AddProvenanceAsync`/`AddHarvestProvenanceAsync`/`AddInjectionAsync`/`AddGoldenCaseAsync` seeders. It truncates every *mapped* table before each test, so a test may assert on whole-table counts and needs no prefix scoping, no queue parking and no hand-reset — the sibling-order failures that broke CI twice (#20, #22) are gone by construction (#73). Tables a test creates itself (`PostgresStorageProbeTests`' scratch tables) are outside that and live until the class's database is dropped. Extend the harness rather than restating any of it in a class.
- `GoldenSuiteTests` is the one Postgres-backed class that must *not* inherit `PostgresTestBase`: the §9 golden suite replays GoldenCases out of the **development** database, so an empty throwaway would leave it sweeping zero cases and passing forever. Its hand-plumbed `TestPostgres.AdminConnectionString` context is deliberate.
- A test that pins a mechanism (a lock, a rollback, a cleanup) gets mutation-checked before review: remove the mechanism, confirm the test fails, restore. Restore from a copy of the file — `git checkout -- <path>` reverts it to HEAD and takes the uncommitted work with it. Record each result in the commit message or PR body — reviewers ask for the red runs otherwise. Review rounds reject vacuous assertions (#61).
- Prefer opening a transaction on a context you created (`IDbContextFactory<MimirDbContext>`) over borrowing the shared scoped one: rollback is then a dispose, and the failure cannot detach entities the caller had staged (MergeGate did this in #66). Borrow the scoped context and you inherit the discipline — `ChangeTracker.Clear()` on failure, or rolled-back `Added` entities re-insert on the caller's next save. `ProjectMerger`/`ProjectResolver` is the one place left that does.
- EF migrations: `dotnet restore` first in a fresh worktree, then from `src/Mimir.Server`: `dotnet ef migrations add <Name> --output-dir Storage/Migrations`.
- A test that never issues SQL (argument checks, pure validation) must not inherit `PostgresTestBase` — its skip-gated context would hide the guard on a machine with no Postgres. Build it over the shared `DisconnectedContextFactory` (or a plain never-connected `MimirDbContext`) so it runs, and fails, everywhere.
- A test that pins a structural property (filter-before-LIMIT, two methods over one clause) proves nothing until mutation-checked: apply the regression temporarily, confirm the test goes red, revert. Run each mutation against every entry point — a predicate enforced redundantly elsewhere keeps one of them green (#67: the search legs' own `@include_retired` masked a broken ambient clause; only the queryless listing caught it). "Every entry point" means every site the guard could sit, not just every public method — `MergeGate.EditAsync` reads its row twice (unlocked pre-check, then again under the lock), so a guard mutation belongs at both (#71).
- Observing a lock wait: `pg_locks` rows for `transactionid` carry a NULL `database`, so a probe joining `pg_database` sees advisory waits only — and a mutation check that drops a lock then times out instead of going red, because the racer blocks on the row conflict rather than the lock it no longer takes. Poll `pg_stat_activity` filtered to `datname = current_database()` and `wait_event IN ('advisory','transactionid')` (#70).
- A hand-built DI graph in a test must mirror `AddMimirStorage`: both `AddDbContextFactory<MimirDbContext>` and `AddDbContext<MimirDbContext>(..., optionsLifetime: ServiceLifetime.Singleton)`. Register only one and a service taking the other won't resolve; drop the singleton options and the factory poisons every singleton resolving from the root (#23).
- Reading Wisdom as ids-then-hydrate opens a window — the row can retire between the two queries. Re-assert `RetiredAt == null` in the hydration (`BriefService` and `InjectionBrowser.PromoteAsync` do; `QueryRanking` does not, so the Prompt lane's window is still open). No test can force the window without an interceptor, so it is defense in depth, not a pinned mechanism.
- After pushing a PR: `gh pr checks <n> --watch` — CI is the arbiter (fails on skips, runs on Linux); local green is not.
- Bumping `global.json`'s SDK version is a dependency bump: SDK-delivered packages are pinned in every `packages.lock.json`. Local restores are unlocked so it passes quietly; CI restores locked and fails. Regenerate all five lock files in the same commit.
- `appsettings.json` restates the §11 defaults baked into the `Configuration/` options classes, and `AppSettingsTests` fails on drift — change a default in both places.
- `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on; only the NuGet-audit codes (NU1900–NU1904) are exempt (ADR-0007). A style warning fails the build.

## Agent skills

### Issue tracker

Issues live in this repo's GitHub Issues (smadam813/mimir), managed via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-role vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`), each matching its GitHub label 1:1. See `docs/agents/triage-labels.md`.

A follow-up filed out of a review round is `needs-triage` until its fix has been grilled to a decision record; `ready-for-agent` means that decision walk is done (#66/#67 versus #68/#72).

### Domain docs

Single-context layout — `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.
