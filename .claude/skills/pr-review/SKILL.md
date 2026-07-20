---
name: pr-review
description: Review the current diff for correctness bugs and reuse/simplification/efficiency cleanups at the given effort level (low/medium: fewer, high-confidence findings; high→max: broader coverage, may include uncertain findings). Pass --comment to post findings as inline PR comments, or --fix to apply the findings to the working tree after the review.
argument-hint: "[low|medium|high|xhigh|max] [--fix] [--comment] [<target>]"
disable-model-invocation: true
---

# pr-review

Read `$ARGUMENTS`:

- **Tier** — the first bare word matching `low`, `medium`, `high`, `xhigh`, or
  `max`. Absent one, use `high`.
- **Flags** — `--fix`, `--comment`, or both.
- **Target** — everything else: a PR number, branch, or path. Empty means the
  working diff.

Read `${CLAUDE_SKILL_DIR}/tiers/<tier>.txt` and follow it as your instructions
for this run. Read only that one — the tier bodies are alternatives, not layers.

For each flag passed, also read its file in `${CLAUDE_SKILL_DIR}/flags/` and
treat it as appended to the end of the tier body: `comment.txt` first, then
`fix.txt` when both are passed.
