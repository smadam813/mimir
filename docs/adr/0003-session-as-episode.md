# Two-tier memory: the session is the Episode

Memory has exactly two tiers: raw Episodes (one per Claude Code session, streamed as Events and Sealed at session end) and distilled Wisdom (atomic notes). Capture is dumb — no LLM anywhere in the path, truncated tool I/O, fail-open hooks — and all intelligence lives in distillation. Decided across charting and [#6](https://github.com/smadam813/mimir/issues/6), rejecting both "index everything" (retrieval over transcripts) and "distilled only" (no raw tier).

**Consequences**: a crashed session still keeps everything up to its last Event; distillation reads session-shaped narratives; capture volume stays low-MB per session by construction.
