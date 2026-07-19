# Harvest built-in memory, never replace or edit it

Claude Code's auto-memory keeps working stock; Mimir continuously ingests it one-way (auto-memory → Mimir) and never writes back. CLAUDE.md is out of Mimir's domain entirely — authored configuration, not memory. Decided on [#5](https://github.com/smadam813/mimir/issues/5): strictly additive, no hard Docker dependency for baseline memory, and the one-directional flow is what rules out split-brain.

**Consequences**: recall must exclude content the built-in already injects (the current project's harvested items); baseline memory survives Mimir being down; there is no "suppress auto-memory" mode to maintain.
