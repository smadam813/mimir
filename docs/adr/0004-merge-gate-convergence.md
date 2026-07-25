# Recurrence emerges at the Merge Gate — no reflection pipeline

There is no batch job that reads many Episodes hunting cross-session patterns. Instead, every Wisdom candidate passes through one write-time Merge Gate: match → reinforce + rewrite (versioned); dispute → adjudicate (Supersede or Scope-split). Recurrence, cross-project generalization (promotion to Global), and contradiction handling are all merge-gate effects. Decided on [#8](https://github.com/smadam813/mimir/issues/8).

**Consequences**: the Merge Gate is the single write path to the Wisdom tier — nothing else may insert Wisdom; a future reflection pass, if ever needed, layers on top rather than replacing it.

**Extended by [#66](https://github.com/smadam813/mimir/issues/66)**: "single write path" now covers rewrites as well as inserts. The §8.1 curator edit changes a Wisdom's words, so it goes through the gate too — one keeper for re-embedding and the version chain, under the gate's advisory lock, instead of a second copy of that code racing the first. Retire, unretire and delete stay outside: they change a row's standing, not its words.
