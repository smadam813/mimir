---
paths:
  - "src/Mimir.Server/Storage/**"
---

# Storage: EF migrations

EF migrations: `dotnet restore` first in a fresh worktree, then from `src/Mimir.Server`: `dotnet ef migrations add <Name> --output-dir Storage/Migrations`.

The migrations, not `MimirDbContext`, are what Postgres enforces: the model builder only decides what the *next* migration is generated from. So a schema rule is pinned by a test against a migrated database (`SchemaConstraintTests`, `ProvenanceDeletionTests`), and mutating `OnModelCreating` to check one proves nothing — mutate the migration's `onDelete:` or `unique:` instead (#137).

## The schema's shape

Tables and columns are snake_case, matching the §3 entity descriptions, because the ranking queries this schema exists for are hand-written SQL. The ticket that creates an entity builds its full §3 column set; consumers of the later columns arrive with later tickets.

Every index earns its place from a named reader, and the reader is the thing to check before changing one:

- `projects.root_paths` is GIN over `text[]`, answering "which Project has been seen at this root" (§3.1, §5).
- `harvested_items.path` serves the scanner's latest-row-per-path working set; the `converted_at IS NULL` partial index is the converter's unseen-versions working set (§5).
- `wisdom.embedding` is HNSW rather than IVFFlat, because HNSW needs no training rows and so works from the first Wisdom onward (`SchemaConstraintTests.TheEmbeddingIndex_IsHnsw_NotAMethodNeedingTrainingRows` reads the method back out of `pg_am`).
- `episodes.distillation`'s partial index and `DistillationQueue`'s claim/depth predicates are one membership rule in two languages; they agree by test (`DistillationQueueTests.TheQueuesMembershipRule_IsThePartialIndexsFilter`), and the header's third statement of it by another, so neither needs prose.
- `injections.session_id` and `injections.project_id` serve the §8.3 injection-log reads.

## The Candidate Universe is named by the method

`WisdomSearch` never lets a caller assemble a universe out of filter properties: `SearchAmbientAsync` and `ListAmbientAsync` are the ambient universe (with and without a query), and `SearchAsync` is everything, narrowed only by the filter. Internally `ambientProjectId` and `filter` are alternatives rather than a combination — every public entry passes a constant for one of them, and the SQL simply ANDs what it is given, so an ambient search told to include Retired would silently exclude it anyway. Keeping the choice between two visible call sites is what makes that unstatable rather than merely unchecked: no runtime guard can be forgotten if no caller can express the contradiction.

## Why the queryless listing is unbounded (#72)

`ListAmbientAsync` returns the whole universe, unordered and unlimited, and that is measured rather than overlooked. A LIMIT could only truncate arbitrarily, because `brief_score` is not computable in that query — it combines reinforcement, explicit salience and a recency term against the clock, all in C# after hydration — which is the crowd-out pathology #54/#57/#58 removed from the search legs, reintroduced on the queryless side. Capping honestly would mean relocating §7 scoring into SQL, and the numbers do not ask for it.

Benchmarked at a 50,000-row design ceiling (~2–10× any plausible single-user reality, given Merge Gate convergence), the whole compose path — listing, hydration, scoring, render — is flat at ~0.3 s steady state, and *indistinguishable from the same path over an empty universe*, which is the finding that matters. Quadrupling the text hydrated per row did not move it; neither did Release.

The number that is *not* ~0.3 s: the first compose in a fresh process cost 0.62–0.85 s (1.07 s against a freshly bulk-loaded table) — EF compiling the queries, the JIT, and the first physical connection. A long-lived server pays that once at startup, not once per session, and no cap would remove it; but it is close enough to `BriefTripwire`'s own one-second threshold that on a slower host the first Brief after a restart will fire the warning. That is the tripwire reporting what it measured, once per restart, not a miscalibration. `BriefTripwire` is what keeps all of this honest — it re-measures every real composition on the real machine and says so, in the Brief itself, if one exceeds a second or the set passes 25,000 rows. Growth here is monotonic by design (§10 has no age-based retirement), so the guard is the measurement, not the absence of one.

Only the "unlimited" half is pinned (`WisdomSearchAmbientTests.TheQuerylessListing_IsUnlimited_HoweverSmallTheSearchLegsCapIs`). "Unordered" stays doc-only: no test can tell a missing `ORDER BY` from one that happens to agree with heap order, so a pin would be vacuous (#137).

## What the Episode leg does server-side (doc-only)

`EventSearch` clips the payload with `left(e.payload::text, @payload_chars)` and bounds the set with `LIMIT @top_n`, both inside the one statement — a stored payload runs to tens of KB, and the point of each is the bytes never crossing the wire. Neither half is pinnable from outside: a mutant that selects the whole payload and trims after `ToListAsync`, or fetches everything and `.Take`s, produces results identical to the real thing. `EventSearchTests` therefore pins the outcome (a preview 1000 chars long; the caller's cap honoured) and says so at each site; the *where* survives only here.

## The optimistic write path

Capture writes optimistically: concurrent hooks race on unique indexes and the loser re-reads and tries again. `DbRaces` holds the shared pieces — the attempt bounds and the two `IsUniqueViolation`/`IsForeignKeyViolation` overloads, one per exception shape (EF wraps, raw SQL does not).

The FK half exists for one race only, and it is worth knowing why a foreign-key violation is ever retryable at all: the #17 clone merge hard-deletes the losing Project, and a concurrent hook can reference that loser in the window between re-pointing its rows and deleting it. The loser's rows are already re-pointed by then, so retrying the rolled-back merge whole finds the survivor. Nothing else may be retried on that signal.

## Waiting on Postgres

`StorageService` migrates in a loop, retrying until Postgres answers — that is how "mimir waits on postgres" holds even without Compose healthchecks. The retry itself is doc-only: driving it needs a fake clock, and the loop does not resume off one in this harness (#137). What is pinned is the half that matters to an operator — that it runs in the background, so the health strip is visible and reporting what it is waiting for while Postgres boots (`StorageServiceTests`).
