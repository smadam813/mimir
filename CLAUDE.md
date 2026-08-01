## Architecture

.NET 10, five projects in `Mimir.slnx`: three under `src/`, two test projects under `tests/`. `src/Mimir.Server` is the modular monolith (pipeline modules under `Capture/`, `Harvest/`, `Distillation/`, `Recall/` over `Storage/`, `Evaluation/` for the §9 golden runner, and the Blazor UI in `Components/`+`Ui/`), `src/Mimir.Cli` is the host companion (`mimir hook`, `mimir mcp`), `src/Mimir.Contracts` holds the DTOs between them. Postgres+pgvector is the single store.

A module owns its DI registrations and its HTTP surface, both in its `IMimirModule` implementation in `Modules/Modules.cs`; `Program.cs` only walks the list. A new pipeline service registers there, not in `Program.cs`. `Configuration/`, `Models/` (Ollama provisioning) and `Health/` sit outside the pipeline and register themselves.

`docs/spec/mimir-v1.md` is the buildable spec and is normative. The "§11", "§12" citations scattered through the code, CI comments, and config point into it.

Run it: `docker compose up -d postgres ollama`, then `dotnet run --project src/Mimir.Server` (port 6464; the `Development` config points at the compose-published services).

Service visibility follows the module, not the surface: taking an internal type in a public constructor is CS0051, so an internal service makes its consumers internal too (`MergeGate` → `WisdomBrowser`, #66). Blazor is the exception, because `@inject` generates a private property, so a public component injects an internal service from the same assembly fine.

A comment never carries a behavioral rule. A rule has exactly one home: the test that pins it; or, where no test can reach it, one entry in `.claude/rules/` scoped to the paths it governs — or a paragraph in this file where the rule would cause mistakes in a session that never opens those paths, per "Path-scoped rules" below. The modules are being converted one slice at a time and #129 tracks the sweep, so a doc comment still carrying a rule is unconverted rather than sanctioned: `Ui/`, `Components/`, `Capture/`, `Distillation/` and `Storage/` are done; the long thin tail (`Recall/`, `Harvest/`, the small modules, `Mimir.Cli`, `Mimir.Contracts`) and the test projects are not. Until it finishes, changing a rule still means grepping for the other statements of it — an unswept module restates its neighbours' rules. Three comment kinds are legal: a constraint imported from outside the code ("Npgsql refuses non-UTC"); a one-line guard on a deliberate oddity — an empty catch, a seemingly redundant re-assert, a name chosen against the grain — saying why it exists and, where relevant, why nothing pins it; and tooling (`#pragma` justifications, `<inheritdoc/>`). Where prose would restate a rule enforced elsewhere in code, delegate to the enforcing member instead (`MergeGate.NoOpOf`) rather than describing it. (#129)

Raw string literals carry the file's line endings, and the checkout decides those: the index is LF (`core.autocrlf=true`, no `.gitattributes`), so a Windows working copy reads CRLF while a Linux one (CI, and the Docker image that ships) reads LF. Every prompt assembled in a `"""…"""` block therefore has platform-dependent separators, and no prompt pin catches it: they assert with `ShouldContain` and `TrimEnd().ShouldEndWith`. So don't extract a text builder that supplies its own newline for anything sent verbatim to a model: `"\n"` would pin LF on Windows too, and `Environment.NewLine` only agrees by accident. Keep the text in one literal. #77 kept `/no_think` a `const` interpolated into each caller's own prompt for that reason.

## Build & test

- `dotnet test Mimir.slnx --filter "requires!=ollama"` matches CI's filter (the golden suite needs Ollama, which CI deliberately lacks). Postgres-backed tests skip themselves when no Postgres is reachable (`docker compose up -d postgres`, or set `MIMIR_TEST_POSTGRES`). CI fails on any skip, so check the skip count locally before trusting green.
- Absorbing a seam must not push pure computation behind a Postgres-backed entry point, or its pins go behind one too and skip on the machine where the mistake is being made. Keep the pure part an internal builder the service calls (`InjectionLabel`, `InjectionWrapper`) rather than a private method (#83). Always-on rather than scoped to `tests/`, because the shape is chosen in the service, in a session that may never open a test file.
- Prefer opening a transaction on a context you created (`IDbContextFactory<MimirDbContext>`) over borrowing the shared scoped one: rollback is then a dispose, and the failure cannot detach entities the caller had staged (MergeGate did this in #66). Borrow the scoped context and you inherit the discipline: `ChangeTracker.Clear()` on failure, or rolled-back `Added` entities re-insert on the caller's next save. `ProjectMerger`/`ProjectResolver` is the one place left that does.
- Reading Wisdom as ids-then-hydrate opens a window, because the row can retire between the two queries. Re-assert `RetiredAt == null` in the hydration (`BriefService` and `InjectionBrowser.PromoteAsync` do; `QueryRanking` does not, so the Prompt lane's window is still open). No test can force the window without an interceptor, so it is defense in depth, not a pinned mechanism.
- After pushing a PR, run `gh pr checks <n> --watch`. CI is the arbiter (it fails on skips and runs on Linux); local green is not. Its totals count the *merge* commit, so they exceed local whenever `main` has moved ahead of the branch: check `git log HEAD..origin/main` before treating a mismatch as a discovery problem.
- Bumping `global.json`'s SDK version is a dependency bump: SDK-delivered packages are pinned in every `packages.lock.json`. Local restores are unlocked so it passes quietly; CI restores locked and fails. Regenerate all five lock files in the same commit.
- `appsettings.json` restates the §11 defaults baked into the `Configuration/` options classes, and `AppSettingsTests` fails on drift, so change a default in both places.
- `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild` are on; only the NuGet-audit codes (NU1900–NU1904) are exempt (ADR-0007). A style warning fails the build.
- A `dotnet run --project src/Mimir.Server` left running from manual testing locks `Mimir.Contracts.dll`/`Mimir.Server.dll`; the next `dotnet build`/`dotnet test` fails with MSB3027 until that process is killed.
- A `.OwnsMany(...).ToJson(...)` jsonb column isn't LINQ-inert: `db.Injections.Where(i => i.Items.Any(x => !db.Wisdom.Any(w => w.Id == x.WisdomId)))` translates to a real Postgres `NOT EXISTS`, verified against Postgres (#89's review round). Try LINQ before writing raw SQL against a jsonb owned collection.
- `mcp__Claude_Browser__preview_start {name: ...}` reads `.claude/launch.json` from the session's *original* directory, not a worktree entered later via `EnterWorktree`. If you've switched worktrees mid-session, start the server manually (`dotnet run --project src/Mimir.Server`) and use `preview_start {url: "http://localhost:6464"}` instead.
- `.claude/settings.json` enables the `csharp-lsp` plugin, which only shells out to a `csharp-ls` binary it does not install: without it the plugin loads and the language server is inert, with no error saying so. `dotnet tool install --global csharp-ls` is one-time human setup — it mutates the machine, not the checkout, so no allow rule covers it and an unattended session cannot self-heal. It reads `Mimir.slnx` directly (community server, not Microsoft's Roslyn LS) and its first solution load costs ~90 s, seconds thereafter (#132).

## Path-scoped rules

This file is the repo's only always-on instruction file, so ambient context has exactly one place to audit. Path-specific material lives in `.claude/rules/*.md`, each scoped by a `paths:` frontmatter glob and loaded only once Claude reads a file it governs — `blazor-ui.md`, `capture.md`, `distillation.md`, `tests.md`, `storage.md`, `claude-settings.md`, each naming its own paths. No rule there goes unscoped — a rule that would cause mistakes in a session that never touches its governed paths belongs here instead. Path-scoped rules drop out after compaction until re-triggered, which is why nothing load-bearing outside its paths may live in one.

## Agent skills

### Issue tracker

Issues live in this repo's GitHub Issues (smadam813/mimir), managed via the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

Default five-role vocabulary (`needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`), each matching its GitHub label 1:1. See `docs/agents/triage-labels.md`.

A follow-up filed out of a review round is `needs-triage` until its fix has been grilled to a decision record; `ready-for-agent` means that decision walk is done (#66/#67 versus #68/#72).

### Domain docs

Single-context layout: `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.

### Design source

The Mimir Mono UI design (#86) and the Nocturne design system it's built on live in claude.ai/design projects. Read them with the `DesignSync` tool (`list_projects` → `get_file`). WebFetch 403s on them (no claude.ai auth session), and browser automation works but is much slower for pulling file contents.
