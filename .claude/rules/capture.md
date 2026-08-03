---
paths:
  - "src/Mimir.Server/Capture/**"
---

# Capture: the hook surface

- One route per answer shape. The fire-and-forget hooks share `POST /api/capture/events` and answer `202`; a hook that answers with content to print gets its own route — SessionStart the Brief, UserPromptSubmit the Prompt lane's injection. A new hook that only records lands on the shared route (#136).
- `PayloadTruncator` is a hook-surface concern and nothing else. Assistant messages — the other §4 stored-in-full class beside the prompt — never arrive on that surface at all, an accepted v1 loss (§4 declines to read the transcript, ADR-0003), so the truncator grows no branch for them.
