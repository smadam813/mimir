# Research: official Claude Code repo-optimization guidance

Resolves [#125](https://github.com/smadam813/mimir/issues/125), for map [#124](https://github.com/smadam813/mimir/issues/124). What Anthropic officially recommends for making a repository maximally discoverable and effective for Claude Code — per mechanism: what it is, what it's recommended for, and its cost. Feeds a per-mechanism adopt/skip decision session.

Investigated 2026-07-31 against the official Claude Code documentation (code.claude.com/docs) and official Anthropic engineering/research posts. Primary sources only; each claim cites its URL. Facts marked *(inference)* are ours, not the docs'. Overlapping mechanics were already pinned by the 2026-07-19 integration-surface research ([docs/research/claude-code-integration-surface.md](claude-code-integration-surface.md), issue #2); this doc cross-references rather than restates where that one is deeper (hook payloads, MCP limits, auto memory).

## TL;DR

The official posture is: **keep CLAUDE.md lean (target under 200 lines) and push everything else into on-demand surfaces** — path-scoped rules files (`.claude/rules/*.md` with `paths:` frontmatter), skills (name+description at startup, body on invocation), and subdirectory CLAUDE.md files (load when files there are read). Advisory text (CLAUDE.md, rules, skills) is for guidance; **hooks are for enforcement** — anything that must happen every time. `.claude/settings.json` is the committed team-shared carrier for permissions, hooks, and MCP enablement. Official code-intelligence (LSP) plugins exist for some languages; whether a C# one exists is unconfirmed and is an open question for the adoption session. No official Postgres or .NET MCP server is named in the docs; GitHub MCP and Playwright MCP are the two officially-referenced servers plausibly useful here.

---

## 1. CLAUDE.md / memory files

**What it is.** Markdown context files loaded into every session, concatenated broad→specific: managed policy file → user `~/.claude/CLAUDE.md` → project `./CLAUDE.md` or `./.claude/CLAUDE.md` → `./CLAUDE.local.md` (still supported, auto-gitignored). Files in the working directory and its ancestors load in full at launch; files in *subdirectories* load on demand, when Claude reads files in that directory. Source: <https://code.claude.com/docs/en/memory>, <https://code.claude.com/docs/en/large-codebases>.

**Official recommendations.**

- **Size: target under 200 lines per file.** No hard limit exists — the file loads in full regardless — but longer files "consume more context and reduce adherence" (<https://code.claude.com/docs/en/memory>).
- **Content test:** "for each line, ask: would removing this cause Claude to make mistakes? If not, cut it." Include bash commands Claude can't guess, style rules that differ from defaults, and gotchas; exclude API documentation and long explanations. Prefer pointers over inline content. Source: <https://code.claude.com/docs/en/best-practices>.
- **Imports:** `@path/to/file` inlines another file at launch, recursive to a maximum depth of 4 hops; relative paths resolve from the importing file; `@`-refs inside backticks/code fences stay literal. Note imports organize but do **not** save context — the imported file still loads at launch. Source: <https://code.claude.com/docs/en/memory>.
- **Bootstrap:** `/init` generates a starting CLAUDE.md; `CLAUDE_CODE_NEW_INIT=1` enables an interactive multi-phase flow. The `#` shortcut and "remember this" write to auto memory (see below), not CLAUDE.md.
- **Auto memory** (adjacent, not a repo file): `MEMORY.md`'s first 200 lines / 25 KB load at session start, topic files on demand; per-project opt-out via `autoMemoryEnabled: false`. Details in the [#2 research doc §3.2](claude-code-integration-surface.md).

**Cost.** Root CLAUDE.md is a permanent per-session context tax and is re-injected from disk after compaction, so every line is paid on every conversation. Maintenance cost is the drift documented in this repo already (stale restatements — see CLAUDE.md's own doc-comments rule). *(inference)* For Mimir's large/dense CLAUDE.md, the official direction is unambiguous: cut toward 200 lines and move path-specific material into rules files.

**Where guidance is silent:** no stated token budget beyond the 200-line target; no official ratio of pointers vs inline content; no guidance on ordering sections within CLAUDE.md.

## 2. Path/glob-activated rules files

**What it is.** `.claude/rules/*.md` (discovered recursively; subdirectories allowed) is the official mechanism. A rule with a YAML-frontmatter `paths:` list of glob patterns (e.g. `src/**/*.{ts,tsx}`) loads **on demand, when Claude accesses a matching file**; a rule without `paths:` loads at launch unconditionally, like CLAUDE.md. `~/.claude/rules/` holds user-level rules; symlinks are supported for sharing rule sets across repos. Source: <https://code.claude.com/docs/en/memory>.

**Official recommendations.** This is the docs' modularization answer for large/dense instruction sets (<https://code.claude.com/docs/en/large-codebases>): keep the root file short, scope the rest to the paths it governs.

**Cost.** Near zero at session start for path-scoped rules. Budget limits: 1,000 expanded patterns / 4 MiB per rule's `paths` list (brace expansion counts against the pattern count). One compaction caveat from the #2 research: path-scoped rules and subdirectory CLAUDE.md are **lost on compaction** until re-triggered, while root CLAUDE.md and unscoped rules are re-injected ([#2 doc §4](claude-code-integration-surface.md)) — so a rule that must *always* hold belongs unscoped or in the root file.

**Where guidance is silent:** no official recipe for splitting an existing CLAUDE.md into rules; no stated per-rule size target (the 200-line framing is stated for memory files).

## 3. Hooks

**What it is.** Shell/HTTP/MCP-tool/prompt handlers bound to lifecycle events, configured in settings files (any scope), plugin `hooks/hooks.json`, or skill/agent frontmatter. Current core events: `SessionStart`, `SessionEnd`, `UserPromptSubmit` (can block; 30 s cap), `Stop` (can block), `StopFailure`, `PreToolUse` (can block), `PostToolUse`, `PostToolUseFailure`, `PermissionRequest` (can block), `Notification`, `PreCompact`, `InstructionsLoaded`, `MessageDisplay` (10 s cap) — plus the longer tail catalogued in the [#2 research doc §1](claude-code-integration-surface.md). Matchers: exact tool name, pipe-separated alternatives, regex, `*`. Exit 0 = success (stdout JSON parsed for decisions/context), exit 2 = blocking error (stderr fed back to Claude), exit 1 = non-blocking failure. Sources: <https://code.claude.com/docs/en/hooks>, <https://code.claude.com/docs/en/hooks-guide>.

**Official recommendations.** The docs draw a sharp line: CLAUDE.md and skills are *advisory*; hooks are *enforcement* — "actions that must happen every time with zero exceptions". Canonical repo uses named in the guide: auto-format after file edits (`PostToolUse` on Edit/Write), block dangerous commands (`PreToolUse` on Bash), lint/test on `Stop`. Source: <https://code.claude.com/docs/en/hooks-guide>.

**Cost.** Synchronous hooks block the agent loop until they return (default timeout 600 s; `UserPromptSubmit` 30 s; `SessionEnd` hooks share a 1.5 s budget unless a hook sets its own timeout) — a slow formatter on every edit is paid on every edit. Security: hooks run arbitrary shell with the user's permissions; the docs tell you to treat hook commands as code you must review, and project-scope hooks execute for everyone who trusts the repo. Output injected into context is capped at 10,000 characters ([#2 doc §1.5](claude-code-integration-surface.md)).

**Where guidance is silent:** no official catalog of recommended hooks per ecosystem (nothing .NET-specific, e.g. `dotnet format` on PostToolUse is an obvious but unofficial pattern).

## 4. Per-repo settings (`.claude/settings.json`)

**What it is.** `.claude/settings.json` is checked in and team-shared; `.claude/settings.local.json` is auto-gitignored for personal overrides. Precedence: managed policy > CLI args > local > project > user. Settings hot-reload on change. Source: <https://code.claude.com/docs/en/settings>.

**Official recommendations for per-repo keys:** `permissions` allow/deny/ask lists (deny rules **merge across scopes and cannot be un-denied** by a lower-precedence file — a committed deny is a hard team-wide rule), `hooks`, MCP enablement (`enableAllProjectMcpServers` / `enabledMcpjsonServers` / `disabledMcpjsonServers`), `model`, `attribution` (custom commit/PR trailer text), and worktree `sparsePaths`/`symlinkDirectories` for monorepos. Don't commit personal preferences, secrets, or machine-specific paths — that's what the local file is for. Permission-list curation is also a headline recommendation of the best-practices post (<https://code.claude.com/docs/en/best-practices>).

**Cost.** Essentially free at runtime (settings are configuration, not context). The cost is governance: committed deny rules and hooks bind every contributor, so they need the same review care as CI config.

## 5. Skills and slash commands as repo conventions

**What it is.** `.claude/skills/<name>/SKILL.md` with frontmatter: `name`, `description` (its keywords drive model-initiated invocation), `disable-model-invocation: true` (manual `/name` only), `paths:` globs to scope auto-loading, `context: fork` (run in an isolated subagent), `skills:` preload list. Progressive disclosure: **only name + description load at session start; the body loads on invocation.** Skills replaced the older `.claude/commands/` custom-commands mechanism — a skill invoked as `/name` *is* the slash-command convention now. Source: <https://code.claude.com/docs/en/skills>.

**Official recommendations.** Commit skills to the repo for team-shared procedures (release steps, review checklists, repo-specific workflows). Use `disable-model-invocation: true` for side-effecting workflows so they cost zero context until deliberately invoked and can't fire on a keyword match.

**Cost.** One name+description line per skill at session start; the body is paid only when used (re-injected after compaction capped at 5,000 tokens/skill, 25,000 total — [#2 doc §4](claude-code-integration-surface.md)). Maintenance: a stale skill misleads exactly when invoked.

**Where guidance is silent:** no official threshold for when a procedure belongs in a skill vs a rules file; *(inference)* the docs' frame is rules = constraints on code you touch, skills = procedures you run.

## 6. LSP servers / IDE integration

**What it is.** Two official surfaces. (a) IDE extensions for VS Code and JetBrains, which share IDE diagnostics with the CLI automatically (`mcp__ide__getDiagnostics`). Sources: <https://code.claude.com/docs/en/vs-code>, <https://code.claude.com/docs/en/jetbrains>. (b) Official **code-intelligence plugins** in the official marketplace (`/plugin install <lang>-lsp@claude-plugins-official`), which replace file-scanning with language-server symbol queries — the docs claim large context savings in big codebases. Named languages: TypeScript, Python, Go, Rust "and others". Sources: <https://code.claude.com/docs/en/large-codebases>, <https://code.claude.com/docs/en/discover-plugins#code-intelligence>.

**Cost.** Plugin install + language server process. IDE extensions are per-developer, not per-repo — nothing to commit.

**Where guidance is silent / open question:** whether a **C# / csharp-lsp** official plugin exists is unconfirmed — verify in the marketplace before the adoption decision; there is no .NET-specific integration guidance anywhere in the docs.

## 7. MCP servers

**What it is.** External tool servers. A repo declares team-shared ones in a committed `.mcp.json` at the root (project scope, per-user approval prompt on first use); local scope lives in `~/.claude.json` per project; user scope is global. Tool *names* load at session start with schemas deferred (tool search on by default), so declared servers are cheap until called. Sources: <https://code.claude.com/docs/en/mcp>, <https://code.claude.com/docs/en/mcp-quickstart>; scopes/limits detail in the [#2 research doc §2](claude-code-integration-surface.md).

**Official recommendations.** Connect a server when you'd otherwise copy data into the chat by hand — issue trackers, monitoring, databases. Security: review project-scoped servers like code; pass tokens via headers, not env. Officially referenced servers plausibly useful to a .NET 10 + Blazor + Postgres repo: **GitHub MCP** (remote HTTP) and **Playwright MCP** (browser automation — Blazor UI walking). **No official Postgres or .NET MCP server is named in the docs**; the official suggestion for a database surface is build-your-own with the MCP SDK. *(inference)* For this repo, `gh` CLI already covers the GitHub case per the best-practices post's own "provide domain CLIs" advice, and psql-via-Bash covers ad-hoc Postgres queries — MCP is situational here, not a default adopt.

**Cost.** Near-zero context (tool search); per-server process/connection; the approval-and-review burden for anything committed.

## 8. Cross-cutting best-practices synthesis

From <https://code.claude.com/docs/en/best-practices>, the Anthropic engineering post "Claude Code: Best practices for agentic coding" (<https://www.anthropic.com/engineering/claude-code-best-practices>), and the expertise research post (<https://www.anthropic.com/research/claude-code-expertise>):

- Keep CLAUDE.md under ~200 lines; modularize into rules and skills.
- Curate permission allow/deny lists so routine work doesn't prompt.
- Provide domain CLIs (`gh` etc.) — Claude is effective with tools it can run and observe.
- Hooks for enforcement, advisory text for guidance.
- Subagents for file-heavy research to keep the main context lean.
- Give Claude verification gates it can run itself (tests, linters) so it iterates autonomously; plan-first workflow for non-trivial changes.
- Research finding: per-instruction *domain expertise* in the instructions matters more than generic coding advice.

## 9. Adopt/skip framing for the decision session

| Mechanism | Official posture | Mimir-relevant note |
| --- | --- | --- |
| CLAUDE.md | Adopt; cut toward <200 lines | Currently large and dense; the official content test ("would removing this cause mistakes?") is the knife |
| `.claude/rules/` + `paths:` | Adopt — the official modularization target | Matches the maintainer's stated preference; mind the compaction caveat for must-always-hold rules |
| Hooks | Adopt for enforcement only | e.g. format/build gates; nothing .NET-specific is official |
| `.claude/settings.json` | Adopt: permissions, hooks, MCP enablement | Committed denies are un-overridable — review like CI |
| Skills | Adopt for repo procedures | Already in use here (issue tracker, triage docs) |
| IDE integration | Adopt per-developer | Nothing to commit |
| LSP plugin | Adopt **if** a C# one exists | Unconfirmed — verify in marketplace first |
| MCP | Situational | GitHub/Postgres cases already covered by `gh`/Bash; Playwright MCP is the interesting one for Blazor |
