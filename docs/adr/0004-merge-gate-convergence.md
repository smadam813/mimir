# Recurrence emerges at the Merge Gate — no reflection pipeline

There is no batch job that reads many Episodes hunting cross-session patterns. Instead, every Wisdom candidate passes through one write-time Merge Gate: match → reinforce + rewrite (versioned); dispute → adjudicate (Supersede or Scope-split). Recurrence, cross-project generalization (promotion to Global), and contradiction handling are all merge-gate effects. Decided on [#8](https://github.com/smadam813/mimir/issues/8).

**Consequences**: the Merge Gate is the single write path to the Wisdom tier — nothing else may insert Wisdom; a future reflection pass, if ever needed, layers on top rather than replacing it.
