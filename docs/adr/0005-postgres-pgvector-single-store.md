# One store: Postgres + pgvector

Vectors, full-text search, and relational metadata live in a single ACID Postgres container (pgvector; hybrid search via RRF in SQL) rather than a dedicated vector database. Decided on [#3](https://github.com/smadam813/mimir/issues/3): Qdrant has the stronger vector engine but forces a relational sidecar and app-level consistency; sqlite-vec was pre-v1 with no official .NET package. Postgres was the only candidate covering all three needs in one container with a first-class .NET stack (Npgsql + Pgvector).

**Considered options**: Qdrant + relational sidecar; SQLite + sqlite-vec/FTS5. The trade-offs accepted (non-BM25 FTS ranking, hand-rolled RRF) are documented as reversible in the [storage survey](../research/storage-engine-survey.md).
