# Mimir v1 — Buildable Specification

Mimir is a fully-offline, single-user memory service on one machine, giving every Claude Code session one shared brain across all projects: sessions are captured as Episodes, distilled into Wisdom by local models, and recalled automatically. This document assembles every decision from the [wayfinder map](https://github.com/smadam813/mimir/issues/1) into a spec an agent can implement without making further design decisions.

Vocabulary is normative per [`CONTEXT.md`](../../CONTEXT.md). Hard-to-reverse decisions are recorded in [`docs/adr/`](../adr/). Background research: [integration surface](../research/claude-code-integration-surface.md), [storage survey](../research/storage-engine-survey.md), [local models](../research/local-models.md).

## 1. Constraints

- **Fully offline** (ADR-0001): no cloud AI calls, ever. Distillation and embeddings run on local models.
- **Docker Compose** for all infrastructure; **.NET** application layer; **Blazor** web UI.
- **Single user, single machine, Claude Code only.** Out of scope: team sharing, multi-machine sync, other agent clients.
- **Never touch CLAUDE.md** — authored configuration is not memory. **Never write to auto-memory** — Harvest is one-way (ADR-0002).
- **Sessions never break or slow because of memory**: Capture and Recall are fail-open everywhere.
- Hardware envelope: RTX 4080 (16 GB VRAM), 32 GB RAM, Windows 11 + Docker Desktop (WSL2).

## 2. System overview

```
Windows host                          Docker Compose
┌───────────────────────┐             ┌──────────────────────────────────────┐
│ Claude Code           │             │ mimir (ASP.NET Core, 127.0.0.1:6464) │
│  ├─ hooks ──────────► │ mimir CLI ─►│  ├─ Capture module    (HTTP API)     │
│  └─ MCP (stdio) ────► │  (hook/mcp) │  ├─ Recall module     (HTTP API)     │
│                       │             │  ├─ Harvester         (hosted svc)   │
│ ~/.claude/projects ───┼── ro mount ►│  ├─ Distiller         (hosted svc)   │
└───────────────────────┘             │  └─ Blazor UI (Interactive Server)   │
                                      │ postgres (pgvector)   ollama (GPU)   │
                                      └──────────────────────────────────────┘
```

Three projects (`src/`): **Mimir.Server** (service + workers + UI), **Mimir.Cli** (host companion, single-file publish), **Mimir.Contracts** (DTOs shared by both). The Server is one modular monolith with module seams along the pipeline: Capture, Harvest, Distillation, Recall (plus the UI). No service split in v1.

**Model client layer (decided in [#4](https://github.com/smadam813/mimir/issues/4)):** all LLM/embedding access goes through **Microsoft.Extensions.AI** abstractions (`IChatClient`, `IEmbeddingGenerator<string, Embedding<float>>`) with **OllamaSharp** registered as the implementation. Semantic Kernel and raw HTTP are ruled out; OllamaSharp's native API is used (not the OpenAI-compat surface) because it supports startup model provisioning (pulling `qwen3:8b` / `qwen3-embedding:0.6b` on first run).

## 3. Storage (ADR-0005)

Postgres 17 + pgvector in one container. One ACID store for vectors, full-text, and relational metadata. Hybrid search over Wisdom = pgvector cosine KNN + `tsvector` FTS (top-50 candidates per leg), fused with Reciprocal Rank Fusion (RRF, k=60) in SQL. .NET access via Npgsql + `Pgvector.EntityFrameworkCore` (EF Core for entities; hand-written SQL for the ranking query).

**Score scales (normative):** RRF-fused scores are rank-fusion values (max ≈ 0.033) used **only for ordering**. All *thresholds* apply to the **cosine similarity of the vector leg's best match**: the Merge-Gate match threshold (0.80) and the Prompt-lane injection gate (0.75) are cosine similarities, never fused scores.

### Entities (canonical names per CONTEXT.md)

- **Project**: `id`, `identity` (normalized git remote URL, else root path), `root_paths` (text[], every root this Project has been seen at), `display_name`. Identity resolution and the clone-merge rule are in §3.1. The reserved **Global** pseudo-project holds `scope=Global` Wisdom and no Episodes.
- **Episode**: `id`, `session_id` (unique), `project_id`, `started_at`, `sealed_at?`, `seal_reason?` (hook-reported reason, or `crash-swept`), `cwd`, `distillation` (`pending | running | done | failed`), `distilled_at?`. Unsealed = live or crashed.
- **Event**: `id`, `episode_id`, `seq`, `type` (`UserPromptSubmit | PostToolUse | Stop | Remember`), `at`, `payload` (jsonb, truncated per §4), `payload_full_size`, `salient` (true only for `Remember`), `tsv` (generated column over payload text, for the Episode FTS leg of `mimir_search`). Session end is not an Event — it Seals the Episode (fields on Episode).
- **Wisdom**: `id`, `kind` (`Fact | Preference | Lesson | Procedure`), `scope` (`Global` or `project_id`), `text` (current version), `embedding` (vector(1024)), `tsv`, `reinforcement` (int), `last_confirmed_at`, `contested_at?`, `retired_at?`, `superseded_by?` (wisdom id). Retired Wisdom is excluded from all Recall and default search.
- **WisdomVersion**: `wisdom_id`, `version`, `text`, `created_at`, `cause` (`distilled | merged | adjudicated | edited`). Full chain kept forever.
- **Provenance**: `wisdom_id` → (`episode_id?`, `event_id?`, `harvested_item_id?`). Unioned on merge. Hard deletion of a referenced Event/Episode (§8.2) is the sole operation that removes Provenance rows; Wisdom whose Provenance empties survives and is flagged "orphaned provenance" in the UI.
- **HarvestedItem**: `id`, `project_id`, `path`, `content_hash`, `content`, `first_seen`, `last_changed`, `gone_at?`. Re-versioned on hash change (prior rows kept).
- **Injection**: `id`, `session_id`, `project_id`, `at`, `lane` (`Brief | Prompt | MCP`), `query_context` (the prompt text for `Prompt`; null for `Brief`; the tool query for `MCP`), `chars`, `items` (wisdom ids + scores), `verdict?` (`useful | noise`), `verdict_at?`.
- **GoldenCase**: `id`, `query_context` (prompt text), `project_id` (affinity context), `expected_wisdom_id`, `created_from_injection_id?`, `note`.

### 3.1 Project identity resolution

Identity follows the repository, not the directory ([#9](https://github.com/smadam813/mimir/issues/9)). Resolution happens **host-side in the `mimir` CLI**, which sends `project_identity` + `root_path` with every hook POST:

1. `git -C <cwd> remote get-url origin` (if no `origin`: the alphabetically first remote; if no remote or not a repo: skip to 3).
2. **Normalize** to `host/owner/repo`: strip scheme and credentials, lowercase host, convert scp-form `git@host:path` to `host/path`, strip trailing `.git` and `/`.
3. Fallback identity: the repo root (`git rev-parse --show-toplevel`), else `cwd`, as an absolute path.

The service matches a Project by identity, else by `root_paths` containing the reported root, creating one if needed and appending unseen roots. **Identity upgrade:** a path-identity Project that later reports a remote identity is upgraded in place. **Clone merge:** if the upgrade collides with an existing Project of the same identity, the two rows merge (references re-pointed to the survivor, `root_paths` unioned) — two clones are one Project.

## 4. Capture

An Episode **is** a session (ADR-0003). Capture is dumb, lossy-by-design; no LLM calls anywhere in the path.

**Hook channel.** The `mimir` CLI is registered in user-level `settings.json`:

| Hook event | Mode | Behavior |
|---|---|---|
| `PostToolUse`, `Stop`, `SessionEnd` | `async: true` | `mimir hook <event>`: stdin JSON → `POST /api/capture/events`, 3 s cap, always exit 0. |
| `SessionStart` | synchronous | `mimir hook SessionStart`: fetches the Brief (§7) and prints it to stdout. 3 s cap, exit 0 with empty output on any failure. |
| `UserPromptSubmit` | synchronous | `mimir hook UserPromptSubmit`: **one** round-trip (`POST /api/hooks/user-prompt`) that both records the prompt Event and returns any Prompt-lane injection, printed to stdout. 500 ms target, 3 s cap, exit 0 with empty output on any failure. |

UserPromptSubmit is deliberately a single synchronous registration doing capture + recall in one call: the Prompt lane must be synchronous to inject at all, and folding the capture POST into the same round-trip adds no additional blocking. Claude Code deduplicates identical hook commands, so dual registration is not an option. This bounded-synchronous prompt capture is the one refinement of [#6](https://github.com/smadam813/mimir/issues/6)'s "all capture async" — the governing premise (sessions never slow because of memory) is held by the 500 ms/3 s fail-open budget.

The service creates or resumes the Episode from `session_id` + the CLI-resolved Project (§3.1) and appends the Event. `SessionEnd` Seals the Episode with its reason.

**Fidelity.** Prompts and assistant messages stored in full. Tool inputs/outputs truncated per event to **4 KB per payload field (3 KB head + 1 KB tail, marked with `…[truncated N bytes]…`)**; original sizes recorded. No raw session dumps are stored, and v1 deliberately does not read `transcript_path` at SessionEnd — the truncation loss is accepted (ADR-0003 supersedes the transcript-consolidation clause of [#2](https://github.com/smadam813/mimir/issues/2)'s research recommendation).

**Explicit save.** The MCP tool `mimir_remember(content, kind)` appends a `Remember` Event (`salient=true`) to the current Episode (binding per §7.1).

**Crash tolerance.** A session that dies without SessionEnd leaves an unsealed Episode holding everything up to its last Event. The Distiller's sweep (§6) Seals unsealed Episodes idle > 24 h with `seal_reason=crash-swept`.

## 5. Harvest

One-way ingestion of built-in auto-memory (ADR-0002). A hosted service scans `/harvest` (read-only bind mount of the host's `~/.claude/projects/`) every **5 minutes** and opportunistically on every SessionEnd. **The first scan is the Backfill** — no special mode.

**Slug → Project mapping.** Each `<slug>/memory/**/*.md` belongs to the directory Claude Code mangled into `<slug>`; the Harvester demangles it back to an absolute path (`C--git-mimir` → `C:\git\mimir`) and resolves a Project by `root_paths` (§3.1). No match creates a path-identity Project for that root — upgraded/merged later when hook traffic reveals its remote identity.

**Item mechanics.** Per file: compute content hash; on new/changed hash store a HarvestedItem (keyed by path, prior versions kept); deletions set `gone_at` (derived Wisdom untouched).

**Candidate conversion (mechanical, no LLM).** Changed HarvestedItems become Merge-Gate candidates by splitting on markdown H1/H2 sections (a file without headings is one candidate), hard-capped at 2,000 chars per candidate. **Kind** comes from the memory-file frontmatter `type` when present (`user`→Preference, `feedback`→Lesson, `project`→Fact, `reference`→Fact), else Fact. **Scope** = the file's Project. The Merge Gate's rewrite naturally compacts oversized harvested text when it merges.

## 6. Distillation

Runs on **qwen3:8b via Ollama** (`/no_think` — non-reasoning mode; `num_ctx: 16384`), entirely off any session hot path (ADR-0004), through the §2 model-client layer.

**Trigger and bookkeeping.** Sealing an Episode sets `distillation=pending`. A single worker takes pending Episodes one at a time (`running` → `done`/`failed`); the queue **is** this DB state, surviving restarts. A sweep every **6 h** re-queues `failed`, resets `running` stale > 1 h, and crash-Seals idle unsealed Episodes (§4). A `done` Episode is **never re-distilled** — re-processing would push duplicate candidates through the Merge Gate and inflate Reinforcement.

**Distiller.** Input: the Episode's Event stream. Episodes exceeding the context window are **chunked chronologically into ~12K-token windows and distilled per chunk** — no reduce step is needed because the Merge Gate is the reduce: candidates from later chunks merge with wisdom born from earlier ones. `Remember` Events are included in every chunk they could inform (they are small and salient by definition). Output per chunk: zero or more candidates `{kind, scope (Global | this project), text ≤ 500 chars, provenance event ids}`. Prompting guidance: extract only durable, reusable lessons — not session narration; prefer no candidates over weak ones. (The Distiller's selectivity *is* v1's inferred salience; the explicit salience marker from `Remember` outranks it via the ranking boost, per the glossary.)

**Merge Gate — the single entry point to the Wisdom tier** (UI curation edits existing Wisdom in place per §8; only candidates pass the gate). For each candidate:
1. Embed (qwen3-embedding:0.6b, 1024 dims); hybrid-search existing non-Retired Wisdom.
2. **No match** (best cosine < 0.80): insert as new Wisdom (reinforcement=1, version 1).
3. **Match, agreement**: the Distiller rewrites the merged text from both; reinforcement+1; `last_confirmed_at=now`; Provenance unioned; prior text kept as a WisdomVersion (`cause=merged`). A Project-scoped Wisdom confirmed from a *different* Project is promoted to Global during this rewrite.
4. **Match, contradiction**: the Distiller adjudicates — **Supersede** (new Wisdom inserted; old gets `superseded_by` + `retired_at`) or **Scope-split** (rewritten into **one Global and one Project-scoped** Wisdom, `cause=adjudicated`). Either way the surviving Wisdom gets `contested_at=now` (cleared after 14 days), surfaced by the UI.

## 7. Recall

Three lanes; ambient lanes carry Wisdom only; everything fails open (never delay a prompt; on timeout/downtime inject nothing). **All injected content — both ambient lanes — is wrapped in provenance labels**: a header identifying it as Mimir memory (not user instructions), each Wisdom tagged kind/scope/last-confirmed.

**Ambient candidate universe:** Wisdom scoped to the session's Project, plus Global. Wisdom scoped to *other* Projects never injects ambiently — it reaches other projects via merge-gate promotion to Global, or deliberately via MCP search.

- **Brief** (SessionStart, incl. `source: "compact"` re-fire): top Wisdom by **brief_score = recency × salience × (1 + log₂(1 + reinforcement))** (no query exists at session start), filled to ≤ **4,000 chars**.
- **Prompt lane** (UserPromptSubmit, via the single §4 round-trip): embed the prompt; **gate** on best-match cosine ≥ **0.75**; order eligible results by the query formula below; inject ≤ **1,500 chars** — most prompts inject nothing. 500 ms target end-to-end; the CLI enforces the 3 s cap.
- **MCP tools** (`mimir mcp`, stdio, user scope): `mimir_search(query, {project?, kind?, since?, include_episodes = true})` — hybrid search over Wisdom **and** Episodes (the Episode leg is FTS-only over `Event.tsv` + metadata filters; Events carry no embeddings in v1); `mimir_timeline(project?, since?)` — Episode timeline; `mimir_remember(content, kind)` — §4. MCP reaches everything regardless of scope, including Retired Wisdom only when `include_retired` is passed.

**Query ranking** (Prompt lane and the Wisdom leg of `mimir_search`):
`score = RRF(vector_rank, fts_rank, k=60) × affinity × recency × salience × (1 + ln(1 + reinforcement)/10)` where affinity = **1.5** if Wisdom scope matches the session's Project (1.0 for Global); recency = `max(0.3, 0.5^(days_since_last_confirmed / 90))`; salience = **1.3** if any Provenance Event is `salient`. Retired Wisdom never ranks.

**Native-content exclusion.** Wisdom whose only Provenance is HarvestedItems of the *current* Project is excluded from ambient injection (the built-in already loads that content natively); it remains reachable via MCP.

**Injection logging.** Every actual injection is an Injection row (with `query_context` per §3); empty Prompt-lane decisions are not logged — misses enter the golden set by hand (§9).

### 7.1 MCP session binding

A stdio MCP server never receives a session id. `mimir mcp` resolves its **Project** from its own working directory (Claude Code spawns it in the project dir; `CLAUDE_PROJECT_DIR` as fallback) via §3.1. `mimir_remember` attaches to **the most recently active unsealed Episode of that Project**; with no unsealed Episode, the content goes directly to the Merge Gate as a candidate (a deliberate save is never dropped). With concurrent sessions in one repo the most-recent-active heuristic can mis-attribute provenance (not content); accepted for v1.

## 8. Web UI (Blazor Interactive Server)

Project-centric chassis: sidebar of Projects (+ Global pseudo-project) → tabs **Wisdom · Episodes · Injections**; a four-tile **health strip** pinned on top (Ollama state/models; Distillation queue depth + last run; Harvester last scan/items/changed; Storage counts/size). Four surfaces, nothing more. **Curation affordances follow Wisdom wherever it renders** — kind badges everywhere; anywhere a Wisdom appears (injection-log items, Provenance drill-downs) links to its detail with inline edit/Retire available.

1. **Wisdom browser + curation**: search; filters kind/scope/project/contested/retired; detail with text, versions, reinforcement, Provenance (drill to the underlying Events); actions: **edit** (new WisdomVersion, `cause=edited`, re-embed), **Retire/unretire**, **Delete** (confirmed; removes the Wisdom + version chain).
2. **Episode timeline + detail**: Sealed and live unsealed Episodes; drill into the Event stream; hard **Delete** (confirmed) of an Event or Episode for sensitive content — this also removes Provenance rows referencing the deleted records (§3).
3. **Injection log**: per session/lane entries with sizes and items; one-click **useful / noise** verdict per entry; "promote to golden case" action (fills GoldenCase from the entry's `query_context` + `project_id`).
4. **Health strip** as above. Adjudication review = the contested filter in surface 1. Harvest detail = the harvester tile. No settings UI.

Live updates (unsealed episodes, health, queue) push over the built-in SignalR circuit.

## 9. Evaluation

- **Marks**: the Injection verdicts (§8.3), stored on the entry (against its items + `query_context`). A verdict applies to the entry as a whole; per-Wisdom penalties are **not** derived in v1 — marks feed only the two measures below and golden promotion.
- **Golden set**: GoldenCases (grown from marked injections/misses; also hand-addable) run by a dev-time integration test: for each case, run the §7 query ranking (unthresholded, with the case's `project_id` affinity context) and assert `expected_wisdom_id` ranks in the top **5**. Reported as pass rate.
- **Measures**: injection precision (useful / marked) and golden-set pass rate. Nothing else in v1.

## 10. Retention

- **Episodes/Events: keep forever.** Growth is visible on the Storage tile. *Designated future lever (specced, not built):* event-payload compaction — drop payloads unreferenced by Provenance after a window, keep skeletons; the window becomes a config knob if and when built.
- **Wisdom**: **Retire** (reversible; excluded from recall/default search) and **Delete** (permanent; cascades the version chain). **No decay- or age-based retirement** — Supersede's automatic retirement at the Merge Gate (§6.4) and human curation are the only paths to Retired; recency decay handles staleness as a ranking effect.
- **Histories kept indefinitely:** WisdomVersions, superseded chains, HarvestedItem versions.

## 11. Configuration (appsettings / env, documented defaults)

| Knob | Default |
|---|---|
| Brief budget / prompt budget | 4000 / 1500 chars |
| Prompt-lane cosine gate | 0.75 |
| Merge-gate cosine match threshold | 0.80 |
| Boosts: affinity / salience | 1.5 / 1.3 |
| Reinforcement ranking factor | 1 + ln(1+n)/10 |
| Recency half-life / floor | 90 days / 0.3 |
| RRF k / per-leg top-N / golden-set k | 60 / 50 / 5 |
| Event payload cap | 4 KB (3 head + 1 tail) |
| Harvested candidate cap | 2000 chars |
| Harvest scan interval | 5 min |
| Distiller sweep / crash-seal idle / stale-running | 6 h / 24 h / 1 h |
| Distiller context (`num_ctx`) / chunk size | 16384 / ~12K tokens |
| Contested flag duration | 14 days |
| Models: distiller / embedding | qwen3:8b (`/no_think`) / qwen3-embedding:0.6b (1024 d) |
| Hook budgets: prompt target / hard cap | 500 ms / 3 s |
| Service port (localhost only) | 6464 |

## 12. Compose topology

Services: **mimir** (built from repo `Dockerfile`; `127.0.0.1:6464:8080`; depends_on postgres+ollama; bind mount `${USERPROFILE}/.claude/projects:/harvest:ro`), **postgres** (`pgvector/pgvector:pg17`; volume `pgdata`), **ollama** (`ollama/ollama`; `gpus: all`; volume `ollama`). Localhost is the trust boundary — no auth in v1. On startup Mimir.Server provisions the two models via OllamaSharp (§2).

## 13. Host companion (`mimir` CLI)

Self-contained single-file win-x64 executable. Verbs: `mimir hook <event>` (per the §4 table: async capture POSTs; synchronous SessionStart/UserPromptSubmit printing injection payloads; always exit 0) and `mimir mcp` (stdio MCP server per §7). The CLI performs Project identity resolution (§3.1) locally and sends it with every request. Config: `MIMIR_URL` env (default `http://127.0.0.1:6464`). Registration of hooks and the user-scope MCP server is documented in the README; automated install is out of v1.

## 14. Suggested build order

1. Compose skeleton + Mimir.Server walking skeleton (health strip real).
2. Capture path (CLI `hook` → API → Episodes/Events, identity resolution) + Episode timeline UI.
3. Harvest (+Backfill, slug mapping) → HarvestedItems.
4. Merge Gate + Distiller → Wisdom; wisdom browser UI.
5. Recall lanes (+CLI recall path, MCP server) + injection log.
6. Evaluation (marks, golden runner) + retention actions (Retire/Delete).

## 15. Decision index

Every decision's full rationale lives on its ticket: [#2](https://github.com/smadam813/mimir/issues/2) integration surface · [#3](https://github.com/smadam813/mimir/issues/3) storage · [#4](https://github.com/smadam813/mimir/issues/4) local models · [#5](https://github.com/smadam813/mimir/issues/5) harvest-not-replace · [#6](https://github.com/smadam813/mimir/issues/6) capture · [#7](https://github.com/smadam813/mimir/issues/7) recall · [#8](https://github.com/smadam813/mimir/issues/8) distillation · [#9](https://github.com/smadam813/mimir/issues/9) data model/glossary · [#10](https://github.com/smadam813/mimir/issues/10) UI scope · [#11](https://github.com/smadam813/mimir/issues/11) architecture · [#13](https://github.com/smadam813/mimir/issues/13) evaluation · [#14](https://github.com/smadam813/mimir/issues/14) retention.

Mechanism-level choices made at assembly time (filling in the tickets' decisions, flagged for review): the single synchronous UserPromptSubmit round-trip (§4), cosine-vs-RRF score scales (§3), the Brief's query-free formula (§7), the ambient candidate universe (§7), CLI-side identity resolution + clone merge (§3.1), harvest slug mapping and mechanical candidate conversion (§5), chunked distillation with merge-gate-as-reduce (§6), FTS-only Episode search (§7), and MCP session binding (§7.1).
