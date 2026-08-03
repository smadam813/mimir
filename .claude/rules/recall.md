---
paths:
  - "src/Mimir.Server/Recall/**"
  - "src/Mimir.Server/Ui/InjectionDisplay.cs"
---

# Recall: the three lanes, and what no test reaches

`RecallScoring.BriefScore` and `QueryScore` are stated a second time, in words, by `Ui.InjectionDisplay.Formula` — one is arithmetic a lane runs, the other is prose a curator reads, and rendering the second out of the first is not a thing C# can do. Each end is pinned separately (`RecallScoringTests`, `InjectionDisplayTests`); nothing pins their agreement, so change either expression's *shape* and change the other. What is already safe is the numbers in them: every factor `Formula` quotes is read off the live `RecallOptions`, so a §11 retune cannot leave the screen explaining the old one.

`InjectionLabel.Date` and `McpTexts.Date` format identically and are deliberately separate. `InjectionLabel` owns the §7 label line every lane injects; `McpTexts` owns MCP's own wording for Episode sections and timeline entries. Collapsing them would let a change to MCP prose rewrite what a Brief puts in front of a session. `QueryRanking` has no unfiltered overload for the same reason: reaching past the ambient universe is stated (`WisdomSearchFilter.None`), never defaulted, so no consumer can forget a filter that was never its to apply.

`InjectionLog` saves on the shared scoped `MimirDbContext`, which is only honest because recall stages nothing else on it by the time a lane reaches the keeper. A lane that grows staged work of its own has to move off it.

MCP route errors surface. Unlike the fail-open hook routes, the MCP lane is deliberate, and an honest error beats a silent empty answer — `McpEndpoints` deliberately catches nothing.

The Brief's growth-tripwire line is the only non-Wisdom content any recall surface volunteers (CONTEXT.md's Brief entry admits it). It exists because the ambient universe grows monotonically by design toward a §11 hook cap that degrades to an *empty* Brief, which a session cannot tell apart from Mimir having nothing to say — so the warning goes into the one surface every session reads. `BriefTripwire.ComposeWarnAfter` sits below that cap for the same reason: a warning armed at the cliff would only ever reach a Brief nobody receives.
