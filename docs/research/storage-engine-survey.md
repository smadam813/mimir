# Research: fully-local storage engine survey

- **Question:** Which storage engine should back a fully-local memory store running in Docker Compose, needing vector similarity search, full-text/keyword search (hybrid search), and relational metadata (projects, provenance links, timestamps)?
- **Ticket:** [#3](https://github.com/smadam813/mimir/issues/3)
- **Date:** 2026-07-19
- **Context:** single developer machine (32 GB RAM) shared with dev workloads; fully offline; .NET client; ~100k–1M vectors of dim 384–1536 as the working scale envelope.
- **Sources:** official docs of each engine (pgvector/Postgres, Qdrant, SQLite/sqlite-vec), official Docker Hub pages, official NuGet/GitHub client repos. Every claim cites its source inline.

## TL;DR

**Recommendation: Postgres + pgvector, as a single `pgvector/pgvector` container.** It is the only candidate where all three requirements — vectors, keyword search, and relational metadata — live in one ACID engine behind one first-class .NET stack (Npgsql + official `Pgvector`/`Pgvector.EntityFrameworkCore`), and hybrid search plus provenance joins are a single SQL statement. Its honest weaknesses: keyword ranking is `ts_rank`/`ts_rank_cd`, not BM25, and hybrid fusion (RRF) is hand-rolled SQL rather than a built-in. Qdrant has the better pure vector/hybrid engine but is explicitly not relational and forces a sidecar DB plus app-level consistency across two stores. SQLite + sqlite-vec + FTS5 is the zero-ops option but its vector extension is pre-v1, brute-force-only in stable releases, and has no official .NET package.

---

## Candidate 1: Postgres + pgvector

### Search capabilities

- **Vector:** pgvector v0.8.5 supports six distance operators (L2, inner product, cosine, L1, Hamming, Jaccard) and two ANN index types — HNSW (better speed-recall, slower/more memory to build) and IVFFlat — plus exact scan by default. `vector` columns index up to 2,000 dims; `halfvec` (fp16) up to 4,000; dims 384–1536 fit comfortably. ([pgvector README](https://github.com/pgvector/pgvector))
- **Filtered vector search:** with ANN indexes, filtering applies *after* the index scan, so selective `WHERE` clauses can under-fill the `LIMIT`. Documented mitigations: iterative index scans (`hnsw.iterative_scan`) and partial indexes. Works, but needs tuning awareness. ([pgvector README](https://github.com/pgvector/pgvector))
- **Full-text:** built-in `tsvector`/`tsquery` with GIN indexes ("the preferred text search index type"), stemming and per-language configurations, and `websearch_to_tsquery` for raw user input. **Ranking is not BM25**: `ts_rank` (lexeme frequency) and `ts_rank_cd` (cover density) use "no global information" — no corpus-wide IDF. ([PostgreSQL textsearch-controls](https://www.postgresql.org/docs/current/textsearch-controls.html), [textsearch-indexes](https://www.postgresql.org/docs/current/textsearch-indexes.html))
- **Hybrid:** nothing built-in; the pgvector org publishes an official Reciprocal Rank Fusion example — two CTEs (vector top-k, FTS top-k), `FULL OUTER JOIN`, `1/(60 + rank)` scoring. The upside of DIY-in-SQL: the same query can join provenance/project metadata directly. ([pgvector RRF example](https://github.com/pgvector/pgvector-python/blob/master/examples/hybrid_search/rrf.py))
- **Relational:** it's Postgres — full SQL, FKs, joins, transactions. One transaction boundary across memories, embeddings, FTS, and provenance.

### .NET client maturity

Best of the field. Npgsql 10.0.3 (~900M NuGet downloads) with an official EF Core provider; the pgvector org ships `Pgvector` (Npgsql/Dapper), `Pgvector.Dapper`, and `Pgvector.EntityFrameworkCore` (EF Core 9/10) with LINQ distance methods (`CosineDistance` etc.) and migration-managed HNSW indexes. ([Npgsql](https://www.nuget.org/packages/Npgsql), [pgvector-dotnet](https://github.com/pgvector/pgvector-dotnet))

### Operational weight in Compose

One container: `pgvector/pgvector` (stock postgres image + prebuilt extension, ~156 MB, 100M+ pulls, tags per Postgres major). One volume, one init statement (`CREATE EXTENSION vector;`). One backup story (`pg_dump`). ([Docker Hub](https://hub.docker.com/r/pgvector/pgvector))

### Resource footprint

Postgres defaults are deliberately small (`shared_buffers` 128 MB, `work_mem` 4 MB) — modest idle footprint on a shared box, cappable via Compose memory limits. Raw vector data: 100k × 1536d ≈ 0.6 GB; 1M × 1536d ≈ 6.2 GB (halved with `halfvec`); the README notes indexes need not fit in RAM, they just perform better when they do. HNSW builds want `maintenance_work_mem` headroom. Well inside a 32 GB machine. ([runtime-config-resource](https://www.postgresql.org/docs/current/runtime-config-resource.html), [pgvector README](https://github.com/pgvector/pgvector))

---

## Candidate 2: Qdrant (+ relational sidecar)

### Search capabilities

- **Vector:** best-in-class for this list. Filtrable HNSW — filter-aware graph edges applied *during* traversal (payload indexes must exist before ingest), ACORN mode for hard filters, four distance metrics, rich quantization (scalar, binary, product, TurboQuant in v1.18 for 8–32x compression). Server v1.18.3 current. ([Indexing](https://qdrant.tech/documentation/concepts/indexing/), [Quantization](https://qdrant.tech/documentation/guides/quantization/), [Releases](https://github.com/qdrant/qdrant/releases))
- **Keyword:** the full-text payload index is a **filter/match mechanism, not ranked retrieval**. Ranked keyword search goes through sparse vectors; since v1.10 the server computes IDF, and since **v1.15.2 the local OSS server generates BM25 sparse vectors server-side** — so a .NET client gets ranked BM25 without Python. Anything fancier (SPLADE, miniCOIL, dense embeddings) is client-side, since FastEmbed is Python-only and non-BM25 server inference is Cloud-only. ([Indexing](https://qdrant.tech/documentation/concepts/indexing/), [v1.15.2 release](https://github.com/qdrant/qdrant/releases/tag/v1.15.2), [Inference](https://qdrant.tech/documentation/inference/))
- **Hybrid:** the strongest story surveyed — one Query API request runs dense + sparse prefetch with built-in server-side RRF (weighted since v1.17) or DBSF fusion, plus multi-stage re-ranking. C# examples in the official docs. ([Hybrid queries](https://qdrant.tech/documentation/concepts/hybrid-queries/))
- **Relational: none, by design.** Payloads are JSON per point — no joins, no FKs, no multi-point transactions (WAL gives per-operation durability only). Qdrant's own FAQ: it is "a vector search engine in the first place" and suggests combining it with specialized tools. Projects/provenance/timestamps therefore need a **sidecar DB** (SQLite/Postgres) keyed to point UUIDs, with app-level consistency between two stores. ([Payload](https://qdrant.tech/documentation/manage-data/payload/), [FAQ](https://qdrant.tech/documentation/faq/qdrant-fundamentals/), [Storage](https://qdrant.tech/documentation/concepts/storage/))

### .NET client maturity

Official first-party `Qdrant.Client` NuGet (gRPC-based), versioned in lockstep with the server (1.18.1 vs server 1.18.3), actively released through 2026. Docs good, thinner than Python's. ([qdrant-dotnet](https://github.com/qdrant/qdrant-dotnet), [NuGet](https://www.nuget.org/packages/Qdrant.Client))

### Operational weight in Compose

Single ~70 MB `qdrant/qdrant` container, zero mandatory config, one volume (`/qdrant/storage`), ports 6333 (REST/UI) + 6334 (gRPC). But the sidecar requirement means the *system* is two stores: Qdrant + a relational DB — two backup stories, two consistency domains. ([Installation](https://qdrant.tech/documentation/installation/), [Docker Hub](https://hub.docker.com/r/qdrant/qdrant))

### Resource footprint

Rust single binary. Official sizing: `vectors × dims × 4 bytes × 1.5`. For this envelope: 100k × 1536 ≈ 0.9 GB; 1M × 1536 ≈ 9.2 GB — fine on 32 GB, and quantization or `on_disk` memmap cuts it to ~1–2 GB (at roughly 2x latency per halving of RAM-resident vectors). ([Capacity planning](https://qdrant.tech/documentation/capacity-planning/))

---

## Candidate 3: SQLite + sqlite-vec + FTS5

### Search capabilities

- **Vector:** sqlite-vec vec0 virtual tables, KNN via `MATCH` + `k`, L2/cosine/L1, int8/binary quantization (32x storage cut, ~95% recall anecdotal). **Pre-v1** ("expect breaking changes"), stable v0.1.9; **brute-force only in stable releases** — ANN (IVF/DiskANN) exists only in v0.1.10 alphas. Author's practical limit for latency-sensitive apps: "100's of thousands" of vectors. Metadata columns, aux columns, and partition keys inside KNN queries since v0.1.6. ([sqlite-vec](https://github.com/asg017/sqlite-vec), [v0.1.0 announcement](https://alexgarcia.xyz/blog/2024/sqlite-vec-stable-release/index.html), [v0.1.6](https://github.com/asg017/sqlite-vec/releases/tag/v0.1.6))
- **Full-text:** FTS5 is mature core SQLite (since 3.9.0, 2015) with **built-in BM25 ranking as the default** — notably better-grounded keyword relevance than Postgres's `ts_rank`. ([FTS5 docs](https://www.sqlite.org/fts5.html))
- **Hybrid:** officially documented by the sqlite-vec author — pure-SQL RRF over a vec0 CTE and an FTS5 CTE with `FULL OUTER JOIN`, same k=60 pattern as pgvector's. ([Hybrid search post](https://alexgarcia.xyz/blog/2024/sqlite-vec-hybrid-search/index.html))
- **Relational:** plain SQL tables, one transaction boundary across everything. Constraint: WAL mode allows many readers but **one writer at a time, same host only** — the DB belongs to a single owning process. ([WAL docs](https://www.sqlite.org/wal.html))

### .NET client maturity

`Microsoft.Data.Sqlite` (official MS) supports `EnableExtensions`/`LoadExtension`, but **there is no official sqlite-vec NuGet** — the install docs cover Python/Node/Ruby/Rust/Go and no C#. You bundle the prebuilt `vec0.dll`/`.so` per platform yourself (no Windows arm64 build), and MS docs warn NuGet native-library resolution doesn't apply to SQLite extension loading. Workable, but DIY. ([MS extensions docs](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/extensions), [sqlite-vec install](https://alexgarcia.xyz/sqlite-vec/installation.html))

### Operational weight in Compose

Zero extra containers — in-process, one .db file on a volume. The flip side: there is no independent service; multiple containers can't sanely share the file (single-writer, same-host), so the pattern is one owning app process exposing an API. ([sqlite-vec](https://alexgarcia.xyz/sqlite-vec/), [WAL docs](https://www.sqlite.org/wal.html))

### Resource footprint

Minimal — page cache only. Author's brute-force benchmarks: 1M × 128d ≈ 33 ms, but on-disk 1M × 3072d float ≈ 8.5 s; interpolated for this envelope, ~100k vectors stay interactive, 1M at higher dims needs quantization or the alpha ANN indexes. ([benchmarks](https://alexgarcia.xyz/blog/2024/sqlite-vec-stable-release/index.html))

---

## Other candidates considered (sidebar)

- **Meilisearch** — first-class built-in hybrid search and an official .NET SDK, single light container; but a document search engine with no relational model, so it only replaces the search half. ([hybrid docs](https://www.meilisearch.com/docs/learn/ai_powered_search/getting_started_with_ai_search), [SDKs](https://www.meilisearch.com/docs/learn/resources/sdks))
- **DuckDB + VSS** — vss is explicitly experimental; HNSW persistence is behind a flag with documented index-corruption risk on unclean shutdown, and DuckDB.NET is community-tier. Disqualified for a durable store. ([vss docs](https://duckdb.org/docs/stable/core_extensions/vss))
- **Redis 8 (Query Engine)** — vector + FTS in one engine, official NRedisStack client, licensing resolved (AGPLv3 option); but memory-first storage is an awkward fit on a shared 32 GB box and metadata is JSON-not-SQL. ([search docs](https://redis.io/docs/latest/develop/ai/search-and-query/), [licenses](https://redis.io/legal/licenses/))
- **Elasticsearch/OpenSearch** — capable hybrid, but JVM heap guidance (up to 50% of node memory, as much again off-heap) and cluster ceremony are overkill for a single-user local store. ([heap guidance](https://www.elastic.co/guide/en/elasticsearch/reference/current/advanced-configuration.html))
- **Weaviate** — the most credible Qdrant alternative: native BM25F hybrid with tunable alpha, official C# client, single container; but same sidecar problem as Qdrant. Milvus eliminated by its 3-container standalone footprint (etcd + MinIO). ([hybrid](https://docs.weaviate.io/weaviate/search/hybrid), [clients](https://docs.weaviate.io/weaviate/client-libraries), [Milvus install](https://milvus.io/docs/install_standalone-docker-compose.md))

A cross-cutting observation: every dedicated search/vector engine fails on the same axis — no relational metadata story. The real contest is "one engine that does all three (Postgres, SQLite) vs. best-in-class vector engine plus a sidecar (Qdrant, runner-up Weaviate)."

---

## Comparison

| | Postgres + pgvector | Qdrant (+ sidecar) | SQLite + sqlite-vec/FTS5 |
|---|---|---|---|
| **Vector search** | HNSW/IVFFlat, 6 metrics, indexable to 2,000d (4,000 halfvec); post-scan filtering needs tuning | Best: filtrable HNSW (in-traversal filters), rich quantization | Brute-force only in stable; pre-v1; ~100k's practical ceiling |
| **Keyword search** | Mature FTS, but `ts_rank`/`ts_rank_cd` — **not BM25** (no IDF) | Server-side **BM25** sparse vectors (OSS, since v1.15.2); FT index is filter-only | **FTS5 with built-in BM25** — best keyword ranking of the three |
| **Hybrid search** | Hand-rolled RRF SQL (official example); joinable with metadata in one query | Best: one Query API call, server-side RRF/DBSF fusion | Hand-rolled RRF SQL (official author example) |
| **Relational metadata** | Full SQL, FKs, ACID — native | **None**; JSON payloads; needs sidecar DB + app-level consistency | Full SQL, one transaction boundary; single-writer, same-host |
| **.NET client** | Npgsql (~900M dl) + official Pgvector/EF Core packages — best of field | Official first-party gRPC client, lockstep releases | MS Sqlite lib is official, but **no sqlite-vec NuGet** — DIY native bundling |
| **Compose weight** | 1 container (~156 MB), 1 volume, 1 init statement | 1 container (~70 MB) **+ sidecar DB container** = 2 stores, 2 backup stories | 0 containers; but no shared service — DB tied to one process |
| **Footprint (1M × 1536d)** | ~6.2 GB data (3.1 GB halfvec); 128 MB default shared_buffers, cappable | ~9.2 GB by official formula; ~1–2 GB quantized/on-disk | Page cache only, but brute-force latency at this scale needs quantization/alpha ANN |
| **Fully offline** | Yes | Yes (BM25 local; other inference is Cloud-only) | Yes |

## Recommendation

**Postgres + pgvector.** Rationale, in order of weight:

1. **It's the only single-engine answer to all three requirements.** Memories, embeddings, keyword index, projects, provenance links, and timestamps share one ACID store — hybrid search and provenance joins are literally one SQL query. Qdrant's own FAQ pushes relational needs to a second store, which means two consistency domains and two backup stories for a system whose core value is not losing or corrupting memory.
2. **The .NET path is the most mature surveyed** — Npgsql plus official pgvector packages with EF Core/Dapper/LINQ support, versus lockstep-but-thinner (Qdrant) or DIY native binaries (sqlite-vec).
3. **Operational weight and footprint fit the constraint.** One ~156 MB container with 128 MB default shared_buffers on a shared 32 GB machine, and the scale envelope (even 1M × 1536d ≈ 6 GB) is comfortable.
4. **The trade-offs are acceptable and reversible.** Non-BM25 keyword ranking and hand-rolled RRF are the real costs; at memory-store corpus sizes (thousands to low hundreds of thousands of short documents), RRF fusion with `ts_rank_cd` is serviceable, and if keyword relevance ever proves limiting, options exist without changing engines (e.g., ParadeDB's `pg_search` BM25 extension) or Qdrant can be added later strictly as a search index while Postgres stays the source of truth.

**When to revisit:** if vector scale grows past low millions or filtered-ANN tuning becomes a recurring pain, Qdrant as a derived index over a Postgres source-of-truth is the natural evolution. If the store ever collapses into a single in-process app with no shared service requirement, SQLite + sqlite-vec/FTS5 becomes attractive once sqlite-vec ships stable ANN and a .NET package.

*Decision to be confirmed on the wayfinder map (issue #1); this document records the survey, not the decision.*
