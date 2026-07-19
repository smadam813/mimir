# Mimir

A fully-offline memory service giving every Claude Code session on this machine one shared brain across all projects: sessions are captured as episodes, distilled into wisdom, and recalled automatically.

## Language

### Memory tiers

**Episode**:
The raw record of one Claude Code session — a stream of Events, Sealed when the session ends.
_Avoid_: session log, transcript, history

**Event**:
A single captured occurrence inside an Episode: a prompt, tool activity, an assistant message, or a deliberate save.
_Avoid_: log entry, message

**Seal**:
The closing of an Episode when its session ends. An unsealed Episode belongs to a live or crashed session and still counts.
_Avoid_: finalize, archive

**Wisdom**:
An atomic, durable, distilled note — one lesson per note, self-contained. The only tier recall ever volunteers uninvited.
_Avoid_: memory, note, insight

**Kind**:
The closed taxonomy of Wisdom: **Fact** (how the world is), **Preference** (how the user wants things done), **Lesson** (learned the hard way), **Procedure** (how to do something in this environment).
_Avoid_: category, type, tag

**Scope**:
Where a Wisdom holds: **Global** (everywhere) or scoped to one Project. The Merge Gate may promote a Project-scoped Wisdom to Global when it recurs elsewhere.
_Avoid_: visibility, namespace

**Salience**:
The importance signal on an Event or Wisdom. Explicit salience comes from a deliberate save and outranks inferred salience.
_Avoid_: priority, importance score

**Provenance**:
The links from a Wisdom back to the Episodes, Events, and Harvested Items it derives from. Merges union provenance; it is never discarded.
_Avoid_: source, citation

### Pipeline

**Capture**:
The passive, always-on recording of sessions into Episodes. Capture is dumb: no judgment, no models, never blocks a session.
_Avoid_: logging, tracking

**Harvest**:
The one-way ingestion of Claude Code's built-in auto-memory into Mimir. Mimir never writes back. The first harvest is the Backfill.
_Avoid_: import, sync, migration

**Harvested Item**:
The path-keyed, content-hashed record of one auto-memory file, re-versioned when its content changes. Enters the Merge Gate as a pre-distilled Wisdom candidate.
_Avoid_: imported memory, external memory

**Distillation**:
Turning a Sealed Episode into Wisdom candidates, performed by the Distiller off any session hot path.
_Avoid_: summarization, reflection, compression

**Merge Gate**:
The single write-time entry point to the Wisdom tier. A candidate either becomes new Wisdom, Reinforces a matching Wisdom (merged rewrite, prior text versioned), or triggers Adjudication when it disputes one.
_Avoid_: dedup step, upsert

**Reinforcement**:
The count of independent confirmations a Wisdom has received at the Merge Gate. Feeds recall ranking and refreshes recency.
_Avoid_: hit count, weight

**Adjudication**:
The Merge Gate's ruling on a contradiction: **Supersede** (old Wisdom retired to history with a superseded-by link) or **Scope-split** (both rewritten with explicit Scopes). A recently adjudicated Wisdom is **Contested**.
_Avoid_: conflict resolution, overwrite

### Recall

**Brief**:
The compact, project-aware Wisdom injection delivered at session start.
_Avoid_: context dump, preamble

**Recall**:
How memories reach a session: the Brief, per-prompt retrieval above a confidence threshold, and deliberate tool calls. Ambient recall carries Wisdom only; Episodes surface only through tools. Recall fails open — when Mimir is down, sessions proceed with nothing injected.
_Avoid_: retrieval (for the whole surface), RAG

### Structure

**Project**:
The repository a memory belongs to, identified by its normalized git remote URL — or its root path when no remote exists. Two clones of one repository are one Project.
_Avoid_: workspace, codebase, directory
