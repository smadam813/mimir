# Research: Claude Code integration surface

Resolves [#2](https://github.com/smadam813/mimir/issues/2). What extension points Claude Code offers for Mimir's episode-capture and recall designs, and what each can and cannot do.

Investigated 2026-07-19 against the official Claude Code documentation (code.claude.com/docs). Every claim below is traceable to the source URL cited in its section. Facts marked *(inference)* are ours, not the docs'.

## Summary

Claude Code exposes three complementary integration surfaces:

1. **Hooks** — shell/HTTP/MCP-tool/prompt handlers bound to ~30 lifecycle events. They receive rich JSON payloads (including full tool inputs/outputs and a path to the session transcript), and a specific subset of events can inject text into the model's context. This is the only surface that *observes everything* and the only one that fires deterministically.
2. **MCP servers** — tools/resources/prompts the model calls at its own discretion. Best semantics for on-demand recall ("search my memory"), near-zero context cost by default thanks to tool search, but the model must *choose* to call them.
3. **Memory/config files** — CLAUDE.md hierarchy and the auto-memory directory, both loaded at session start. Auto memory is the closest built-in analog to Mimir: a per-repo directory whose `MEMORY.md` index is auto-loaded (first 200 lines / 25 KB) with topic files read on demand.

**Recommendation:** capture via async hooks (`PostToolUse`, `Stop`, `SessionEnd`), ambient recall via context-injecting hooks (`SessionStart`, `UserPromptSubmit`) under the 10,000-character injection cap, and deep recall via a stdio MCP server exposing search tools. Details and constraints below.

---

## 1. Hooks

Source: <https://code.claude.com/docs/en/hooks> (reference; see also <https://code.claude.com/docs/en/hooks-guide>).

### 1.1 Lifecycle events

Roughly 30 events exist. The ones that matter for Mimir:

| Event | Fires | Matchers |
| --- | --- | --- |
| `SessionStart` | session begins or resumes | `startup`, `resume`, `clear`, `compact` |
| `UserPromptSubmit` | user submits a prompt, before Claude processes it | none (always fires) |
| `PreToolUse` / `PostToolUse` / `PostToolUseFailure` | around every tool call | tool name, regex OK (`mcp__memory__.*`) |
| `PostToolBatch` | after a batch of parallel tool calls resolves | none |
| `Stop` / `SubagentStop` | Claude (or a subagent) finishes responding | none / agent type |
| `SubagentStart` | subagent spawned | agent type |
| `PreCompact` / `PostCompact` | around context compaction | `manual`, `auto` |
| `SessionEnd` | session terminates | `clear`, `resume`, `logout`, `prompt_input_exit`, `bypass_permissions_disabled`, `other` |

Other events (not core to Mimir but available): `Setup`, `UserPromptExpansion`, `PermissionRequest`, `PermissionDenied`, `Notification`, `MessageDisplay`, `TaskCreated`, `TaskCompleted`, `TeammateIdle`, `InstructionsLoaded` (logs which CLAUDE.md/rules files loaded and why), `ConfigChange`, `CwdChanged`, `FileChanged` (watch arbitrary paths registered via `SessionStart` `watchPaths`), `WorktreeCreate`/`WorktreeRemove`, `StopFailure`, `Elicitation`/`ElicitationResult`.

### 1.2 Payloads (JSON on stdin)

Common fields on every event: `session_id`, `prompt_id`, `transcript_path`, `cwd`, `permission_mode`, `hook_event_name`.

Per-event fields of interest:

- `SessionStart`: `source` (startup/resume/clear/compact), `model`, `agent_type`, `session_title`
- `UserPromptSubmit`: `prompt` (the raw user text)
- `PreToolUse`: `tool_name`, `tool_input` (full arguments)
- `PostToolUse` / `PostToolUseFailure`: `tool_name`, `tool_input`, **`tool_response`** (the full tool result)
- `Stop` / `SubagentStop`: **`last_assistant_message`**, `stop_hook_active`
- `PreCompact`: trigger via matcher (`manual`/`auto`)
- `SessionEnd`: `reason`

### 1.3 What hooks can observe

- **Everything that happens in the session.** Tool inputs (`PreToolUse`), tool outputs (`PostToolUse.tool_response`), user prompts (`UserPromptSubmit.prompt`), the assistant's final message per turn (`Stop.last_assistant_message`).
- **The full transcript**: `transcript_path` points at the session's conversation file (JSONL under `~/.claude/projects/...`). Docs note it "may lag" behind the live session — don't treat it as strictly real-time.
- Caveat for capture durability: Claude Code deletes session files older than `cleanupPeriodDays` (default **30 days**) at startup, and `CLAUDE_CODE_SKIP_PROMPT_HISTORY` disables transcript writes entirely (source: <https://code.claude.com/docs/en/settings>). *(inference)* Mimir must copy anything it wants to keep out of the transcript directory; transcripts are not long-term storage.

### 1.4 Injecting context — which events can, which cannot

Hooks return either plain stdout or a JSON object (exit code 0, stdout must be pure JSON). The JSON form supports `continue`, `stopReason`, `suppressOutput`, `systemMessage`, a top-level `decision: "block"` + `reason`, and event-specific `hookSpecificOutput`.

**Events that CAN inject context** (`additionalContext` in `hookSpecificOutput`, or plain stdout treated as context):

- `SessionStart` — injected before the first prompt; also supports `initialUserMessage`, `sessionTitle`, `watchPaths`, `reloadSkills`
- `UserPromptSubmit` / `UserPromptExpansion` — injected alongside the prompt
- `PreToolUse`, `PostToolUse`, `PostToolUseFailure`, `PostToolBatch` — injected next to the tool result; `PostToolUse` can also rewrite the result via `updatedToolOutput`, `PreToolUse` can rewrite arguments via `updatedInput`
- `Stop` / `SubagentStop` — injected at end of turn (conversation continues so Claude can act on it)
- `Setup`, `SubagentStart`

**Events that CANNOT inject context**: `PreCompact`, `PostCompact`, `SessionEnd`, `Notification`, `MessageDisplay`, `PermissionRequest`/`PermissionDenied`, `InstructionsLoaded`, `ConfigChange`, `FileChanged`, `TaskCreated`/`TaskCompleted`, and the worktree events. `PreCompact` can *block* compaction (exit 2) but cannot add to the summary.

**Blocking**: exit code 2 blocks the action on `PreToolUse` (deny tool), `UserPromptSubmit` (erase prompt), `Stop` (force continue), `PreCompact` (block compaction), and others. `PreToolUse` additionally supports `permissionDecision`: `allow` / `deny` / `ask` / `defer`.

### 1.5 Configuration, execution model, limits

- **Where**: `~/.claude/settings.json` (user), `.claude/settings.json` (project, committed), `.claude/settings.local.json` (gitignored), managed policy settings, plugin `hooks/hooks.json`, skill/agent frontmatter. Source: hooks reference + <https://code.claude.com/docs/en/settings>.
- **Handler types**: `command` (shell), `http` (POST of the JSON payload; needs `allowedHttpHookUrls` allowlisting), `mcp_tool` (call a tool on a configured MCP server), `prompt`, `agent`.
- **Synchronous by default** — a hook blocks the loop until it returns. `"async": true` runs it in the background without blocking; `"asyncRewake": true` additionally wakes Claude when the background hook exits 2.
- **Timeouts**: default 600 s for `command`/`http`/`mcp_tool`, 30 s for `prompt`, 60 s for `agent`; `UserPromptSubmit` hooks are capped at **30 s**, `MessageDisplay` at 10 s. Per-hook `timeout` field in seconds.
- **Output size cap**: **10,000 characters** for `additionalContext`, `systemMessage`, and plain stdout. Larger output is saved to a file and replaced with a preview + file path.
- **Parallelism**: all matching hooks for an event run in parallel; identical handlers are deduplicated (command string+args, or URL).
- Env available to the hook process: `CLAUDE_PROJECT_DIR`, plus plugin paths when applicable.

---

## 2. MCP servers

Source: <https://code.claude.com/docs/en/mcp>.

### 2.1 How capabilities surface to the model

- **Tools** are named `mcp__<server>__<tool>` (plugin-bundled: `mcp__plugin_<plugin>_<server>__<tool>`). The model calls them at its own discretion, like any built-in tool — there is no way to force a call (contrast with hooks, which are deterministic).
- **Tool search is on by default**: only tool *names* and *server instructions* load at session start; full schemas are deferred and fetched on demand via a `ToolSearch` tool. Adding servers therefore has minimal context cost. `ENABLE_TOOL_SEARCH=false` loads everything upfront; `auto` loads upfront when it fits within 10% of the context window. A server can set `alwaysLoad: true` (or per-tool `_meta["anthropic/alwaysLoad"]`) to keep its tools permanently visible — at the cost of context and a startup wait (capped at the 5 s connect timeout).
- **Server instructions and tool descriptions are truncated at 2 KB each.** With tool search on, the server instructions are what tells Claude *when* to search for your tools — write them like a skill description.
- **Resources**: referenced by the user as `@server:protocol://resource/path` @-mentions; fetched and attached to the prompt. Claude also gets list/read tools for resources automatically.
- **Prompts**: surface as slash commands `/mcp__servername__promptname`; results are injected directly into the conversation.
- **Channels**: a server declaring the `claude/channel` capability (opted in with `--channels`) can *push* messages into the session — the one MCP mechanism that doesn't wait for the model to act.

### 2.2 Configuration scopes

| Scope | Command | Stored in | Shared |
| --- | --- | --- | --- |
| **local** (default) | `claude mcp add <name> ...` | `~/.claude.json` under the project's path | no |
| **project** | `claude mcp add --scope project` | `.mcp.json` at repo root | yes, via VCS; requires per-user approval dialog (`claude mcp reset-project-choices` to reset; `enableAllProjectMcpServers` / `enabledMcpjsonServers` / `disabledMcpjsonServers` settings control it) |
| **user** | `claude mcp add --scope user` | `~/.claude.json` | all your projects |

Precedence when a name collides: local > project > user > plugin > claude.ai connectors (whole entry wins; no field merging). Transports: `http` (recommended), `sse` (deprecated), `stdio` (local process), `ws`. `.mcp.json` supports `${VAR}` / `${VAR:-default}` expansion in `command`, `args`, `env`, `url`, `headers`. Stdio servers get `CLAUDE_PROJECT_DIR` in their environment and can implement `roots/list` to learn the session's working directories.

### 2.3 Limits and performance

- **Output**: warning at 10,000 tokens; hard limit **25,000 tokens by default** (`MAX_MCP_OUTPUT_TOKENS` to raise). A tool can declare `_meta["anthropic/maxResultSizeChars"]` up to a 500,000-char ceiling; oversized results are persisted to disk and replaced with a file reference.
- **Timeouts**: `MCP_TIMEOUT` (server startup); `MCP_TOOL_TIMEOUT` (per-call wall clock, default ~28 h) or per-server `timeout` field in ms; idle timeout 5 min (HTTP/SSE/WS) / 30 min (stdio), tunable via `CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT`.
- **Backgrounding**: main-conversation MCP calls still running after 2 minutes move to a background task (v2.1.212+), so a slow recall query degrades gracefully rather than blocking.
- Dynamic updates: servers can send `list_changed` to update tools/resources/prompts mid-session; HTTP/SSE servers auto-reconnect with exponential backoff.

---

## 3. Memory and config surfaces

Source: <https://code.claude.com/docs/en/memory>.

### 3.1 CLAUDE.md

- **Hierarchy and load order** (broad → specific, all concatenated, never overridden): managed policy file (Windows: `C:\Program Files\ClaudeCode\CLAUDE.md`; macOS: `/Library/Application Support/ClaudeCode/CLAUDE.md`; Linux: `/etc/claude-code/CLAUDE.md`) → user `~/.claude/CLAUDE.md` → project `./CLAUDE.md` or `./.claude/CLAUDE.md` → `./CLAUDE.local.md`.
- **When it loads**: files in the working directory and every ancestor directory load **in full at launch**. Files in *subdirectories* load lazily, when Claude reads files in those directories.
- **How it's delivered**: as a *user message after the system prompt* — context, not enforced configuration. Block-level HTML comments are stripped before injection.
- **Imports**: `@path/to/file` anywhere in a CLAUDE.md pulls the file in at launch; recursive to a **maximum depth of 4 hops**; skipped inside code spans/fenced blocks; external (outside-repo) imports require a one-time approval dialog. Imports organize but don't save context — imported files still load at launch.
- **Rules**: `.claude/rules/*.md` (recursive) load at launch like CLAUDE.md; with `paths:` YAML frontmatter they load only when Claude reads a matching file. `~/.claude/rules/` for user-level rules. `claudeMdExcludes` (glob) skips unwanted files.
- **Size guidance**: no hard limit — CLAUDE.md loads in full regardless of length — but docs say target **under 200 lines** per file; longer files "consume more context and reduce adherence".

### 3.2 Auto memory

- On by default (`autoMemoryEnabled: false` or `CLAUDE_CODE_DISABLE_AUTO_MEMORY=1` to disable). Claude writes notes itself; the `#` shortcut and "remember this" requests also land here.
- **Location**: `~/.claude/projects/<project>/memory/`, where `<project>` is derived from the **git repository** — all worktrees and subdirectories of one repo share one memory directory. Machine-local; not synced. Relocatable via the `autoMemoryDirectory` setting (any settings scope; project-scope honored only after workspace trust).
- **Loading**: `MEMORY.md` is an index; the **first 200 lines or 25 KB, whichever comes first**, load at the start of every conversation. Everything past that threshold is silently dropped. Claude Code warns/errors on writes that approach/exceed the limit. **Topic files** (`debugging.md`, etc.) are *not* loaded at startup — Claude reads them on demand with normal file tools.
- **Subagents do not get the main session's auto memory** (except forks); a subagent can have its own memory directory via its `memory` frontmatter field.

### 3.3 Other context-injection surfaces

- `--append-system-prompt` (per-invocation; the only user-controlled *system-prompt-level* surface), output styles, skills (one-line descriptions load at startup; bodies on invocation), `/memory` and `/context` for inspection.
- `CLAUDE_CODE_ADDITIONAL_DIRECTORIES_CLAUDE_MD=1` loads memory files from `--add-dir` directories.

---

## 4. Constraints that shape the design

Sources: <https://code.claude.com/docs/en/context-window>, <https://code.claude.com/docs/en/settings>, plus sections above.

- **Context window**: 200K tokens standard; 1M available on Fable 5, Sonnet 5, Opus 4.6+, Sonnet 4.6 (`[1m]` variants).
- **What survives compaction** (auto or `/compact`): system prompt unchanged; **project-root CLAUDE.md, unscoped rules, and auto memory are re-injected from disk**; path-scoped rules and nested CLAUDE.md are lost until re-triggered; invoked skill bodies re-injected capped at 5,000 tokens/skill, 25,000 total; hooks unaffected (they're code, not context). Skill *descriptions* are not re-injected.
- **Recall injected via hooks does NOT survive compaction** *(inference from the above)* — but `SessionStart` fires with `source: "compact"` after compaction, so a recall hook can re-inject. `PreCompact`/`PostCompact` hooks exist for bookkeeping (no context injection).
- **Hook injection cap**: 10,000 characters per hook output. **`UserPromptSubmit` hooks have a 30 s timeout** — per-prompt recall must be fast. Synchronous hooks block the session; use `async: true` for capture paths.
- **MCP output cap**: 25,000 tokens default. Tool descriptions/server instructions: 2 KB.
- **Transcript lifetime**: session files deleted after `cleanupPeriodDays` (default 30 days); transcripts can be disabled entirely. Capture must persist episodes into Mimir's own store promptly.

---

## 5. Implications for Mimir

*(This section is our synthesis — design input, not doc facts.)*

**Episode capture** — hooks are the only surface that sees everything and fires deterministically:

- `PostToolUse` (async) → stream tool calls + results into the episode store without blocking the loop.
- `Stop` (async) → capture `last_assistant_message` per turn; `UserPromptSubmit` → capture the user's intent verbatim.
- `SessionEnd` / `SessionStart(source=resume|clear)` → episode boundaries; `transcript_path` gives the full JSONL for end-of-session summarization/consolidation, provided we read it before cleanup.
- A `mcp_tool`-type hook can call a Mimir MCP server tool directly, letting capture and recall share one server process.

**Recall** — three tiers with different tradeoffs:

1. **Ambient**: `SessionStart` hook injects a small "what Mimir knows about this repo/task" digest (≤10K chars; re-fires on `compact` to survive compaction).
2. **Per-prompt**: `UserPromptSubmit` hook receives the prompt text — cheap targeted retrieval keyed on it, injected alongside (must return within 30 s; realistically sub-second).
3. **On-demand deep recall**: Mimir MCP server (stdio, user scope for personal memory + optional project `.mcp.json` for team memory) exposing search/timeline tools. Near-zero context cost via tool search; write server instructions carefully (2 KB) so Claude knows when to reach for memory. MCP prompts (`/mcp__mimir__recall ...`) give users an explicit recall command for free.

**Coexistence with auto memory**: Claude Code's built-in auto memory already owns `~/.claude/projects/<project>/memory/` with a 200-line/25 KB index budget. Mimir can either (a) treat that directory as its human-readable index layer (respecting the budget, using topic files for depth), or (b) keep its own store and use hooks/MCP for injection, leaving auto memory untouched. `autoMemoryDirectory` makes the directory relocatable if Mimir wants to manage it.
