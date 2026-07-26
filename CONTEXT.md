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
The links from a Wisdom back to the Episodes, Events, and Harvested Items it derives from. Merges union provenance; the Merge Gate never discards it. What does remove a link is the disappearance of the thing it points at — see Orphaned.
_Avoid_: source, citation

**Orphaned**:
The standing of a Wisdom left with no Provenance at all: every Episode, Event and Harvested Item it derived from was deleted, and the cascade took the links with them. The words and the version chain survive; only the trail back is gone. Orphaned is a standing a Wisdom falls into, never an act performed on it, and nothing reverses it — the records it pointed at are gone.
_Avoid_: dangling, unsourced, broken provenance

**Retire**:
The reversible exclusion of a Wisdom from all recall and default search, keeping its versions and Provenance. Superseded Wisdom is Retired automatically; deletion is a separate, explicit, permanent act. Retire changes standing, not words: a Retired Wisdom's text remains editable, and an edit never unretires it.
_Avoid_: archive, soft delete, disable

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

**Distillation Queue**:
The Sealed Episodes owed distillation. The queue is state on the Episode row, not a broker: Sealing is what enqueues, a single worker claims oldest-Seal-first, and recovery is a boot re-queue of every claim a dead process left plus the sweep's reset of claims gone stale.
_Avoid_: job queue, task queue, worker pool

**Merge Gate**:
The single write-time entry point to the Wisdom tier. A candidate either becomes new Wisdom, Reinforces a matching Wisdom (merged rewrite, prior text versioned), or triggers Adjudication when it disputes one. Every rewrite of a Wisdom's words goes through the gate, a curator's edit included — retiring and deleting do not, since they change a row's standing rather than its words.
_Avoid_: dedup step, upsert

**Reinforcement**:
The count of independent confirmations a Wisdom has received at the Merge Gate. Feeds recall ranking and refreshes recency.
_Avoid_: hit count, weight

**Adjudication**:
The Merge Gate's ruling on a contradiction: **Supersede** (old Wisdom retired to history with a superseded-by link) or **Scope-split** (both rewritten with explicit Scopes). A recently adjudicated Wisdom is **Contested**.
_Avoid_: conflict resolution, overwrite

**Admission**:
The Merge Gate's processing of one candidate — new Wisdom, Reinforcement, or Adjudication. Admissions happen in gate-owned atomic batches: all of a batch's admissions, and the caller's completion marker, commit together or not at all, and batches never interleave. A batch's state belongs to the gate, so a failed batch leaves no residue in the caller that asked for it — the caller's retry is the whole of its recovery.
_Avoid_: write, insert, upsert

### Recall

**Brief**:
The compact, project-aware Wisdom injection delivered at session start. Carries Wisdom, plus the **growth tripwire**'s line when it fires — the single exception to "ambient recall carries Wisdom only".
_Avoid_: context dump, preamble

**Growth tripwire**:
The Brief's self-measurement: a composition that exceeds one second, or an ambient Candidate Universe past 25,000 rows, appends one warning line inside the Brief's own wrapper and logs a warning. The Brief is the channel because it is the one every session reads, and because the failure being watched for is silent — past the §11 hook cap the session gets an empty Brief and exit 0, which is indistinguishable from Mimir having nothing to say.
_Avoid_: health check, monitoring, alert

**Recall**:
How memories reach a session: the Brief, per-prompt retrieval above a confidence threshold, and deliberate tool calls. Ambient recall carries Wisdom only — Episodes surface only through tools, and the sole non-Wisdom exception is the Brief's **growth tripwire** line. Recall fails open — when Mimir is down, sessions proceed with nothing injected.
_Avoid_: retrieval (for the whole surface), RAG

**Injection**:
The record of one thing a lane actually recalled into a session — its lane, its query context, its size, and the Wisdom it carried. Empty decisions leave no trace, so an Injection means memory reached the session; a lane's own wording (`mimir_search` reporting no matches, the Brief's growth-tripwire line alone) is not one. Its Project is the lane's own: the session's Project for the Brief and the Prompt lane, the requester's affinity Project (or Global, when the directory matches none) for `mimir_search`, which reaches every scope and so has no single Project the answer came from. Later marked **useful** or **noise**.
_Avoid_: impression, delivery

**Candidate Universe**:
The set of Wisdom a recall surface may draw from. **Ambient** (the Brief and the Prompt lane): the session's Project plus Global, non-Retired, minus the native-content exclusion. **Everything** (`mimir_search`, the golden runner): the whole tier, optionally narrowed. The universe restricts the search itself, never a ranked result after the fact — Storage owns each universe as its own entry point, and a Recall lane names the one it wants rather than assembling it.
_Avoid_: pool, corpus, scope filter

### Structure

**Project**:
The repository a memory belongs to, identified by its normalized git remote URL — or its root path when no remote exists. Two clones of one repository are one Project.
_Avoid_: workspace, codebase, directory
