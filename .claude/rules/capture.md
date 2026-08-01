---
paths:
  - "src/Mimir.Server/Capture/**"
---

# Capture: the hook surface

- One route per answer shape. The fire-and-forget hooks share `POST /api/capture/events` and answer `202`; a hook that answers with content to print gets its own route — SessionStart the Brief, UserPromptSubmit the Prompt lane's injection. A new hook that only records lands on the shared route (#136).
- Two rules of `CaptureEndpoints` that no test reaches yet, both needing a composed request pipeline rather than a call into the endpoint method. They are pre-existing coverage gaps, parked in [#140](https://github.com/smadam813/mimir/issues/140) rather than pinned by #136, so they live here until their tests land — delete the entry with the pin. SessionEnd fires the harvest (§5) and distillation (§6) triggers fire-and-forget, so sealing never waits on either. And SessionStart resumes the session's Episode and answers a fresh Brief, including the `source: "compact"` re-fire, which arrives carrying the same session id and is therefore not a special case in the code.
- `PayloadTruncator` is a hook-surface concern and nothing else. Assistant messages — the other §4 stored-in-full class beside the prompt — never arrive on that surface at all, an accepted v1 loss (§4 declines to read the transcript, ADR-0003), so the truncator grows no branch for them.
