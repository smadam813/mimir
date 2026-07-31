# Research: inventory and classification of the repo's comment lines (#126)

Part of the wayfinder map #124. This is the sweep's worklist: every comment block in every
`.cs`/`.razor` file under `src/` and `tests/`, classified block by block.

## Method

- Inventory built with `rg -c '^\s*(///|//|@\*|\*)'` over `src/**` and `tests/**` (230 files with
  comments), then every file read in full and classified comment-block by comment-block; where a
  block's halves fall in different classes the line ranges are split.
- Class-2 pins were verified by reading the named test files, not guessed from names. Where no
  pinning assertion was found, the block is class 3.
- The failed first attempt's salvaged classifications for five files (three Capture test suites,
  `WisdomSurface.razor`, `EpisodeSurface.razor`) are incorporated verbatim; its provisional
  read of `PostgresTestBase.cs` was re-derived from scratch against `PostgresTestBaseTests.cs`
  and corrected (the reset contract is class 2, pinned by the pollution pairs — not class 3).
- Actual counted totals differ slightly from the ticket's `rg` estimate (~2,837) because
  multi-line `@* *@` interiors and trailing `//` comments count differently per counting method;
  per-file `total=` figures below are the classifiers' exact counts.

## Taxonomy

1. **C1 — noise**: restates the adjacent code; deletable outright.
2. **C2 — pinned rule**: a rule an existing test already pins; deletable per the taxonomy, with
   the pinning test named (but see the meta-point under Taxonomy gaps).
3. **C3 — unpinned rule**: rule-carrying, pinned nowhere; needs a home (test or doc) before
   deletion. Each entry names file:lines, the rule, and pinnable-by:
   `plain test` | `bUnit render test` (bUnit adoption is decided) | `doc-only`.
4. **C4 — tooling**: `<inheritdoc/>` chains, `#pragma` justifications, auto-generated markers.
   Note: no project sets `GenerateDocumentationFile`, so XML doc tags are **not**
   compiler-consumed anywhere — `///` is a human convention here, not a tooling one.

## Headline counts

237 files, 4,956 comment lines classified (the ticket's ~2,837 `rg` estimate undercounted; the
same `rg -c` run over the same 237 files sums to 4,794, and block-level counting of multi-line
`@* *@` interiors and `/* */` continuations adds the rest).

| Class | Lines | Share | Meaning |
|---|---|---|---|
| C1 noise | 292 | 5.9% | deletable outright |
| C2 pinned rule | 3,106 | 62.7% | test named per block above |
| C3 unpinned rule | 1,492 | 30.1% | needs a home before deletion — the sweep's real worklist |
| C4 tooling | 66 | 1.3% | inheritdoc, pragma justifications, generated files |

Split of C3 by proposed home (approximate, by lines): ~55% `bUnit render test` (dominated by the
Blazor surfaces — see clusters below), ~20% `plain test`, ~25% `doc-only` (rationale, measured
facts, architecture statements that belong in CONTEXT.md/ADRs or CLAUDE.md rather than any test).

Headline asymmetry: `src/` C3 concentrates in Components (the un-rendered surfaces) plus the
concurrency/race machinery (DbRaces, ProjectResolver, Debouncer); `tests/` comments are
overwhelmingly C2 (a test file's comments restate what the class itself pins) with a thin C3 band
of harness contracts and fake-behavior guarantees no assertion enforces.

## Per-file inventory

Format per file:

```
  FILE {path} total={comment lines} c1= c2= c3= c4=
  C2 {lines} | {rule} | pinned-by: {test}
  C3 {lines} | {rule} | pinnable-by: {how}
  C4 {lines} | {what}
  C1 {line refs}
```

### src/Mimir.Server/Ui

FILE src/Mimir.Server/Ui/WisdomDisplay.cs total=177 c1=11 c2=135 c3=31 c4=0
C2 18-22 | A Removed run carries the previous version's words; a row's own text is exactly its Kept+Added runs concatenated | pinned-by: WisdomDisplayTests.TheDiff_DrawsExactlyTheNewText_AndAccountsForEveryWordOfTheOld
C2 33-37 | The pending draft row has At=null, Pending=true, drawn at the head of the chain | pinned-by: WisdomDisplayTests.AnUnsavedDraft_HeadsTheChain_AsTheVersionItWouldBecome
C2 63-67 | The Reinforcement bar is a fixed row of segments so it cannot outgrow its width | pinned-by: WisdomDisplayTests.TheReinforcementBar_FillsOneSegmentPerConfirmation_AndStopsAtItsWidth
C2 70-73 | Fill is clamped at both ends (0..segments) | pinned-by: WisdomDisplayTests.TheReinforcementBar_FillsOneSegmentPerConfirmation_AndStopsAtItsWidth
C2 77-85 | Save-disable wording delegates to MergeGate.NoOpOf (no second statement of the no-op set); the third no-op stays unworded and Save stays enabled | pinned-by: WisdomDisplayTests.SavingIsPointless_OnBlankTextAndOnTextThatAlreadySaysThis
C2 94-100 | EditExplanation states the gate's mechanics verbatim (version, cause, Reinforcement/recency untouched) | pinned-by: WisdomDisplayTests.TheEditorExplains_WhatTheGateWillDo_InTheGatesOwnTerms
C2 107-112 | CharacterCount counts what a session would receive, N0-formatted with singular at 1 | pinned-by: WisdomDisplayTests.TheEditorCountsWhatASessionWouldReceive_AndTheAsideNamesItsUnit
C2 120-123 | Merged is the one badge remapping: it reads "reinforced" | pinned-by: WisdomDisplayTests.TheCauseBadge_ReadsMergedAsReinforced_AndEveryOtherCauseAsItself
C2 126-127 | Every other cause reads as its own lowercased name, so a new §3 cause names itself | pinned-by: WisdomDisplayTests.TheCauseBadge_ReadsMergedAsReinforced_AndEveryOtherCauseAsItself
C2 134-140 | The legend is built from the enum in enum order — a new cause cannot be silently missing | pinned-by: WisdomDisplayTests.TheLegend_DefinesEveryCauseTheDomainHas
C2 158-164 | Past DiffWordBound the diff answers wholesale instead of word-by-word (quadratic-walk guard) | pinned-by: WisdomDisplayTests.TheDiff_PastItsWordBound_SaysTheWholeTextChanged
C2 167-179 | Null previous reads plain; removed runs precede added at every divergence; kept runs come from current so the row is exactly this version's text | pinned-by: WisdomDisplayTests.TheFirstVersion_HasNothingToDifferFrom_AndReadsPlain, TheDiff_MarksTheWordsThatWent_AndThenTheWordsThatArrived, TheDiff_DrawsExactlyTheNewText_AndAccountsForEveryWordOfTheOld
C2 191-193 | The wholesale (past-bound) pair still goes through SeparateRemovedRuns | pinned-by: WisdomDisplayTests.TheDiff_PastItsWordBound_SaysTheWholeTextChanged
C2 250-257 | A struck run at the edge of the row's text gets its separator on the removed run itself, never on this version's verbatim text | pinned-by: WisdomDisplayTests.TheDiff_KeepsAStruckRunClearOfTheWordsBesideIt
C2 286-292 | Chain orders newest-first itself (not the caller's ORDER BY) and diffs each row against the one below | pinned-by: WisdomDisplayTests.TheChain_ReadsNewestFirst_AndDiffsEachVersionAgainstTheOneBelowIt
C2 309-311 | The draft heads the chain as the version it would become | pinned-by: WisdomDisplayTests.AnUnsavedDraft_HeadsTheChain_AsTheVersionItWouldBecome
C2 315-319 | Whether the draft is a version is MergeGate.NoOpOf's one statement, shared with the Save button | pinned-by: WisdomDisplayTests.ADraftThatWouldSaveNothing_IsNotAVersion
C2 320-324 | The pending row is measured against the current text handed in, not the head version's text | pinned-by: WisdomDisplayTests.AnUnsavedDraft_IsMeasuredAgainstWhatTheWisdomSays_NotAgainstItsHeadVersion
C2 325-328 | The pending row carries the trimmed draft, because trimmed is what the gate would write | pinned-by: WisdomDisplayTests.AnUnsavedDraft_HeadsTheChain_AsTheVersionItWouldBecome
C2 351-353 | A word carries its following whitespace so runs reassemble into the text exactly | pinned-by: WisdomDisplayTests.TheDiff_DrawsExactlyTheNewText_AndAccountsForEveryWordOfTheOld
C2 378-380 | Adjacent same-change words merge into one run (an unchanged sentence is one plain run) | pinned-by: WisdomDisplayTests.TheDiff_MarksTheWordsThatWent_AndThenTheWordsThatArrived
C2 393 | Reinforcement unit is singular at exactly one confirmation | pinned-by: WisdomDisplayTests.TheEditorCountsWhatASessionWouldReceive_AndTheAsideNamesItsUnit
C2 397-401 | RecallNote says marks judge the injection entry, not the line | pinned-by: WisdomDisplayTests.TheRecallNote_CountsTheUnjudgedEntries_AndSaysWhatAMarkIsLeftOn
C2 411-414 | RetireHint wording: reversible / Unretire, standing not words | pinned-by: WisdomDisplayTests.TheEditorCountsWhatASessionWouldReceive_AndTheAsideNamesItsUnit
C2 420-423 | ProvenanceNote renders only when an Episode-backed link exists | pinned-by: WisdomDisplayTests.TheProvenanceNote_PromisesTheEpisodeOnlyWhereThereIsOne
C2 429-433 | A link is titled by the moment (or harvest source), never the session id | pinned-by: WisdomDisplayTests.AnEventProvenance_IsNamedByTheMomentItself_NotBySessionId, AHarvestProvenance_IsNamedByWhereItWasHarvestedFrom
C2 439-442 | The all-null arm is unreachable by construction but the renderer must never die on it | pinned-by: WisdomDisplayTests.AProvenanceLinkingToNothing_StillReadsAsSomething
C2 446-447 | The detail line reads: what happened, where the session ran, which Event | pinned-by: WisdomDisplayTests.AnEventProvenance_IsNamedByTheMomentItself_NotBySessionId
C2 466 | A candidate that named no Events links to the session as a whole | pinned-by: WisdomDisplayTests.AnEpisodeProvenance_IsNamedByWhenTheSessionStarted
C2 470 | The unreachable shape still reads as words | pinned-by: WisdomDisplayTests.AProvenanceLinkingToNothing_StillReadsAsSomething
C3 6-11 | The version-chain screen opens on the Changed (diff) view; Full is the way out | pinnable-by: bUnit render test
C3 55-60 | Surface rules must be pinned in this pure class — nothing renders components, so @code wording is unholdable | pinnable-by: doc-only (obsolete once bUnit lands)
C3 124-125 | The cause is persisted as a string, so renaming a WisdomVersionCause member is a data migration | pinnable-by: doc-only
C3 153-154 | A cause added to §3 without a meaning here reads the honest fallback sentence, never a wrong one | pinnable-by: doc-only (untestable without a fifth enum member)
C3 203-205 | A whitespace-only rewrap must not read as a rewording (match trims, drawn run keeps whitespace) | pinnable-by: plain test
C3 293-296 | Chain is a function of the Wisdom alone — a keystroke burst must not re-diff it | pinnable-by: doc-only
C3 312-314 | Pending-row diffing is split from Chain so one call doesn't re-diff the whole chain per character | pinnable-by: doc-only (same rule as 293-296)
C3 332-333 | An empty chain never grows a pending row | pinnable-by: plain test
C3 448-450 | A harvested path is appended alongside an Episode link, not switched on against it | pinnable-by: plain test
C1 30, 46, 49-54, 116, 209-210

FILE src/Mimir.Server/Ui/WisdomBrowser.cs total=119 c1=11 c2=83 c3=22 c4=3
C2 9-14 | The four lenses' semantics: Active excludes Retired by default; Contested/Orphaned/Retired each list their state | pinned-by: WisdomBrowserTests.TheDefaultListing_ShowsActiveWisdomNewestFirst_AndExcludesRetired + the three lens tests
C2 23-26 | ProjectId names the Ambient Candidate Universe (own Wisdom plus Global), what a session there recalls | pinned-by: WisdomBrowserTests.SelectingAProject_ListsItsOwnWisdom_AndNoOtherProjects, SelectingAProject_AlsoListsGlobal_TheSetASessionThereRecalls
C2 39-47 | ProjectOwned+Global partition Entries exactly; Kinds count before the Kind filter, every Kind in enum order | pinned-by: WisdomBrowserTests.TheHeaderCounts_PartitionTheListIntoProjectOwnedAndGlobal, TheKindFilter_NarrowsTheList_ButLeavesEveryChipCounting, TheKindChips_CountTheWholeUniverse_EveryKindInEnumOrder
C2 71-77 | Display fields are non-null wherever the matching id is (cascades remove rows rather than blanking them) | pinned-by: WisdomBrowserTests.TheProvenanceDrillDown_CarriesTheMomentAndTheWorkingDirectory
C2 91-102 | Recall figures are whole-install, whole-history; marks count entries not verdicts; every lane in enum order, zeros included | pinned-by: WisdomBrowserTests.TheDetail_CountsEveryLaneThatRecalledIt_AcrossEveryProject, TheDetail_CountsTheMarksLeftOnTheEntriesThatCarriedIt, TheDetail_OfNeverRecalledWisdom_StillNamesEveryLane
C2 125-130 | FirstVersionAt is the foot of the chain, a different date from LastConfirmedAt | pinned-by: WisdomBrowserTests.TheDetail_ReadsBothEndsOfTheChain_OffItsRowsRatherThanItsLength
C2 133-138 | CurrentVersion is read off the head row, never counted from length (gate numbers MAX+1) | pinned-by: WisdomBrowserTests.TheDetail_ReadsBothEndsOfTheChain_OffItsRowsRatherThanItsLength
C2 145-148 | Retire/delete change standing never words; edit goes through the Merge Gate which owns re-embed, chain, lock | pinned-by: WisdomBrowserTests.Retiring_IsReversible_AndTimestamped, Editing_AppendsAnEditedVersion_AndReEmbedsTheNewText
C2 173 | Chips count before the Kind filter so clicking one never rewrites the others | pinned-by: WisdomBrowserTests.TheKindFilter_NarrowsTheList_ButLeavesEveryChipCounting
C2 193-194 | Search is word-aware FTS with a substring fallback | pinned-by: WisdomBrowserTests.Search_MatchesWordsAndSubstrings_ButNeverRetiredByDefault
C2 216 | The drill-down resolves each link to human-recognizable words | pinned-by: WisdomBrowserTests.TheProvenanceDrillDown_CarriesTheMomentAndTheWorkingDirectory
C2 267-272 | Recall aggregated by lane and mark in one read, deliberately unscoped by Project | pinned-by: WisdomBrowserTests.TheDetail_CountsEveryLaneThatRecalledIt_AcrossEveryProject
C2 308-321 | Edit via gate: cause=edited version, re-embed, Reinforcement/recency untouched; unchanged is a no-op, Retired is not; EditExplanation is the held restatement | pinned-by: WisdomBrowserTests.Editing_AppendsAnEditedVersion_AndReEmbedsTheNewText, Editing_WithoutChangingTheText_AddsNoVersion, MergeGateTests.AnEdit_RewordsARetiredWisdom_AndLeavesItRetired, WisdomDisplayTests.TheEditorExplains_WhatTheGateWillDo_InTheGatesOwnTerms
C2 325 | Retire is reversible and out of recall/default search from this moment | pinned-by: WisdomBrowserTests.Retiring_IsReversible_AndTimestamped, TheDefaultListing_ShowsActiveWisdomNewestFirst_AndExcludesRetired
C2 342-345 | Delete is permanent; the schema cascades chain and Provenance (the "confirmation is the UI's job" clause is itself only bUnit-pinnable) | pinned-by: WisdomBrowserTests.Deleting_RemovesTheWisdomWithItsChain_ReferencedRecordsSurvive
C3 8 | The sidebar's "Needs attention" group renders these four lenses, one link each | pinnable-by: bUnit render test
C3 27-29 | No scope-filter control may be added — the sidebar selection already names the universe | pinnable-by: doc-only
C3 142-144 | Every read opens its own short-lived context | pinnable-by: doc-only
C3 161-166 | Header figures and chips must come off the one listing read, so a concurrent Admission cannot make them disagree with the list | pinnable-by: doc-only (snapshot race unforceable without an interceptor)
C3 217 | An Event-only Provenance row backfills its EpisodeId from the Event's own Episode | pinnable-by: plain test
C3 242-244 | The Episode is projected once, not one correlated subquery per column | pinnable-by: doc-only
C3 289-293 | Every surface reads Wisdom through ToEntries so curation affordances follow it everywhere | pinnable-by: doc-only
C4 149-151 | internal-visibility justification: internal MergeGate makes the class internal (CS0051, build-enforced)
C1 36, 54-57, 108, 111, 115-118

FILE src/Mimir.Server/Ui/EpisodeDisplay.cs total=104 c1=2 c2=95 c3=7 c4=0
C2 5-8 | Done is the resting state the list marks with nothing | pinned-by: EpisodeDisplayTests.OnlyTheRestingState_GoesUnmarked
C2 20-22 | List rows and the drill-down must describe one Episode the same way | pinned-by: EpisodeDisplayTests.ARowAndTheDrillDown_WordOneSealTheSameWay, ASealWithoutAReason_IsWordedTheSameWayEverywhere
C2 27-32 | The stream bound withholds past 50 and the control names what is withheld | pinned-by: EpisodeDisplayTests.ABoundedStream_SaysHowManyItIsShowingOfHowMany, OneEventPastTheBound_IsStillBounded
C2 38 | Unsealed means live; a Seal always shows its reason | pinned-by: EpisodeDisplayTests.ASealWithNoRecordedReason_SaysSo_RatherThanReadingUnsealed
C2 42-46 | A missing Seal reason reads "no reason" from the one shared phrase, never unsealed | pinned-by: EpisodeDisplayTests.ASealWithoutAReason_IsWordedTheSameWayEverywhere
C2 49-57 | Duration is null while unsealed, reads in the largest unit incl. days, clamps at zero against clock skew | pinned-by: EpisodeDisplayTests.AnUnsealedEpisode_HasNoDurationToState, ADuration_ReadsInTheLargestUnitItFills, ASealStampedBeforeItsStart_ReadsAsNoTime_RatherThanNegative
C2 77-80 | A configured interval reads in whole hours | pinned-by: EpisodeDisplayTests.AConfiguredInterval_ReadsInWholeHours
C2 83-90 | An unsealed Episode reads "not queued", never its Distillation column | pinned-by: EpisodeDisplayTests.AnUnsealedEpisode_IsNotInTheQueueAtAll_WhateverItsColumnSays, ASealedEpisode_NamesItsDistillationColumn
C2 96-103 | The nothing-produced note routes through State: Failed says attempted-and-failed, only Done settles the emptiness | pinned-by: EpisodeDisplayTests.AFailedDistillation_SaysItWasAttempted_NotThatItNeverRan, ADoneEpisodeThatProducedNothing_SaysTheEmptinessIsSettled, AnUnsealedEpisode_IsNotDescribedByItsColumn_HereEither
C2 119-123 | The bound note names both figures, null when nothing is withheld | pinned-by: EpisodeDisplayTests.AStreamInsideTheBound_IsWholeAndSaysNothingAboutIt, ABoundedStream_SaysHowManyItIsShowingOfHowMany
C2 129-132 | The expand toggle is reversible and null when the stream is whole | pinned-by: EpisodeDisplayTests.AnExpandedStream_SaysItIsWhole_AndOffersTheBoundBack
C2 141-147 | The anchor's writer, DOM id and reader form one round trip spelled in one place | pinned-by: EpisodeDisplayTests.TheAnchorTheLinkWrites_IsTheOneTheStreamCarries_AndTheReaderOpens
C2 153-157 | AnchoredEvent parses only the #event-{guid} fragment this surface writes | pinned-by: EpisodeDisplayTests.AProvenanceLink_AnchorsTheEventItNames, AnUrlWithNoEventAnchor_AnchorsNothing
C2 173-178 | An anchor the bound would withhold forces the stream open whole | pinned-by: EpisodeDisplayTests.AnAnchorTheBoundWouldWithhold_OpensTheStreamWhole, AnAnchorInsideTheBound_LeavesTheStreamAsItIs
C2 183-186 | An unsealed Episode is Live whatever its Distillation column says | pinned-by: EpisodeDisplayTests.AnUnsealedEpisode_IsLive_WhateverItsDistillationColumnSays
C2 198 | Done carries no state word | pinned-by: EpisodeDisplayTests.OnlyTheRestingState_GoesUnmarked
C2 208-216 | MetaLine composition: Failed names the re-queue, quiet-Done says "no Wisdom", pre-Done states no figure, live says only its cwd | pinned-by: EpisodeDisplayTests.AFailedEpisode_NamesTheSweepsRecovery_SoFailedDoesNotReadTerminal, ADistilledEpisodeThatProducedNothing_SaysSoInWords, AnEpisodeStillOwedDistillation_ClaimsNeither, ALiveEpisode_SaysOnlyWhereItRuns_LeavingLiveToTheRowsOwnMark
C2 221-223 | The Seal is worded by StateLabel; its live half is deliberately unused in MetaLine | pinned-by: EpisodeDisplayTests.ARowAndTheDrillDown_WordOneSealTheSameWay, ALiveEpisode_SaysOnlyWhereItRuns_LeavingLiveToTheRowsOwnMark
C2 248-250 | Events are named in words, never the hook's own name | pinned-by: WisdomDisplayTests.AnEventProvenance_IsNamedByTheMomentItself_NotBySessionId
C3 9-10 | The state chips filter by the four non-Done states | pinnable-by: bUnit render test
C3 23-24 | Pure by construction — no database reaches this class, so its pins run without Postgres | pinnable-by: doc-only
C3 251-253 | All four EventWord mappings are CONTEXT.md's exact words (only the PostToolUse mapping is asserted anywhere) | pinnable-by: plain test
C1 138, 150

FILE src/Mimir.Server/Ui/EpisodeBrowser.cs total=60 c1=2 c2=46 c3=12 c4=0
C2 8-16 | WisdomCount = distinct still-standing Wisdom this Episode is Provenance for; Retired excluded like every chassis Wisdom figure | pinned-by: EpisodeBrowserTests.ASummary_CountsTheWisdomTheEpisodeProduced, WisdomDrawnFromSeveralOfOneEpisodesEvents_CountsOnce, RetiredWisdom_StopsCountingTowardsTheEpisodeThatProducedIt
C2 37-41 | EpisodeWisdom carries what the §8.1 rows carry (kind, text, scope, reinforcement) | pinned-by: EpisodeBrowserTests.TheDrillDown_NamesTheWisdomThisEpisodeProduced_NewestConfirmationFirst
C2 44-51 | Produced counts the same way as the list row's WisdomCount, so drill-down and row never disagree | pinned-by: EpisodeBrowserTests.WisdomDrawnFromSeveralOfOneEpisodesEvents_IsOneLine, RetiredWisdom_StopsBeingSomethingTheEpisodeProduced
C2 55-60 | PromptCount is exactly the UserPromptSubmit Events (§3: start/end are not Events) | pinned-by: EpisodeBrowserTests.TheDrillDown_CountsPromptsAlone_NotEveryEvent
C2 67-68 | Hard deletes are announced on the feed so open lists drop the rows | pinned-by: EpisodeBrowserTests.DeletingAnEvent_RemovesItAlone_AndAnnouncesTheChange, DeletingAnEpisode_TakesItsEventsWithIt_AndAnnouncesTheChange, DeletingWhatIsAlreadyGone_StaysQuiet
C2 73-76 | Search is word-aware over the Event payload's tsv; null/blank lists every Episode | pinned-by: EpisodeBrowserTests.Searching_IsWordAware_NotSubstring, Searching_KeepsOnlyTheEpisodesWhoseEventsMatch, AnEpisodeWithNoEvents_IsSearchedAway_ButListedWhenNothingIsTyped
C2 102-106 | The count is over distinct WisdomId (one Provenance row per Event) with Retired excluded | pinned-by: EpisodeBrowserTests.WisdomDrawnFromSeveralOfOneEpisodesEvents_CountsOnce, RetiredWisdom_StopsCountingTowardsTheEpisodeThatProducedIt
C2 131-135 | Existence over a join gives one line per Wisdom, Retired excluded, newest confirmation first | pinned-by: EpisodeBrowserTests.WisdomDrawnFromSeveralOfOneEpisodesEvents_IsOneLine, RetiredWisdom_StopsBeingSomethingTheEpisodeProduced, TheDrillDown_NamesTheWisdomThisEpisodeProduced_NewestConfirmationFirst
C2 152 | Event hard delete removes that one Event alone | pinned-by: EpisodeBrowserTests.DeletingAnEvent_RemovesItAlone_AndAnnouncesTheChange
C2 174 | Episode hard delete takes every Event with it via the FK cascade | pinned-by: EpisodeBrowserTests.DeletingAnEpisode_TakesItsEventsWithIt_AndAnnouncesTheChange
C3 28-33 | The list's four readers derive state through EpisodeSummary.State; the rule itself stays EpisodeDisplay.State's | pinnable-by: doc-only
C3 64-66 | Every method opens its own short-lived context because a Blazor circuit outlives any DbContext lifetime | pinnable-by: doc-only
C3 77-79 | Episode metadata (cwd, session id) is deliberately excluded from search | pinnable-by: plain test
C1 69-70

FILE src/Mimir.Server/Ui/Debouncer.cs total=105 c1=1 c2=47 c3=57 c4=0
C2 5-31 | Trailing-edge debounce: burst collapses to last signal; superseded run cancelled silently, failures logged, teardown-safe; ceiling behavior with unchanged trailing edge | pinned-by: DebouncerTests (ABurst_RunsOnceWithTheLastSignalsWork_NotOncePerSignal, ASupersededRun_IsNotReportedAsAFailure, AFailingAction_IsReported_NotLeftForNobodyToObserve, ASignalRacingDispose_IsRefusedRatherThanArmingATimerNobodyCancels, ABurstPastTheCeiling_RunsDuringIt_NotOnlyOnceItGoesQuiet)
C2 79-84 | A ceiling multiple of zero or less is refused at construction | pinned-by: DebouncerTests.ACeilingOfZeroOrLess_IsRefused_RatherThanTurningTheDebounceOff
C2 96-100 | Schedule supersedes the pending run and is a no-op once disposed; returns without awaiting | pinned-by: DebouncerTests.ABurst_RunsOnceWithTheLastSignalsWork_NotOncePerSignal + DisposingMidBurst_RunsNothingThatWasStillWaitingOutItsDelay
C2 138-139 | A signal waits a full delay or the ceiling's remainder, whichever is shorter | pinned-by: DebouncerTests.ABurstPastTheCeiling_RunsDuringIt + ThatSameBurst_WithNoCeilingConfigured_RunsNothingUntilItGoesQuiet
C2 171-172 | The burst clock resets after a served burst so the next burst's ceiling is measured fresh | pinned-by: DebouncerTests.ABurstPastTheCeiling_RunsDuringIt (the 2–4 range bounds both a stuck and a never-restarting clock)
C2 190 | Cancellation is the ordinary superseded/disposed case, not a fault | pinned-by: DebouncerTests.ASupersededRun_IsNotReportedAsAFailure
C2 198-201 | Dispose drops the pending run and refuses a racing Schedule | pinned-by: DebouncerTests.DisposingMidBurst + ASignalRacingDispose_IsRefused
C3 32-33 | Ceiling is a multiple of delay, not an absolute duration, so test-shrunk delays shrink it too | pinnable-by: doc-only
C3 35-38 | Feed-driven callers must pass DefaultCeilingMultiple; search-driven callers must pass none (mid-word fire) | pinnable-by: bUnit render test
C3 42-45 | Every live surface must debounce on this one shared constant, never a per-component copy | pinnable-by: bUnit render test (or doc-only)
C3 48-61 | DefaultCeilingMultiple=4 rests on unfiltered-feed density; do not lower it on single-session measurements | pinnable-by: doc-only
C3 71-74 | The burst clock is read/written only under _gate (feed publishes are concurrent) | pinnable-by: doc-only
C3 101-110 | The token must be read inside the gate, never off the source after release (ObjectDisposedException race; explicitly untestable, #112) | pinnable-by: doc-only
C3 131-134 | RunAsync may compare the superseded source by reference but never dereference it | pinnable-by: doc-only
C3 140-143 | NextWait must be called under _gate — the burst clock lives there | pinnable-by: doc-only
C3 173-177 | A superseding signal's burst clock is not the elapsed run's to clear; callers must carry their own generation check against the extra run | pinnable-by: bUnit render test / doc-only
C3 202-206 | Dispose never interrupts a run whose delay elapsed; a torn-down surface sees its last refresh finish | pinnable-by: plain test
C1 66

FILE src/Mimir.Server/Ui/InjectionBrowser.cs total=102 c1=5 c2=82 c3=15 c4=0
C2 10-14 | A hard-deleted Wisdom leaves a null card but the item stays visible | pinned-by: InjectionBrowserTests.AHardDeletedWisdom_LeavesItsItemVisible_WithoutACard
C2 19-22 | Salient is computed from the live §7 definition, not a stored as-of flag | pinned-by: InjectionBrowserTests.Items_CarryTheSalienceBoostTheirScoreTookFromSection7
C2 38-42 | CanPromote needs a query_context and at least one live (unretired, undeleted) Wisdom | pinned-by: InjectionBrowserTests.ABriefEntry_CannotPromote + AnEntryWithNoLiveWisdomLeft_CannotPromote
C2 46-50 | WisdomSinceDeleted counts items whose Wisdom is gone | pinned-by: InjectionBrowserTests.AnEntry_CountsTheWisdomHardDeletedSinceItCarriedThem
C2 54 | Entries are newest-first within a session | pinned-by: InjectionBrowserTests.TheListing_GroupsPerSessionNewestFirst_AndCarriesTheEntrysShape
C2 60-64 | A deleted Wisdom keeps its recall count with a null card | pinned-by: InjectionBrowserTests.MostRecalled_KeepsAWisdomDeletedSince
C2 67-70 | Search narrows on query_context only, so it never matches a Brief | pinned-by: InjectionBrowserTests.TheSearch_NarrowsOnQueryContext_AndNeverMatchesABriefWhichHasNone
C2 73-80 | Aside figures are whole-Project; only Sessions and Matching answer the filters | pinned-by: InjectionBrowserTests.TheSearch_NarrowsOnQueryContext + TheAside_CountsMarkedNoiseUnmarkedAndSessions
C2 92, 95, 98 | Precision = useful/marked (null until marked); Noise = marked−useful; Unmarked = total−marked, whole history | pinned-by: InjectionBrowserTests.Precision_IsUsefulOverMarked + PrecisionIsNull_UntilAnythingIsMarked + TheAside_CountsMarkedNoiseUnmarkedAndSessions_OverTheWholeProject
C2 104-108 | Truncated is measured against Matching, not TotalEntries | pinned-by: InjectionBrowserTests.AFilteredListing_IsNotTruncated_TheBoundIsMeasuredAgainstWhatMatched
C2 120-125 | The listing bound must not move the §9 precision inputs (whole-history) | pinned-by: InjectionBrowserTests.TheListing_BoundsToTheMostRecentEntries_PrecisionCountsThemAll
C2 139-142 | scoped feeds the aside untouched by filters; matching alone takes them | pinned-by: InjectionBrowserTests.TheSearch_NarrowsOnQueryContext + TheListing_BoundsToTheMostRecentEntries
C2 154-156 | Case-insensitive substring match with LIKE metacharacters escaped | pinned-by: InjectionBrowserTests.TheSearch_TakesAPercentSignLiterally_RatherThanAsAWildcard
C2 170-173 | Matching is derived when the Take came back short or the listing is unnarrowed; only a narrowed full bound queries | pinned-by: InjectionBrowserTests.AFilteredListingThatFillsTheBound_CountsWhatMatched_NotTheWholeProject
C2 190-191 | Every lane keeps a chip, zero-count lanes included | pinned-by: InjectionBrowserTests.TheLaneFilter_NarrowsTheListing_WhileTheChipsCountEveryLane
C2 218-219 | Salience is composed from ExplicitSalience, the same definition the lanes score with | pinned-by: InjectionBrowserTests.Items_CarryTheSalienceBoostTheirScoreTookFromSection7
C2 226-227 | The partial unique index caps promotion at one case per entry | pinned-by: InjectionBrowserTests.Promoting_IsIdempotent_ARepeatClickReturnsTheExistingCase
C2 235-236 | Sessions order by latest entry, entries newest-first inside | pinned-by: InjectionBrowserTests.TheListing_GroupsPerSessionNewestFirst
C2 274-277 | Re-marking switches the verdict and refreshes verdict_at | pinned-by: InjectionBrowserTests.Marking_SticksWithVerdictAt_AndRemarkingSwitches
C2 290-297 | Promote fills the case from the entry, expects the top-ranked still-live Wisdom, is idempotent, null for Brief/no-live-Wisdom | pinned-by: InjectionBrowserTests.Promoting_FillsTheCaseFromTheEntry + Promoting_SkipsARetiredWisdom + Promoting_FallsToTheNextSurvivingItem + ABriefEntry_CannotPromote + Promoting_IsIdempotent
C3 15-18 | The log surface links to §8.1 for curation and never offers curation actions in the score table | pinnable-by: bUnit render test
C3 112-117 | Every browser method opens its own short-lived context; promotion is the only UI write that grows the golden set | pinnable-by: doc-only
C3 197-199 | Most-recalled must stay a server-side jsonb group-by, never a materialize-then-Take | pinnable-by: doc-only
C3 350-351 | The unique-violation catch yields to a concurrently-inserted case | pinnable-by: plain test (race hard to force; only the sequential path is pinned)
C1 25, 57, 101, 128, 131

FILE src/Mimir.Server/Ui/InjectionDisplay.cs total=71 c1=5 c2=48 c3=18 c4=0
C2 8-12 | Formula factors are read off live RecallOptions, never restated defaults | pinned-by: InjectionDisplayTests.Formula_ReadsItsFactorsOffTheLiveOptions_NotARestatementOfTheDefaults
C2 33 | Mcp renders as the initialism "MCP" | pinned-by: InjectionDisplayTests.Name_SpellsMcpAsAnInitialism
C2 49-52 | Budget is the lane's §11 char budget, null for MCP (result-count capped) | pinned-by: InjectionDisplayTests.Budget_IsTheLanesOwnCharBudget_AndNothingForMcp
C2 60-65 | Score precision: two decimals ≥1, four below, enough to tell fused scores apart | pinned-by: InjectionDisplayTests.Score_KeepsEnoughPrecisionToTellTwoFusedScoresApart
C2 69-78 | Two formulas (Brief query-free, query lanes fused with affinity), numbers off the options | pinned-by: InjectionDisplayTests.Formula_ForTheBrief_IsTheQueryFreeScore + Formula_ForAQueryLane_IsTheFusedRankingWithItsAffinityBoost
C2 100-104 | Payload rebuilds through the same InjectionWrapper/InjectionLabel over the same order | pinned-by: InjectionDisplayTests.Payload_RebuildsTheWrapperTheLaneRendered + Payload_KeepsTheRecordedOrder
C2 112-116 | Returns "" when no carried Wisdom survives, null for MCP | pinned-by: InjectionDisplayTests.Payload_IsEmpty_WhenEveryCarriedWisdomWasDeleted + Payload_IsNullForMcp
C2 135-137 | The rebuild is unbounded — recorded items already passed the budget once | pinned-by: InjectionDisplayTests.Payload_IsNotBoundedByAnyBudget_TheRecordedItemsAreWhatAlreadyFitted
C2 141-149 | CannotPromote names the right one of three faults (no query / carried nothing / all dead), null when promotable | pinned-by: InjectionDisplayTests.CannotPromote_TellsCarriedNothingApartFromCarriedOnlyDeadLines + CannotPromote_NamesTheQueryABriefNeverHad + CannotPromote_IsSilentAboutAnEntryThatCanBePromoted
C3 19-23 | This class stays pure of Postgres so its pins run (and fail) on a machine with no Docker | pinnable-by: doc-only
C3 26-29 | A log row's stamp is time-of-day only; the day belongs to the session header | pinnable-by: plain test (TimeOfDay has no test at all today)
C3 79-80 | Changing either RecallScoring method's shape requires changing these prose expressions by hand | pinnable-by: doc-only
C3 105-111 | Rebuild-vs-recorded drift (edited/deleted lines, unrecorded tripwire) is surfaced on screen, with recorded chars as the check | pinnable-by: bUnit render test
C1 15-18, 41

FILE src/Mimir.Server/Ui/ChassisBrowser.cs total=56 c1=4 c2=41 c3=11 c4=0
C2 7-10 | Sidebar entry carries the Project's active (unretired) Wisdom count, Global included as pseudo-project | pinned-by: ChassisBrowserTests.TheSidebarsWisdomCount_IsThisProjectsActiveWisdom + TheSidebar_ListsGlobalFirst
C2 13-20 | Queued = sealed-and-not-Done (Failed included); Distilling = a Running claim held, not backlog | pinned-by: ChassisBrowserTests.TheHeaderPipeline_QueuesSealedNotDone_FailedIncluded + TheHeaderPipeline_Distilling_IsTrueOnlyWhileAClaimIsHeld_NotMerelyBacklogged
C2 23 | Tab-strip counts are per-Project, distinct from the whole-install header | pinned-by: ChassisBrowserTests.TheTabStripCounts_AreThisProjectsAlone
C2 26-32 | "Needs attention" counts run over the ambient universe (Global included) so each count matches its own link's list | pinned-by: ChassisBrowserTests.WisdomAttention_CountsGlobalToo_SoEachFigureIsItsOwnLinksList
C2 35-41 | QueueDepth is Sealed-and-Pending with Failed broken out, narrower than the header's Queued | pinned-by: ChassisBrowserTests.CaptureAttention_SplitsRunningFailedAndQueueDepth_ScopedToThisProject
C2 44 | Recall group is scoped to one Project's Injections | pinned-by: ChassisBrowserTests.RecallAttention_CountsMarkedUsefulAndNoise_ScopedToThisProject
C2 71 | List and single-project lookup share one projection | pinned-by: ChassisBrowserTests.TheSidebarsWisdomCount_IsThisProjectsActiveWisdom
C2 79-80 | The header readout spans every Project | pinned-by: ChassisBrowserTests.TheHeaderPipeline_CountsEveryEpisode_AcrossEveryProject + TheHeaderPipeline_CountsActiveWisdom_AcrossEveryProject
C2 111-115 | Each attention figure is counted through the one keeper producing its link's list | pinned-by: ChassisBrowserTests.WisdomAttention_CountsGlobalToo + WisdomAttention_OrphanedIsActiveWisdomWithNoProvenance
C2 162-166 | First run = no non-Global Project exists; deleting every Episode does not reset it | pinned-by: ChassisBrowserTests.FirstRun_IsTrueWithOnlyGlobal + FirstRun_StaysFalse_OnceIntroduced_EvenWithEveryEpisodeDeleted
C3 51-54 | ChassisBrowser deliberately stays public (no internal dependency forces CS0051), keeping its Blazor consumers public | pinnable-by: doc-only
C3 81-84 | Queued must mirror DistillationQueue.QueueDepthAsync's predicate by hand (context-lifetime mismatch forbids calling it) | pinnable-by: plain test (run both against the same seeded rows)
C3 151-153 | WisdomSinceDeleted must stay a server-side jsonb EXISTS, never a full-table materialization | pinnable-by: doc-only
C1 47-50

FILE src/Mimir.Server/Ui/SurfaceSearch.cs total=26 c1=1 c2=17 c3=8 c4=0
C2 3-7 | One box serves all surfaces; an unclaimed box says so and swallows typing | pinned-by: SurfaceSearchTests.Unclaimed_TheBoxSaysSo_AndSwallowsTyping
C2 21 | Placeholder is null while no surface holds the box | pinned-by: SurfaceSearchTests.Unclaimed_TheBoxSaysSo + AClaim_NamesThePrompt_AndTakesTheTyping
C2 26 | Changed fires on every claim, release and keystroke | pinned-by: SurfaceSearchTests.EveryEdge_RaisesChanged_SoBothSidesRedraw
C2 29-37 | Term resets on both edges; a second claim wins and the superseded release is a no-op; the box is held by holder identity so a same-holder token stays live | pinned-by: SurfaceSearchTests.ANewClaim_StartsFromAnEmptyTerm + ReleasingAClaim_ClearsTheTerm + AnOverlappingClaim_Wins_AndTheOutgoingReleaseIsANoOp + AnEarlierTokenFromTheSameHolder_StillReleases
C2 51 | Set is ignored while no surface holds the box | pinned-by: SurfaceSearchTests.Unclaimed_TheBoxSaysSo_AndSwallowsTyping
C3 8-13 | SurfaceSearch is registered per circuit (Scoped), never per install | pinnable-by: plain test (assert the descriptor's lifetime in UiRegistrationTests)
C3 38-39 | A surface re-claiming for itself must release first, then re-claim | pinnable-by: bUnit render test
C1 18

FILE src/Mimir.Server/Ui/AmbientUniverse.cs total=21 c1=0 c2=14 c3=7 c4=0
C2 6-12 | The §8 universe is the selected Project plus Global, shared by listing and counts; Global needs no special case | pinned-by: WisdomBrowserTests.SelectingAProject_AlsoListsGlobal_TheSetASessionThereRecalls + SelectingGlobal_ListsGlobalAlone + ChassisBrowserTests.WisdomAttention_CountsGlobalToo
C2 22-26 | The three live lenses share one Retired predicate; the Retired lens is the single exception | pinned-by: WisdomBrowserTests lens tests
C2 36-37 | Orphaned = no Provenance row at all, the same rule ToEntries flags by | pinned-by: WisdomBrowserTests.TheOrphanedLens_SurfacesWisdomWithNoProvenanceLeft + ChassisBrowserTests.WisdomAttention_OrphanedIsActiveWisdomWithNoProvenance
C3 13-19 | The §8 universe must never be swapped for the recall lanes' ListAmbientAsync — curation must see Retired rows and harvest-derived (native-content-excluded) Wisdom | pinnable-by: plain test (the Retired half is pinned; the native-content-visible half is pinned nowhere)

FILE src/Mimir.Server/Ui/LikePattern.cs total=16 c1=0 c2=12 c3=4 c4=0
C2 3-8 | % and _ are escaped and the query must pass EscapeCharacter as ILIKE's ESCAPE | pinned-by: LikePatternTests.AMetacharacter_IsEscapedSoItMatchesItself + TheEscapeCharacter_IsTheOneThePatternWasBuiltWith + InjectionBrowserTests.TheSearch_TakesAPercentSignLiterally
C2 15-19 | EscapeCharacter is what the pattern was built with; any other leaves literal backslashes | pinned-by: LikePatternTests.TheEscapeCharacter_IsTheOneThePatternWasBuiltWith + TheEscapeItself_IsEscapedFirst
C2 22 | Contains matches the term literally as a substring | pinned-by: LikePatternTests.APlainTerm_BecomesAContainsMatch
C3 9-12 | Every browser's search box must go through this one class, never a private copy | pinnable-by: doc-only

FILE src/Mimir.Server/Ui/EventPayload.cs total=14 c1=0 c2=6 c3=8 c4=0
C2 7-10 | Payloads render indented with the §4 truncation marker kept visible | pinned-by: EventPayloadTests.Pretty_IndentsAndKeepsTheMarkerReadable
C2 14 | Relaxed escaping keeps the marker as written through re-encoding | pinned-by: EventPayloadTests.Pretty_IndentsAndKeepsTheMarkerReadable
C2 42 | The renderer never throws on malformed input — it answers it as-is | pinned-by: EventPayloadTests.Pretty_AnswersMalformedPayloadAsIs
C3 15 | Relaxed JSON escaping is safe only because Blazor render output HTML-encodes | pinnable-by: bUnit render test
C3 21-27 | The regex must track Capture.PayloadTruncator's exact marker (both sides use independent literals — a marker change leaves these tests green); marker-as-detector chosen over size comparison, false positive accepted | pinnable-by: plain test (assert IsTruncated over PayloadTruncator's real output); the design choice itself doc-only

FILE src/Mimir.Server/Ui/UiRegistration.cs total=5 c1=1 c2=2 c3=2 c4=0
C2 14-15 | Every UI service is registered exactly once (the #91/#94 duplicate) | pinned-by: UiRegistrationTests.EveryUiService_IsRegisteredExactlyOnce
C3 12-13 | SurfaceSearch is Scoped (one circuit = one curator's term), unlike the singleton browsers | pinnable-by: plain test (extend UiRegistrationTests to assert ServiceLifetime.Scoped)
C1 5

### src/Mimir.Server/Distillation

FILE src/Mimir.Server/Distillation/MergeGate.cs total=162 c1=4 c2=147 c3=11 c4=0
C2 11-15 | Distiller candidates carry Episode + plural Event ids, one Provenance row per Event | pinned-by: MergeGateTests.ADistillerShapedCandidate_RecordsOneProvenanceRowPerEvent_Unioned
C2 24-27 | EditAsync's no-op set is exactly three, named | pinned-by: WisdomEditNoOpTests (whole class)
C2 30, 33, 36 | Blank=trimmed-empty, Unknown=no row, Unchanged=same text | pinned-by: WisdomEditNoOpTests (whole class)
C2 40-46 | Gate is sole Wisdom entry: no match inserts; cosine ≥0.80 → arbiter merge/supersede/scope-split | pinned-by: MergeGateTests (whole class)
C2 47-55 | Own context+transaction+advisory lock; rollback is dispose; arbiter failures propagate for caller retry | pinned-by: MergeGateTests.APoisonedRewriteEmbedding_FailsTheBatch_LeavingTheCallersOwnWorkIntact + AnArbiterFailure_Propagates_LeavingTheMatchUntouched
C2 64-68 | One gate-wide advisory xact lock serializes every batch and edit | pinned-by: MergeGateTests.ParallelNearDuplicateBatches_ConvergeOnOneWisdom_ReinforcedTwice + AnEditRacingABatchRewrite_SerializesBehindIt_AndTheChainKeepsGrowing
C2 71-79 | Batch embeds in one round-trip, all-or-nothing on its own context, serialized by the lock | pinned-by: MergeGateTests.AnAdmissionBatch_CommitsTheMarkerAndTheWisdomTogether_EmbeddingOnce + AFailingAdmission_RollsBackTheWholeBatch_LeavingTheMarkerUnset
C2 80-83 | Finalizer runs inside the transaction; its writes commit atomically with the admissions | pinned-by: MergeGateTests.AFinalizerFailure_RollsBackTheWrittenMarker_WithTheAdmissions
C2 109-111 | An admission's search sees what earlier admissions of the same batch staged | pinned-by: DistillationRunTests.OneEpisodesCandidates_MergeWithEachOther_InsideTheOneBatch
C2 115-116 | Nothing is visible until the final commit; disposal rolls the batch back | pinned-by: MergeGateTests.AFailingAdmission_RollsBackTheWholeBatch_LeavingTheMarkerUnset
C2 119-122 | An empty batch commits its finalizer without taking the gate lock | pinned-by: MergeGateTests.AnEmptyBatch_CommitsItsFinalizer_WithoutQueueingBehindTheGateLock
C2 128-130 | Per-admission save stays in-transaction: later candidates see earlier ones, outside sees nothing | pinned-by: DistillationRunTests.OneEpisodesCandidates_MergeWithEachOther_InsideTheOneBatch
C2 145-152 | Edit re-embeds, appends cause=edited under the same lock; reinforcement/recency untouched | pinned-by: MergeGateTests.AnEditRacingABatchRewrite_SerializesBehindIt_AndTheChainKeepsGrowing + AnEdit_LeavesConfirmationAloneAndSkipsAnUnchangedOrMissingWisdom
C2 153-160 | RetiredAt deliberately not consulted: a Retired Wisdom rewords and stays Retired | pinned-by: MergeGateTests.AnEdit_RewordsARetiredWisdom_AndLeavesItRetired
C2 165-167 | Blank is settled before any row is read | pinned-by: MergeGateGuardTests.ABlankEdit_ReturnsBeforeTheGateOpensAnything
C2 175-178 | Unlocked pre-check settles a no-op before the model round-trip; the locked read decides | pinned-by: MergeGateTests.AnEdit_LeavesConfirmationAloneAndSkipsAnUnchangedOrMissingWisdom
C2 197-198 | Re-read under the lock so a raced edit rewords/numbers off the batch's final chain | pinned-by: MergeGateTests.AnEditRacingABatchRewrite_SerializesBehindIt_AndTheChainKeepsGrowing
C2 200-201 | NoOpOf calls a missing row Unknown; the null check is only for the compiler | pinned-by: WisdomEditNoOpTests.AnIdNothingAnswersTo_IsANoOp
C2 212-222 | NoOpOf is the one statement of the no-op set; EditAsync and the §8.1 screen both read it | pinned-by: WisdomEditNoOpTests (whole class) + WisdomDisplayTests
C2 223-228 | current compared untrimmed: stored whitespace means the trim-edit legitimately lands | pinned-by: WisdomEditNoOpTests.TextAlreadySaying_ThisIsANoOp_ComparedAgainstWhatIsStored
C2 237-241 | Both entry points take the lock through one keeper (key/function/placement can't drift) | pinned-by: MergeGateTests.ParallelNearDuplicateBatches_ConvergeOnOneWisdom_ReinforcedTwice + AnEditRacingABatchRewrite_SerializesBehindIt
C2 259-260 | Threshold reads the vector leg's cosine, never the RRF-fused score; FTS-only rows can't match | pinned-by: MergeGateTests.AWordForWordFtsMatch_WithADistantEmbedding_DoesNotReinforce
C2 279-280 | A ScopeSplit between two Global positions degrades to Supersede | pinned-by: MergeGateTests.AScopeSplit_WithNoProjectInPlay_DegradesToSupersede
C2 292 | No match inserts at reinforcement 1, version 1, with Provenance | pinned-by: MergeGateTests.NoMatch_InsertsNewWisdom_AtReinforcementOneVersionOne
C2 325-328 | Agreement: reinforcement+1, last_confirmed=now, provenance unioned, rewrite versioned as merged | pinned-by: MergeGateTests.AnAgreementRewrite_BecomesTheCurrentText_WithTheChainIntact + ANearDuplicate_Reinforces_KeepingTheExistingText
C2 340-342 | Only a candidate scoped to a different Project promotes to Global; a Global candidate can't vouch | pinned-by: MergeGateTests.AnAgreementProposedAsGlobal_IsNotCrossProjectConfirmation_AndDoesNotPromote
C2 353-354 | An agreement rewrite is re-embedded | pinned-by: MergeGateTests.AnAgreementRewrite_BecomesTheCurrentText_WithTheChainIntact
C2 365-368 | Supersede: candidate inserted Contested; loser Retired with superseded_by, text/chain untouched | pinned-by: MergeGateTests.ASupersedeRuling_RetiresTheOldWisdom_AndInsertsTheCandidate
C2 378-383 | ScopeSplit: matched row keeps its own side, sibling takes the other; both Contested, full provenance union, no confirmation | pinned-by: MergeGateTests.AScopeSplit_OnProjectScopedWisdom_AddsAGlobalSibling + AScopeSplit_OnGlobalWisdom_AddsAProjectScopedSibling
C2 442-445 | RewriteAsync is the one keeper of a rewrite; the version chain has one writer under the lock | pinned-by: MergeGateTests.AnEditRacingABatchRewrite_SerializesBehindIt_AndTheChainKeepsGrowing
C2 458-459 | Version rows are always flushed per admission, so the max is authoritative on this connection | pinned-by: DistillationRunTests.OneEpisodesCandidates_MergeWithEachOther_InsideTheOneBatch
C2 476-480 | Provenance is unioned: an already-recorded link is not recorded again | pinned-by: MergeGateTests.ReinforcingFromTheSameHarvestedItem_DoesNotDuplicateProvenance
C2 500-504 | One row per Event; no Events means one row; nothing at all means zero rows, never all-null | pinned-by: MergeGateTests.ADistillerShapedCandidate_RecordsOneProvenanceRowPerEvent_Unioned + McpRememberServiceTests.WithNoUnsealedEpisode_TheContentGoesThroughTheMergeGate
C3 89-92 | Batch embeddings are generated before the transaction/lock opens; only arbiter rewrites embed inside | pinnable-by: plain test
C3 100-102 | An embedding batch shorter than the candidate list must fail loudly before the lock, not silently skip and commit the marker | pinnable-by: plain test
C3 188-191 | An edit's embedding is generated before taking the gate lock (one wasted embed on a raced pre-check is the accepted price) | pinnable-by: plain test
C1 246-249

FILE src/Mimir.Server/Distillation/DistillationQueue.cs total=79 c1=0 c2=71 c3=8 c4=0
C2 9-15 | Queue is state on the Episode row; all transitions after creation live in this class | pinned-by: DistillationQueueTests + DistillationRunTests (whole classes)
C2 16-19 | Sealing enqueues via the pending starting value; no unsealed row can be claimed | pinned-by: DistillationRunTests.TheQueue_TakesTheOldestSeal_AndIgnoresUnsealedAndDone
C2 25-32 | Membership rule = Sealed and not done; QueueDepth restates the partial index's filter verbatim | pinned-by: DistillationQueueTests.QueueDepth_CountsEverySealedEpisodeNotYetDone
C2 38-45 | Boot cutoff is MaxValue so even a claim stamped at/after boot (clock stepped back) is re-queued | pinned-by: DistillationQueueTests.TheStaleSweep_LeavesFreshClaims_ThatBootRecoveryTakesBack
C2 48-52 | Claim takes the oldest-Sealed pending Episode, stamped running | pinned-by: DistillationRunTests.TheQueue_TakesTheOldestSeal_AndIgnoresUnsealedAndDone
C2 58 | An unsealed row is a live session, not work | pinned-by: DistillationRunTests.TheQueue_TakesTheOldestSeal_AndIgnoresUnsealedAndDone
C2 74-78 | FailAsync is state-guarded: only a still-running claim parks failed | pinned-by: DistillationQueueTests.TheFailureParking_OnlyTouchesAClaimStillRunning
C2 86-91 | The done marker runs on the gate's batch context and commits with the Wisdom or not at all | pinned-by: DistillationRunTests.ASealedPendingEpisode_DistillsToDone_WithEventProvenance + AFailureInsideTheBatch_LeavesTheEpisodeStillOwedDistillation
C2 95 | Clock read into a local because EF can't translate it inside SetProperty | pinned-by: DistillationRunTests (runtime translation)
C2 106-111 | Boot re-queues every Running claim (a previous process's) immediately | pinned-by: DistillationQueueTests.BootRecovery_RequeuesAnAbandonedRunningClaim
C2 115-121 | Sweep re-queues only claims quiet past StaleRunningAfter; the cutoff is the whole difference | pinned-by: DistillationQueueTests.TheStaleSweep_LeavesFreshClaims_ThatBootRecoveryTakesBack
C2 125-126 | failed → pending re-queue | pinned-by: DistillationSweepTests.FailedEpisodes_AreRequeued
C2 136-141 | Depth counts everything Sealed and not done, failed included | pinned-by: DistillationQueueTests.QueueDepth_CountsEverySealedEpisodeNotYetDone
C2 147-151 | The one running→pending: unstamped claims cannot prove themselves fresh and are taken | pinned-by: DistillationQueueTests.TheStaleSweep_LeavesFreshClaims + DistillationSweepTests.OnlyRunningClaims_StalePastAnHour_AreReset
C3 20-24 | Exactly two pending writes exist outside this class, each riding an update guarded on sealed_at IS NULL (provable no-op restates) | pinnable-by: doc-only
C3 53-55 | The claimed Episode is returned tracked on the caller's shared scoped context so it can be reloaded after the gate moves the row | pinnable-by: plain test

FILE src/Mimir.Server/Distillation/EpisodeDistiller.cs total=42 c1=4 c2=34 c3=3 c4=1
C2 10-15 | Whole stream in, answered for the whole Episode or not at all; chunking behind the seam | pinned-by: EpisodeDistillerTests.AGoodChunkThenAnUnparseableOne_Throws_LettingNoPartialListOut + AnOversizedEpisode_IsDistilledPerChunk
C2 18-23 | Seq ordering is the caller's to supply; the chunker re-sorts only to slice chronologically | pinned-by: DistillationRunTests.ASealedPendingEpisode_DistillsToDone_WithEventProvenance + EpisodeChunkerTests.AnEpisodeWithinTheBudget_IsOneChunk_InSeqOrder
C2 24-28 | DistillerException means no partial list; callers propagate so the Episode stays owed | pinned-by: EpisodeDistillerTests.AGoodChunkThenAnUnparseableOne_Throws_LettingNoPartialListOut + DistillationRunTests.AnUnusableAnswer_MarksFailed_AndAdmitsNothing
C2 36-42 | Distilled per chunk (gate is the reduce); [eN] labels map answers back to Event ids | pinned-by: EpisodeDistillerTests.AnOversizedEpisode_IsDistilledPerChunk + Candidates_CarryKindScopeText_AndTheReferencedEventIds
C2 54-56 | Semantic checks a grammar can't express (usable text, real refs) stay in Parse | pinned-by: EpisodeDistillerTests.BlankCandidates_AreSkipped_AndLongTextIsCappedAt500 + HallucinatedEventRefs_AreDropped_AndNoRealRefsMeansEpisodeProvenance
C2 84-87 | Prompt: durable lessons only, prefer none over weak; a Remember Event always deserves a candidate | pinned-by: EpisodeDistillerTests.ARememberEvent_IsMarkedAsADeliberateSave
C2 185 | A blank note is the model failing to decline; it is skipped | pinned-by: EpisodeDistillerTests.BlankCandidates_AreSkipped_AndLongTextIsCappedAt500
C2 192-193 | Out-of-chunk event refs are hallucinated and dropped; ref-less falls back to Episode-level provenance | pinned-by: EpisodeDistillerTests.HallucinatedEventRefs_AreDropped_AndNoRealRefsMeansEpisodeProvenance
C3 51-53 | The distiller's own request carries the candidates schema as a generation constraint (format on the call) | pinnable-by: plain test
C4 109 | <inheritdoc/> on DistillAsync
C1 79-81, 232

FILE src/Mimir.Server/Distillation/DistillerCall.cs total=28 c1=0 c2=28 c3=0 c4=0
C2 6-14 | Both model steps call the same way: non-reasoning, fixed context, schema-constrained JSON, stated once | pinned-by: MergeArbiterTests.ThePrompt_CarriesBothTexts_NoThink_AndTheVerdictSchema + EpisodeDistillerTests.ThePrompt_LabelsEventsBySeq_AndSpeaksNoThink
C2 17-22 | ContextTokens=16384 mapped to num_ctx, a constant chunking budgets inside | pinned-by: EpisodeDistillerTests.ThePrompt_LabelsEventsBySeq_AndSpeaksNoThink
C2 25-29 | /no_think ends the user turn of every distiller-model call | pinned-by: EpisodeDistillerTests + MergeArbiterTests prompt tests
C2 32-39 | Temperature 0, §11 context, schema passed as the request's format (constrained decoding) | pinned-by: MergeArbiterTests.ThePrompt_CarriesBothTexts_NoThink_AndTheVerdictSchema

FILE src/Mimir.Server/Distillation/MergeRuling.cs total=24 c1=1 c2=23 c3=0 c4=0
C2 5-9 | Closed ruling hierarchy over a ≥0.80 match: agree-merge, or contradict via Supersede/ScopeSplit | pinned-by: MergeGateTests + MergeArbiterTests (whole classes)
C2 16 | Agreement carries the merged rewrite | pinned-by: MergeArbiterTests.AnAgreementVerdict_YieldsTheMergedRewrite
C2 19-22 | Supersede: candidate inserted, old row Retired with superseded_by | pinned-by: MergeGateTests.ASupersedeRuling_RetiresTheOldWisdom_AndInsertsTheCandidate
C2 25-28 | ScopeSplit: rewritten into one Global and one Project-scoped Wisdom, cause=adjudicated | pinned-by: MergeGateTests.AScopeSplit_OnProjectScopedWisdom_AddsAGlobalSibling
C2 32-35 | Arbiter is the LLM half of the gate; faked in gate tests | pinned-by: MergeArbiterTests (whole class)
C2 39-43 | Unusable answers throw and propagate so admission retries instead of degrading to a mechanical merge | pinned-by: MergeArbiterTests.AnUnusableAnswer_Throws + MergeGateTests.AnArbiterFailure_Propagates_LeavingTheMatchUntouched
C1 47

FILE src/Mimir.Server/Distillation/DistillationRun.cs total=24 c1=1 c2=20 c3=3 c4=0
C2 10-18 | One turn: claim, distill (model calls before the batch), admit as one batch with the done marker as finalizer | pinned-by: DistillationRunTests (whole class)
C2 26 | Null attempt when the queue is empty | pinned-by: DistillationRunTests.TheQueue_TakesTheOldestSeal_AndIgnoresUnsealedAndDone
C2 44-46 | Shutdown mid-run admits nothing; boot recovery re-queues the Running claim | pinned-by: DistillerServiceTests.ARunningClaimFromADeadProcess_IsRequeuedAndWorkedOnBoot
C2 52-53 | A failed batch needs no tracker cleanup here — it ran on the gate's own context | pinned-by: MergeGateTests.APoisonedRewriteEmbedding_FailsTheBatch_LeavingTheCallersOwnWorkIntact
C2 73-77 | The marker commits with the Episode's Wisdom or not at all | pinned-by: DistillationRunTests.AFailureInsideTheBatch_LeavesTheEpisodeStillOwedDistillation
C3 84-86 | After the batch commits, the run's tracked Episode copy is reloaded so later readers read done, not the stale claim | pinnable-by: plain test
C1 7

FILE src/Mimir.Server/Distillation/EpisodeChunker.cs total=16 c1=0 c2=16 c3=0 c4=0
C2 5-10 | Chronological token windows, no reduce (the gate is the reduce); Remembers ride in every chunk in seq position | pinned-by: EpisodeChunkerTests.AnOversizedEpisode_SplitsChronologically_LosingNothing + RememberEvents_RideAlongInEveryChunk
C2 13-18 | 4 chars/token estimate prices the budget | pinned-by: EpisodeChunkerTests.AnOversizedEpisode_SplitsChronologically_LosingNothing
C2 21 | 16-token per-Event overhead for the [eN] header | pinned-by: EpisodeChunkerTests.AnOversizedEpisode_SplitsChronologically_LosingNothing
C2 37-39 | An Event never splits; a window holds at least one; a pile of Remembers can't starve the windows | pinned-by: EpisodeChunkerTests.ASingleEventOverTheBudget_StillGetsAChunk + AnEpisodeOfOnlyRememberEvents_IsOneChunk_EvenOverBudget

FILE src/Mimir.Server/Distillation/DistillationSweep.cs total=16 c1=1 c2=15 c3=0 c4=0
C2 12 | QueueGrew means the worker is worth waking | pinned-by: DistillationSweepTests.FailedEpisodes_AreRequeued + DistillationSweepServiceTests.TheBootPass_RequeuesFailedEpisodes_AndPokesTheWorker
C2 16-24 | Crash-Seal idle unsealed Episodes, run both recovery legs, never touch done, Contested clear rides along | pinned-by: DistillationSweepTests (whole class)
C2 36-40 | Idle means no Event lately (last Event or start); the pending write is a guarded no-op restate | pinned-by: DistillationSweepTests.UnsealedEpisodes_IdlePastADay_AreCrashSealed
C1 9

FILE src/Mimir.Server/Distillation/MergeArbiter.cs total=13 c1=0 c2=13 c3=0 c4=0
C2 7-13 | One call per DistillerCall's shape; rewrites capped at MaxTextLength; anything unusable throws | pinned-by: MergeArbiterTests (whole class)
C2 22-27 | Schema deliberately flat; per-verdict conditional required texts enforced in Parse | pinned-by: MergeArbiterTests.AnUnusableAnswer_Throws + ThePrompt_CarriesBothTexts_NoThink_AndTheVerdictSchema

FILE src/Mimir.Server/Distillation/DistillerService.cs total=15 c1=2 c2=7 c3=6 c4=0
C2 6-12 | Single worker: wakes on trigger or idle poll, drains one at a time, failure parks failed and degrades the tile, never retried hot | pinned-by: DistillerServiceTests (whole class)
C3 36 | After a success the loop looks again immediately (drains without waiting on timer or trigger) | pinnable-by: plain test
C3 46-47 | A won trigger cancels the pending timer so no abandoned Task.Delay accumulates per Seal | pinnable-by: plain test
C3 96-97 | Only shutdown cancellation may stop the loop; any other failure degrades the tile and retries after FailureRetryInterval | pinnable-by: plain test
C3 107 | Null figures keep the tile's last known depth/lastRunAt | pinnable-by: plain test
C1 50, 59

FILE src/Mimir.Server/Distillation/DistillationTrigger.cs total=10 c1=0 c2=7 c3=2 c4=1
C2 5-9 | A Seal's poke wakes the worker before its poll; fire-and-forget, the Seal path never waits | pinned-by: DistillerServiceTests.ASealTrigger_WakesTheWorkerWithoutTheTimer
C2 12 | Request never blocks | pinned-by: DistillerServiceTests.ASealTrigger_WakesTheWorkerWithoutTheTimer
C2 15 | WaitAsync completes when a look was requested since the last wait | pinned-by: DistillationSweepServiceTests.TheBootPass_RequeuesFailedEpisodes_AndPokesTheWorker
C3 23-24 | Capacity-one drop-on-full: N pokes while the worker is busy coalesce into one wake-up | pinnable-by: plain test
C4 19 | <inheritdoc cref="IDistillationTrigger"/>

FILE src/Mimir.Server/Distillation/ModelAnswer.cs total=7 c1=2 c2=5 c3=0 c4=0
C2 6-9 | The 500-char budget applies to every text the model hands back | pinned-by: EpisodeDistillerTests.BlankCandidates_AreSkipped_AndLongTextIsCappedAt500 + MergeArbiterTests.AnOverlongRewrite_IsCappedAtFiveHundredChars
C2 16 | A stray ```json fence is shed before parsing | pinned-by: MergeArbiterTests.AFencedJsonAnswer_StillParses
C1 3, 12

FILE src/Mimir.Server/Distillation/ContestedSweep.cs total=8 c1=1 c2=7 c3=0 c4=0
C2 7-13 | contested_at is cleared after ContestedDuration — a recency flag, not a permanent stain; runs inside the §6 sweep | pinned-by: ContestedSweepTests.OnlyFlagsPastTheContestedDuration_AreCleared + DistillationSweepTests
C1 16

FILE src/Mimir.Server/Distillation/DistillationSweepService.cs total=5 c1=0 c2=4 c3=1 c4=0
C2 6-8, 10 | Sweep runs on boot then every SweepInterval, poking the worker when the queue grew | pinned-by: DistillationSweepServiceTests.TheBootPass_RequeuesFailedEpisodes_AndPokesTheWorker
C3 9 | A failed sweep pass is swallowed and the loop just waits for the next tick | pinnable-by: plain test

### src/Mimir.Server/Storage (incl. Entities and Migrations)

FILE src/Mimir.Server/Storage/WisdomSearch.cs total=98 c1=2 c2=45 c3=51 c4=0
C2 11-15 | Filters apply in SQL before the per-leg LIMIT, never as post-filtering of an unfiltered top-N | pinned-by: McpSearchServiceTests.AFilter_FindsMatchesTheUnfilteredTopNWouldHaveCrowdedOut
C2 20-21 | Retired Wisdom surfaces only for mimir_search with include_retired; all other callers exclude it | pinned-by: McpSearchServiceTests.RetiredWisdom_SurfacesOnlyWithIncludeRetired_AndIsMarked + WisdomSearchTests.RetiredWisdom_IsInvisibleToBothLegs
C2 28 | Since keeps only Wisdom confirmed at or after the instant (gates on last_confirmed_at) | pinned-by: McpSearchServiceTests.KindAndSinceFilters_KeepOnlyMatchingWisdom
C2 32-37 | FusedScore is ordering-only rank fusion (max ≈ 0.033); Cosine is null off the vector leg and the only thresholdable number | pinned-by: WisdomSearchTests.FusedScores_AreRankFusionValues_NeverACosineScale + Cosine_IsTheVectorLegsSimilarity_AndNullOffTheVectorLeg
C2 47-52 | §3 hybrid search: per-leg top-N vector KNN + FTS fused with RRF over non-Retired Wisdom | pinned-by: WisdomSearchTests (whole class)
C2 62-72 | AmbientClause is the single SQL statement of the §7 universe: Project+Global scope, non-Retired, native-content exclusion, orphaned provenance stays in | pinned-by: WisdomSearchAmbientTests.AmbientUniverse_SearchAndList_AgreeOnTheFullEligibilityMatrix
C2 89-91 | Each leg ranks internally contributing 1/(k+rank); FULL JOIN keeps single-leg rows | pinned-by: WisdomSearchTests.RrfFusion_RanksADualLegRowAboveEitherSingleLegRow
C2 173-174 | Unfiltered SearchAsync is the everything universe reaching every Project's Wisdom | pinned-by: McpSearchServiceTests.FusedResults_ReachOtherProjectsWisdom_AndEpisodeEvents_AndLogTheInjection
C2 179-183 | Ambient search restricts both legs before the per-leg LIMIT so a foreign corpus cannot crowd out eligible matches | pinned-by: WisdomSearchAmbientTests.AmbientUniverse_RestrictsBeforeThePerLegLimit_NotAfterFusion
C2 188-189 | ListAmbientAsync returns exactly the ambient universe's ids | pinned-by: WisdomSearchAmbientTests.AmbientUniverse_SearchAndList_AgreeOnTheFullEligibilityMatrix
C2 218-219 | The embedding binds as its text form and is CAST in SQL (no vector mapping on the raw-SQL path) | pinned-by: WisdomSearchTests (whole class, against real Postgres)
C3 53-59 | The Candidate Universe is named by the method, never assembled from caller-supplied filter combinations | pinnable-by: doc-only
C3 92-93 | Ties break on id so ordering is deterministic under equal fused scores | pinnable-by: plain test
C3 131-133 | AmbientIdsSql carries no LIMIT and no ORDER BY — the caller ranks; adding either is a second silent ranking | pinnable-by: plain test
C3 134-160 | Unbounded-listing decision record: #72 benchmark (~0.3 s flat at 50k rows), first-compose warm-up cost, BriefTripwire as the standing guard | pinnable-by: doc-only
C3 190-193 | ListAmbientAsync's unordered-and-unlimited promise is load-bearing | pinnable-by: plain test
C3 203-210 | ambientProjectId and filter are alternatives, not a combination — every public entry passes a constant for one, so no caller can state the contradiction | pinnable-by: doc-only
C1 167-168

FILE src/Mimir.Server/Storage/MimirDbContext.cs total=34 c1=1 c2=8 c3=25 c4=0
C2 48 | The Global pseudo-project is seeded at migration time with a fixed id | pinned-by: PostgresTestBaseTests.AfterTheReset_TheGlobalPseudoProjectIsTheOnlyProject
C2 95 | Event.tsv is a stored generated column over the payload's string values feeding the Episode FTS leg | pinned-by: McpSearchServiceTests.FusedResults_ReachOtherProjectsWisdom_AndEpisodeEvents_AndLogTheInjection
C2 103 | Hard-deleting an Episode removes its Events (§8.2 cascade) | pinned-by: ProvenanceDeletionTests.HardDeletingAnEpisode_TakesItsEventsProvenanceWithIt_TheWisdomSurvives
C2 135 | Embedding dimension 1024 is schema, not config | pinned-by: WisdomSearchTests (every insert uses 1024-d TestVectors)
C2 143 | Wisdom.tsv is a stored generated column over text — the FTS leg | pinned-by: WisdomSearchTests
C2 171 | Deleting a Wisdom cascades its version chain; nothing else touches it (§10) | pinned-by: ProvenanceDeletionTests.DeletingAWisdom_CascadesItsVersionChainAndProvenance_ReferencedRecordsUntouched
C2 191-192 | Hard-deleting an Event/Episode cascades Provenance away — the sole operation that removes it | pinned-by: ProvenanceDeletionTests (whole class)
C3 6-11 | snake_case tables/columns matching §3 because ranking SQL is hand-written; an entity's creating ticket builds its full §3 column set | pinnable-by: doc-only
C3 45 | The GIN index over root_paths serves "which Project has been seen at this root" (§3.1/§5) | pinnable-by: doc-only
C3 74-77 | The partial Distillation index's filter must match DistillationQueue's claim/depth predicates — change one, revisit the other | pinnable-by: doc-only
C3 120 | The path index serves the scanner's latest-row-per-path working set | pinnable-by: doc-only
C3 123 | The filtered converted_at index is the converter's unseen-versions working set | pinnable-by: doc-only
C3 151-152 | HNSW chosen over IVFFlat because it needs no training rows and works from the first Wisdom | pinnable-by: doc-only
C3 156 | Deleting a superseder leaves the retired loser retired, just unlinked (SetNull) | pinnable-by: plain test
C3 193 | HarvestedItem-referencing Provenance restricts because HarvestedItems are never hard-deleted | pinnable-by: plain test
C3 218 | Session and Project indexes exist for the §8.3 injection-log reads | pinnable-by: doc-only
C3 235-237 | Partial unique index makes PromoteAsync idempotency durable under concurrent clicks; hand-inserted cases stay unconstrained | pinnable-by: plain test (only sequential idempotency is pinned today)
C3 242-243 | A GoldenCase expecting hard-deleted Wisdom cascades away with it rather than poisoning the suite | pinnable-by: plain test
C3 246-247 | The promotion link is a breadcrumb (SetNull): losing the source Injection must not delete the case | pinnable-by: plain test
C1 215

FILE src/Mimir.Server/Storage/StorageQueries.cs total=28 c1=0 c2=28 c3=0 c4=0
C2 3-6 | Deliberately schema-agnostic: discovers whatever tables exist rather than naming domain tables | pinned-by: PostgresStorageProbeTests (whole class)
C2 7-12 | ADR-0006: bytes and EXISTS-based occupancy, never a row count or statistic | pinned-by: StorageQueriesTests.OccupancyNeverCounts + PostgresStorageProbeTests.AnalyzedWhileEmptyThenPopulated_ReportsPopulated
C2 15 | __EFMigrationsHistory is infrastructure, not reported data | pinned-by: StorageQueriesTests.TheMigrationsHistoryTable_IsNotReportedAsData
C2 20-22 | Discovery and sizing come from one catalog read | pinned-by: StorageQueriesTests.DiscoveryExcludesPartitionChildren_SoAPartitionedTableIsCountedOnce
C2 23-28 | NOT relispartition prevents double-counting; the CASE on relkind is mandatory or plain tables size at 0 | pinned-by: StorageQueriesTests.DiscoveryRollsUpPartitionSizes_OnlyForPartitionedParents + PostgresStorageProbeTests
C2 45-48 | One EXISTS leg per table unioned; null when there is nothing to ask | pinned-by: StorageQueriesTests.NoTables_ProducesNoQuery + EachTableBecomesOneOccupancyLeg
C2 62-65 | Catalog names are quoted so provenance is irrelevant and mixed case resolves | pinned-by: StorageQueriesTests.IdentifiersAreQuoted_SoCatalogNamesCanNeverBreakOutOfTheQuery + LabelLiteralsEscapeTheirQuotes

FILE src/Mimir.Server/Storage/DbRaces.cs total=26 c1=0 c2=0 c3=26 c4=0
C3 6-9 | The capture write path is optimistic: racers lose on unique indexes, re-read and retry | pinnable-by: doc-only
C3 12-15 | SeqRaceMaxAttempts=5 because N appenders can lose consecutive rounds on (episode_id, seq) | pinnable-by: doc-only
C3 18-22 | CreateRaceMaxAttempts=3: a lost create race resolves on the next query; #17 identity races share the bound | pinnable-by: doc-only
C3 25-28 | Only a unique-key collision may be retried; any other failure must surface | pinnable-by: plain test
C3 32 | On raw SQL the PostgresException arrives unwrapped, hence the second overload | pinnable-by: doc-only
C3 36-39 | An FK violation during the #17 clone merge means the loser was deleted mid-merge; retry finds the survivor | pinnable-by: doc-only
C3 43-46 | The rolled-back clone merge is retried whole; anything but the staged FK race surfaces | pinnable-by: doc-only

FILE src/Mimir.Server/Storage/EventSearch.cs total=17 c1=0 c2=4 c3=13 c4=0
C2 38-40 | The Episode leg is FTS-only over Event.tsv (Events carry no embeddings in v1) | pinned-by: McpSearchServiceTests.FusedResults_ReachOtherProjectsWisdom_AndEpisodeEvents_AndLogTheInjection
C2 71 | projectId narrows to one Project's Episodes; null reaches all | pinned-by: McpSearchServiceTests.ProjectFilter_NarrowsBothLegs_AndAMissNamesTheKnownProjects
C3 7-10 | A hit carries enough Episode metadata to render a timeline entry with no second query; Type is the stored enum string | pinnable-by: plain test
C3 23-24 | Payload is clipped server-side to a preview because stored payloads run to tens of KB | pinnable-by: plain test
C3 41-43 | Filters and rank are wholly in SQL, so the caller's cap is the query LIMIT — no over-fetch | pinnable-by: plain test
C3 46-47 | 1000 preview chars is a margin over the rendered snippet, which collapses whitespace before clipping | pinnable-by: doc-only
C3 72 | since keeps only Events captured at or after the instant | pinnable-by: plain test
C3 73 | topN is applied as the query LIMIT | pinnable-by: plain test

FILE src/Mimir.Server/Storage/PostgresStorageProbe.cs total=17 c1=2 c2=0 c3=14 c4=1
C3 21-27 | Deliberately no wrapping transaction: sizes ignore snapshots while EXISTS honours them, so REPEATABLE READ produces the forbidden "27 MB and empty" | pinnable-by: doc-only (measured behavior)
C3 68-71 | A mid-drop table sizes to NULL (measured); skip the row rather than crash or earn a 42P01 downstream | pinnable-by: doc-only
C3 110-112 | On 42P01 the whole occupancy union aborts, so report Unknown for all rather than guessing or degrading the tile | pinnable-by: doc-only (downstream Unknown rendering pinned by StorageTileFactoryTests; the probe-side catch is not)
C4 7 | <inheritdoc cref="IStorageProbe"/>
C1 11, 56

FILE src/Mimir.Server/Storage/StorageRegistration.cs total=7 c1=0 c2=0 c3=7 c4=0
C3 9 | One Postgres for vectors, full-text and relational metadata (§3/ADR-0005) | pinnable-by: doc-only
C3 15-18 | Both the context factory (for Blazor circuits) and the plain scoped context must be registered — AddDbContextFactory registers only the factory | pinnable-by: plain test
C3 22-23 | Options must stay Singleton alongside the factory or the singleton factory's root-provider resolution is poisoned (#23) | pinnable-by: plain test (rule is mirrored, not asserted, by PostgresTestBase.AddThrowawayStorage)

FILE src/Mimir.Server/Storage/StorageService.cs total=6 c1=0 c2=0 c3=6 c4=0
C3 6-11 | Sole owner of the Storage tile: migrates with retry until Postgres answers, in the background so the health strip stays visible while Postgres boots | pinnable-by: plain test

FILE src/Mimir.Server/Storage/MimirDbContextFactory.cs total=4 c1=0 c2=0 c3=4 c4=0
C3 6-9 | Design-time only, never used at runtime — exists so `dotnet ef migrations add` needs no running server or Postgres | pinnable-by: doc-only

FILE src/Mimir.Server/Storage/StorageTileFactory.cs total=3 c1=1 c2=2 c3=0 c4=0
C2 32-33 | With any occupancy Unknown, the summary is the table count alone — folding Unknown into Populated is the prohibited misreport | pinned-by: StorageTileFactoryTests.WhenAnyTableIsUnknown_TheSummaryMakesNoOccupancyClaim
C1 5

FILE src/Mimir.Server/Storage/ByteSize.cs total=2 c1=1 c2=1 c3=0 c4=0
C2 19 | Whole bytes render with no decimal point; scaled units carry one | pinned-by: ByteSizeTests.FormatsToTheLargestUnitThatKeepsTheNumberReadable
C1 3

FILE src/Mimir.Server/Storage/IStorageProbe.cs total=1 c1=0 c2=1 c3=0 c4=0
C2 5 | The probe measures what is currently in the database (not statistics) for the §8 tile | pinned-by: PostgresStorageProbeTests (whole class)

FILE src/Mimir.Server/Storage/Entities/HarvestedItem.cs total=23 c1=1 c2=21 c3=1 c4=0
C2 3-7 | One row per version: a changed hash inserts a new row, prior rows kept forever, latest row per path is current | pinned-by: HarvestScannerTests (whole class)
C2 14-17 | Path is harvest-relative with forward slashes — the file's stable identity across scans | pinned-by: HarvestScannerTests
C2 25 | FirstSeen is copied forward across versions | pinned-by: HarvestScannerTests
C2 31-35 | GoneAt lands on the then-current version and stays as history; a reappearing file starts a fresh row; derived Wisdom untouched | pinned-by: HarvestScannerTests
C2 38-43 | ConvertedAt null = pending (carries the Backfill); set in the same transaction as the gate's writes for exactly-once handoff | pinned-by: MergeGateTests + HarvestConverterTests
C3 20 | ContentHash is SHA-256 of the file bytes, lowercase hex | pinnable-by: plain test
C1 28

FILE src/Mimir.Server/Storage/Entities/Project.cs total=22 c1=0 c2=22 c3=0 c4=0
C2 3-7 | Identity follows the repository: two clones are one Project; path-born rows upgrade in place, colliding clones merge | pinned-by: ProjectResolverTests (whole class) + ProjectMergeTests
C2 10-14 | GlobalId is the reserved §3 pseudo-project, seeded by migration at a fixed id | pinned-by: PostgresTestBaseTests.AfterTheReset_TheGlobalPseudoProjectIsTheOnlyProject
C2 17-20 | GlobalIdentity is "mimir:global" — neither a normalized remote nor a path, so no real repository collides | pinned-by: PostgresTestBaseTests.AfterTheReset_TheGlobalPseudoProjectIsTheOnlyProject
C2 25 | Identity is the normalized remote, else the root path; unique | pinned-by: ProjectResolverTests + RemoteIdentityTests
C2 28 | Unseen roots are appended on match, never overwritten | pinned-by: ProjectResolverTests.ANewRootForAKnownIdentity_IsAppendedNotDuplicated + ARootAppendedByAnotherContext_SurvivesThisContextsAppend
C2 33-38 | IsPathBorn = identity sits in RootPaths; only such a row upgrades in place or loses a clone merge | pinned-by: ProjectResolverTests.APathIdentityProject_ReportingARemote_IsUpgradedInPlace + AKnownRootWithADifferentRemoteIdentity_MatchesByRoot_AndKeepsItsStoredIdentity

FILE src/Mimir.Server/Storage/Entities/Injection.cs total=21 c1=5 c2=7 c3=9 c4=0
C2 3-7 | One actual injection per row; empty decisions are never logged (§7); verdict applies to the entry as a whole | pinned-by: InjectionLogTests + McpSearchServiceTests.NoMatches_AnswersPlainly_AndLogsNothing
C2 29 | QueryContext is the prompt for Prompt, the tool query for MCP, null for Brief | pinned-by: McpSearchServiceTests + PromptRecallServiceTests + BriefServiceTests
C2 32 | Chars is the whole labeled wrapper as printed | pinned-by: McpSearchServiceTests + InjectionWrapperTests
C3 12-16 | SessionId is deliberately not an FK: an Episode hard-delete purges content, not the record that an injection happened | pinnable-by: plain test
C3 19-22 | ProjectId's meaning is lane-specific; InjectionContext is the normative statement of which each lane passes | pinnable-by: doc-only
C1 35, 38, 44, 52, 60

FILE src/Mimir.Server/Storage/Entities/Wisdom.cs total=17 c1=3 c2=14 c3=0 c4=0
C2 6-10 | Only the Merge Gate writes new rows; Text is the current version with the chain in WisdomVersion; Retired is excluded from all Recall and default search | pinned-by: WisdomSearchTests.RetiredWisdom_IsInvisibleToBothLegs + MergeGateTests
C2 17-20 | scope is a Project id with Global as the reserved pseudo-project | pinned-by: WisdomSearchAmbientTests
C2 29 | Tsv is Postgres-generated, never written by the app | pinned-by: WisdomSearchTests
C2 32 | Reinforcement counts gate confirmations; 1 at birth | pinned-by: MergeGateTests
C2 37 | ContestedAt set by adjudication on contradiction, cleared after 14 days | pinned-by: MergeGateTests + ContestedSweepTests
C2 40 | Retired = excluded from recall and default search, reversibly | pinned-by: WisdomSearchTests + McpSearchServiceTests
C2 43 | SupersededBy is set together with retirement (§6.4) | pinned-by: MergeGateTests
C1 23, 26, 47

FILE src/Mimir.Server/Storage/Entities/Episode.cs total=15 c1=2 c2=13 c3=0 c4=0
C2 3-7 | An Episode is a session (ADR-0003), keyed by session id; unsealed = live or crashed; seal reason is hook-reported or crash-swept | pinned-by: CaptureServiceTests + DistillationSweepTests
C2 15 | SessionId is unique — one Episode per session | pinned-by: CaptureServiceTests.SessionStartTwice_ResumesTheSameEpisode
C2 24 | SealReason is the SessionEnd reason, or crash-swept | pinned-by: CaptureServiceTests.SessionEnd_SealsWithTheHookReportedReason + DistillationSweepTests
C2 29 | The §6 distillation queue is this column | pinned-by: DistillationQueueTests
C2 32-36 | DistillationStartedAt is how "stale Running > 1 h" is measurable across restarts for the sweep's claim reset | pinned-by: DistillationSweepTests
C1 10, 42

FILE src/Mimir.Server/Storage/Entities/Event.cs total=14 c1=1 c2=13 c3=0 c4=0
C2 5-9 | Payloads stored truncated per §4 with original size recorded; Tsv feeds the Episode FTS leg; no embeddings in v1 | pinned-by: PayloadTruncatorTests + CaptureServiceTests + McpSearchServiceTests
C2 16 | Seq is arrival order from 1, unique per Episode | pinned-by: CaptureServiceTests.Events_ArriveInSequenceOnTheSessionsEpisode
C2 26 | PayloadFullSize is the UTF-8 size before truncation | pinned-by: CaptureServiceTests
C2 29 | Salient is true only for Remember Events | pinned-by: CaptureServiceTests + McpRememberServiceTests.LandsSalient_OnTheMostRecentlyActiveUnsealedEpisode
C2 32 | Tsv is Postgres-generated, never written by the app | pinned-by: McpSearchServiceTests
C2 36-39 | Session start/end are deliberately not Events: SessionStart creates/resumes the Episode, SessionEnd Seals it | pinned-by: CaptureServiceTests
C1 23

FILE src/Mimir.Server/Storage/Entities/WisdomVersion.cs total=9 c1=1 c2=8 c3=0 c4=0
C2 3-6 | The full chain is kept forever; deleting the Wisdom (cascade) is the only remover | pinned-by: ProvenanceDeletionTests
C2 21-24 | Distilled marks version 1 of gate-born Wisdom from both entry roads (Distiller and harvest) | pinned-by: MergeGateTests + HarvestConverterTests
C1 11

FILE src/Mimir.Server/Storage/Entities/GoldenCase.cs total=9 c1=2 c2=7 c3=0 c4=0
C2 3-7 | A case is grown by promoting marked injections or hand-inserted, consumed only by the dev-time golden runner | pinned-by: InjectionBrowserTests + GoldenRunnerTests
C2 12 | QueryContext is replayed through the §7 query ranking | pinned-by: GoldenRunnerTests
C2 15 | ProjectId is the affinity context the runner ranks under — never a scope filter | pinned-by: GoldenRunnerTests.CasesRankUnderTheirOwnAffinityContext
C1 20, 23

FILE src/Mimir.Server/Storage/Entities/Provenance.cs total=6 c1=0 c2=6 c3=0 c4=0
C2 3-8 | One source per row, unioned on merge; Event/Episode hard-delete cascades these away (the sole remover) while the Wisdom survives orphaned | pinned-by: ProvenanceDeletionTests + MergeGateTests

FILE src/Mimir.Server/Storage/Migrations/20260719091040_InitialSchema.cs total=5 c1=0 c2=0 c3=2 c4=3
C3 20-21 | Down must drop the vector extension by hand — EF leaves an extension-only Up's Down empty, stranding the extension on revert (hand edit lost on regeneration) | pinnable-by: doc-only
C4 7, 10, 17 | <inheritdoc /> chain
FILE src/Mimir.Server/Storage/Migrations/20260719091040_InitialSchema.Designer.cs total=4 c1=0 c2=0 c3=0 c4=4
FILE src/Mimir.Server/Storage/Migrations/20260720020817_CaptureSchema.cs total=3 c1=0 c2=0 c3=0 c4=3
FILE src/Mimir.Server/Storage/Migrations/20260720020817_CaptureSchema.Designer.cs total=4 c1=0 c2=0 c3=0 c4=4
FILE src/Mimir.Server/Storage/Migrations/20260720054514_HarvestSchema.cs total=3 c1=0 c2=0 c3=0 c4=3
FILE src/Mimir.Server/Storage/Migrations/20260720054514_HarvestSchema.Designer.cs total=4 c1=0 c2=0 c3=0 c4=4
FILE src/Mimir.Server/Storage/Migrations/20260721000044_WisdomSchema.cs total=3 c1=0 c2=0 c3=0 c4=3
FILE src/Mimir.Server/Storage/Migrations/20260721000044_WisdomSchema.Designer.cs total=4 c1=0 c2=0 c3=0 c4=4
FILE src/Mimir.Server/Storage/Migrations/20260722051921_InjectionSchema.cs total=3 c1=0 c2=0 c3=0 c4=3
FILE src/Mimir.Server/Storage/Migrations/20260722051921_InjectionSchema.Designer.cs total=4 c1=0 c2=0 c3=0 c4=4
FILE src/Mimir.Server/Storage/Migrations/20260722051926_DistillerSchema.cs total=3 c1=0 c2=0 c3=0 c4=3
FILE src/Mimir.Server/Storage/Migrations/20260722051926_DistillerSchema.Designer.cs total=4 c1=0 c2=0 c3=0 c4=4
FILE src/Mimir.Server/Storage/Migrations/20260722220609_GoldenSchema.cs total=3 c1=0 c2=0 c3=0 c4=3
FILE src/Mimir.Server/Storage/Migrations/20260722220609_GoldenSchema.Designer.cs total=4 c1=0 c2=0 c3=0 c4=4
FILE src/Mimir.Server/Storage/Migrations/MimirDbContextModelSnapshot.cs total=3 c1=0 c2=0 c3=0 c4=3
(Designer/snapshot C4 lines are each: auto-generated header, `<inheritdoc />`, `#pragma warning disable/restore 612, 618`.)

### src/Mimir.Server/Recall

FILE src/Mimir.Server/Recall/InjectionLog.cs total=58 c1=17 c2=38 c3=3 c4=0
C2 20 | QueryContext is the prompt for Prompt, the tool query for MCP, null for the Brief | pinned-by: BriefServiceTests.Brief_LogsOneInjectionRow_WithTheItemsAndSizeItInjected, PromptRecallServiceTests.OnTopicPrompt_InjectsLabeledWisdomWithinBudget_AndLogsTheInjection, McpSearchServiceTests.FusedResults_...
C2 21-26 | ProjectId is per-lane: session's Project for Brief/Prompt, affinity Project for MCP (not any Project in the answer) | pinned-by: the same three tests' ProjectId asserts
C2 33-53 | InjectionLog is the one keeper of the §7 empty-trace rule (off `included` for ambient, off `text` for RecordAsync), the clock, and the save | pinned-by: InjectionLogTests (whole class)
C2 62-63 | The notice is reserved out of the budget before entries are measured | pinned-by: InjectionWrapperTests.Build_ReservesANoticeOutOfTheBudget + BriefServiceTests.Brief_WarningLine_IsBoughtFromTheWisdomBudget_NotAddedToIt
C2 64 | Returns "" when nothing rendered at all | pinned-by: InjectionLogTests.RenderAndRecord_WithNothingIncludable_ReturnsEmpty_AndLogsNothing
C2 74-75 | Emptiness reads off `included`, never off text — a notice-only render is not an injection | pinned-by: InjectionLogTests.RenderAndRecord_WithANoticeAndNoEntries_ReturnsIt_ButLogsNothing
C2 89-90 | An answer that found only Episodes (empty included) is still an injection | pinned-by: InjectionLogTests.Record_WithAnAnswerCarryingNoWisdom_StillLogsARow
C2 97-99 | RecordAsync reads emptiness off the text: empty answer, no row | pinned-by: InjectionLogTests.Record_WithAnEmptyAnswer_LogsNothing
C3 128-130 | Recall stages nothing else on the shared scoped context by save time; a lane that grows staged work must move off it | pinnable-by: doc-only
C1 6, 15-19, 56-61, 84-88

FILE src/Mimir.Server/Recall/BriefTripwire.cs total=41 c1=4 c2=22 c3=15 c4=0
C2 5-9 | A compose crossing either threshold fires both channels (log + in-Brief line) | pinned-by: BriefTripwireTests (whole class) + BriefServiceTests.Brief_ComposedPastTheTimeThreshold_CarriesTheWarning_AndLogsIt
C2 21-25 | Wall-clock threshold fires on exceed, not reach | pinned-by: BriefTripwireTests.Fire_AtTheWallClockThreshold_FiresNeitherChannel / Fire_OneTickPastTheWallClockThreshold_FiresBothChannels
C2 28-32 | Size threshold likewise exceeded, so the warning fires on fast machines too | pinned-by: BriefTripwireTests.Fire_InsideBothThresholds_FiresNeitherChannel / Fire_OneRowPastTheSizeThreshold_FiresBothChannels_EvenWhenTheComposeWasInstant
C2 43-49 | Inside both thresholds: no log and Brief byte-for-byte unchanged; crossing one: line goes out even for an empty Brief | pinned-by: BriefServiceTests.Brief_ComposedInsideBothTripwireThresholds_CarriesNoWarning_AndLogsNone / SlowEmptyBrief_StillCarriesTheWarning_ButLogsNoInjection
C3 10-18 | The in-Brief line is the only non-Wisdom content any recall surface volunteers (deliberate, glossary-admitted purity violation; cap degrades to empty Brief) | pinnable-by: doc-only
C3 35-40 | HookCap "3s" restates HookCommand.Cap (unreferenceable from here); drift stales the sentence, never the thresholds | pinnable-by: doc-only
C1 50-53

FILE src/Mimir.Server/Recall/QueryRanking.cs total=35 c1=2 c2=26 c3=7 c4=0
C2 11-16 | Cosine is null off the vector leg; no eligibility annotation — the universe is named by the method, so every row is inside it | pinned-by: QueryRankingTests.Unthresholded_EveryHitRanks_WithTheVectorLegsCosineRidingAlong / TheAmbientUniverse_HoldsGlobalAndTheSessionsOwn_NotAnotherProjects
C2 31-38 | Ranking is unthresholded (consumers own gates) and each method names/restricts the universe it ranks | pinned-by: QueryRankingTests (whole class)
C2 46-52 | Ambient universe restricts inside both legs before the per-leg top-N, so a foreign corpus can't crowd an eligible match out | pinned-by: QueryRankingTests.TheAmbientUniverse_SurvivesANearerForeignCorpus_FillingThePerLegTopN
C2 68-69 | Affinity is caller input (requester's / case's Project) | pinned-by: QueryRankingTests.AffinityIsCallerInput_AnotherProjectsContextLeavesTheRowUnboosted
C2 70-72 | Filters are pushed into the search SQL before the per-leg top-N | pinned-by: McpSearchServiceTests.AFilter_FindsMatchesTheUnfilteredTopNWouldHaveCrowdedOut
C3 64-67 | No unfiltered overload: reaching past the ambient universe must be stated (WisdomSearchFilter.None), never defaulted | pinnable-by: doc-only
C3 117-118 | A Wisdom hard-deleted between search and hydration drops out; consumers never meet a dangling id | pinnable-by: doc-only (window unforceable without an interceptor)
C3 128 | Global Wisdom never earns affinity, even when the affinity context is Global | pinnable-by: plain test (rank with affinityProjectId = Project.GlobalId over a Global row)
C1 62-63

FILE src/Mimir.Server/Recall/RecallScoring.cs total=20 c1=5 c2=11 c3=4 c4=0
C2 14 | Recency = max(floor, 0.5^(days/half_life)) per §7 | pinned-by: RecallScoringTests.Recency_IsOneWhenJustConfirmed / Recency_HalvesAtTheHalfLife / Recency_NeverDecaysBelowTheFloor
C2 21-24 | brief_score = recency × salience × (1 + log₂(1 + reinforcement)) | pinned-by: RecallScoringTests.BriefScore_* tests
C2 35-40 | QueryScore = fused × affinity × recency × salience × (1 + ln(1+r)/10), damping gentler than the Brief's | pinned-by: RecallScoringTests.QueryScore_* tests
C3 9-10 | The §8.3 surface (Ui.InjectionDisplay.Formula) restates both expressions in words — change either shape here and change it there | pinnable-by: doc-only (each end pinned separately; nothing pins their agreement)
C3 41-42 | projectAffinity is never true for Global Wisdom | pinnable-by: plain test
C1 5-8, 11

FILE src/Mimir.Server/Recall/InjectionWrapper.cs total=26 c1=2 c2=13 c3=11 c4=0
C2 5-8 | Header disclaims instruction authority; one label line per Wisdom, filled to the char budget in caller's order | pinned-by: InjectionWrapperTests.Build_WrapsEntriesInAHeaderThatDisclaimsInstructionAuthority / Build_TagsEachEntryWithKindScopeAndLastConfirmed / Build_StaysWithinTheBudget_AndReportsWhatItIncluded
C2 30-32 | Notice reserved out of the budget before entries are measured | pinned-by: InjectionWrapperTests.Build_ReservesANoticeOutOfTheBudget_AndPlacesItAfterTheEntries
C2 33-35 | ("", []) when nothing rendered; an oversized entry is skipped, never ends the fill | pinned-by: InjectionWrapperTests.Build_IsEmptyForNoEntries / Build_SkipsAnOversizedEntry_AndKeepsFillingWithLaterOnes
C2 56-58 | A notice alone still earns a wrapper — but only one the budget can hold | pinned-by: InjectionWrapperTests.Build_WithANoticeAndNoEntries_StillCarriesTheNotice / Build_WithANoticeTooLargeForTheBudget_StaysEmpty
C3 9-13 | Must stay pure/Postgres-free so the budget pins run on machines without Docker | pinnable-by: doc-only
C3 14-19 | Not a lane-facing seam: InjectionLog is the only write-path caller (a lane recording itself would apply the wrong empty-trace shape) | pinnable-by: doc-only
C1 28-29

FILE src/Mimir.Server/Recall/McpSearchService.cs total=25 c1=1 c2=17 c3=7 c4=0
C2 9-12, 15-16 | Reaches other Projects' Wisdom, Retired only on request; records via InjectionLog (lane=MCP, query as query_context, affinity Project) | pinned-by: McpSearchServiceTests.FusedResults_... / RetiredWisdom_SurfacesOnlyWithIncludeRetired_AndIsMarked
C2 17-20 | No-recall replies (unknown kind, filter miss, no matches) return before the keeper and leave no trace | pinned-by: McpSearchServiceTests.NoMatches_AnswersPlainly_AndLogsNothing / AnUnknownKind_NamesTheVocabulary
C2 62-63 | Both legs filter in SQL before their LIMIT | pinned-by: McpSearchServiceTests.AFilter_FindsMatchesTheUnfilteredTopNWouldHaveCrowdedOut
C2 92-93 | The row records the affinity Project, not any Project in the answer | pinned-by: McpSearchServiceTests.FusedResults_...
C2 118-120 | Shared §7 label line; the Retired date reads from the same builder so one line can't carry two date rules | pinned-by: McpSearchServiceTests.RetiredWisdom_... + InjectionLabelTests.Line_CarriesTheCallersExtraTag_InsideTheBracket
C3 13-14 | The two legs' scores are incommensurable, so results are two ranked sections, never one interleaved list | pinnable-by: plain test
C3 27 | Deliberate recall renders at most 10 Wisdom / 10 Event hits, not the §3 top-50 pool | pinnable-by: plain test
C3 53 | Unknown directory → Global anchor as affinity Project (which earns no boost) | pinnable-by: plain test
C3 58-59 | Since is normalized to UTC because Npgsql refuses a non-UTC DateTimeOffset against timestamptz and the endpoint is open to any local client | pinnable-by: plain test
C3 129 | Episode hits are grouped per Episode in first-hit order, best-ranked Episode leading | pinnable-by: plain test
C1 148

FILE src/Mimir.Server/Recall/McpRememberService.cs total=24 c1=0 c2=24 c3=0 c4=0
C2 11-17 | Binds salient to the most recently active unsealed Episode; else straight to the Merge Gate; Project resolved-or-created — a deliberate save is never dropped | pinned-by: McpRememberServiceTests (whole class)
C2 39-40 | "Most recently active" = last Event, else start | pinned-by: McpRememberServiceTests.LandsSalient_OnTheMostRecentlyActiveUnsealedEpisode
C2 58-59 | Server-composed payload is verbatim — §4 truncation never applies to a deliberate save | pinned-by: McpRememberServiceTests.LongContent_IsStoredVerbatim_NeverTruncated
C2 67-79 | The no-Episode path admits through the gate on CancellationToken.None so a caller giving up cannot roll the save back | pinned-by: McpRememberServiceTests.WithNoUnsealedEpisode_TheContentGoesThroughTheMergeGate / ACallerGivingUpMidAdmission_StillLandsTheSave

FILE src/Mimir.Server/Recall/BriefService.cs total=24 c1=0 c2=16 c3=8 c4=0
C2 9-18 | Brief ranks the ambient universe by brief_score, delegates rendering/recording to InjectionLog, and every compose self-measures against the tripwire | pinned-by: BriefServiceTests (whole class)
C2 68-69 | Elapsed is measured before rendering, and the count quoted is this compose's own | pinned-by: BriefServiceTests.Brief_ComposedPastTheTimeThreshold_CarriesTheWarning_AndLogsIt
C2 72-75 | A tripwire line still comes back out of a Brief with no Wisdom to log | pinned-by: BriefServiceTests.SlowEmptyBrief_StillCarriesTheWarning_ButLogsNoInjection
C3 33-40 | Hydration re-asserts RetiredAt == null at the last read before rendering (retire-between-list-and-hydrate window; hard-delete drops silently) | pinnable-by: doc-only (window unforceable without an interceptor — CLAUDE.md's defense-in-depth item)

FILE src/Mimir.Server/Recall/InjectionLabel.cs total=22 c1=0 c2=18 c3=4 c4=0
C2 6-19 | One producer of the §7 label line; scope/extra are the caller's, the shape and UTC-date rule are not | pinned-by: InjectionLabelTests (whole class)
C2 26-28 | Scope in caller's words, extra inside the bracket, newline included in the returned line | pinned-by: InjectionLabelTests
C2 37 | A label's date is the UTC calendar day, whatever offset the value carries | pinned-by: InjectionLabelTests.Date_IsTheUtcCalendarDay_WhateverOffsetTheValueCarries
C3 20-23 | Deliberately its own date rule, not McpTexts.Date's — a change to MCP wording must not rewrite what a Brief injects | pinnable-by: doc-only

FILE src/Mimir.Server/Recall/PromptRecallService.cs total=16 c1=0 c2=14 c3=2 c4=0
C2 7-14 | Prompt lane query-ranks the ambient universe and injects only when the best match reaches the cosine threshold (cosine, never fused) | pinned-by: PromptRecallServiceTests (whole class)
C2 23-25 | The ranking names the universe, so an ineligible match can't reach the gate and no foreign corpus crowds an eligible one out | pinned-by: PromptRecallServiceTests.AnotherProjectsWisdom_NeverOpensTheGate_NorInjects + QueryRankingTests
C2 28-30 | The lifted >= holds the gate shut for a below-threshold cosine, null, and NaN | pinned-by: PromptRecallServiceTests.TheGateReadsCosine_ATopFusedRankBelowTheGateStaysShut / ZeroNormEmbeddingsNaNCosine_NeverOpensTheGate
C3 36-37 | With the gate open but no entry fitting the budget, "" out equals the empty injection of an unopened gate (this lane raises no notice) | pinnable-by: plain test
C1 (none)

FILE src/Mimir.Server/Recall/McpProjects.cs total=11 c1=1 c2=6 c3=4 c4=0
C2 11-12 | The filter resolves by display name or identity, and a miss offers the known names back | pinned-by: McpSearchServiceTests.ProjectFilter_NarrowsBothLegs_AndAMissNamesTheKnownProjects + McpTimelineServiceTests.AnUnknownProject_NamesTheKnownOnes
C2 21-24 | No argument matches nothing and misses nothing; a miss carries the answer text | pinned-by: McpSearchServiceTests.ProjectFilter_NarrowsBothLegs
C3 7-10 | The requester lookup mirrors §3.1 matching but never creates a Project — an unknown directory just earns no affinity | pinnable-by: plain test
C1 51

FILE src/Mimir.Server/Recall/McpTimelineService.cs total=7 c1=0 c2=3 c3=4 c4=0
C2 8-10 | Timeline lists newest first with each entry's seal state, every Project unless narrowed | pinned-by: McpTimelineServiceTests.Timeline_ListsNewestFirst_WithSealState / ProjectAndSinceFilters_NarrowTheTimeline
C3 11-12 | Nothing here is Wisdom, so a timeline call never logs an Injection row | pinnable-by: plain test
C3 25-26 | Since normalized to UTC (Npgsql refuses non-UTC DateTimeOffset against timestamptz) | pinnable-by: plain test

FILE src/Mimir.Server/Recall/McpTexts.cs total=7 c1=1 c2=1 c3=5 c4=0
C2 15 | Seal state renders live, or sealed with its §4 reason | pinned-by: McpTimelineServiceTests.Timeline_ListsNewestFirst_WithSealState
C3 19-23 | Injection label lines never read this Date — a change here cannot rewrite what a Brief injects | pinnable-by: doc-only (mirror of InjectionLabel 20-23)
C1 6

FILE src/Mimir.Server/Recall/McpEndpoints.cs total=5 c1=0 c2=0 c3=5 c4=0
C3 5-9 | Unlike the fail-open hook routes, MCP route errors surface — an honest error beats a silent empty answer | pinnable-by: doc-only

FILE src/Mimir.Server/Recall/ExplicitSalience.cs total=5 c1=0 c2=5 c3=0 c4=0
C2 5-9 | A Wisdom is salient when any Provenance Event was a deliberate save; one definition so the boost never diverges between lanes | pinned-by: BriefServiceTests.Brief_RanksSalientWisdomAboveAnOtherwiseEqualOne + QueryRankingTests.SalientProvenance_OutranksANearerPlainRow

### src/Mimir.Server/Components

FILE src/Mimir.Server/Components/Wisdom/WisdomSurface.razor total=164 c1=5 c2=5 c3=154 c4=0 (salvaged, verified)
C2 173-175 | edit-explanation sentence lives in WisdomDisplay | pinned-by: WisdomDisplayTests.TheEditorExplains_WhatTheGateWillDo_InTheGatesOwnTerms
C2 667-668 | edit re-embeds new text (§8.1) | pinned-by: WisdomBrowserTests.Editing_AppendsAnEditedVersion_AndReEmbedsTheNewText
C3 (all pinnable-by bUnit render test unless noted): 10-15 one component both routes; 23-25 header shows both figures; 205-207 chain view outlives selection; 227-232 draft passed raw beside Save's text; 236-240 @key on version rows (head-growth); 265-270 explicit role=deletion/insertion for screen readers; 303-308 Delete absent while editor open; 320-328 legend needs SelectedId AND _detail; 336-338 legend one expression (Razor trims whitespace); 346-348 aside only beside a Wisdom; 415-416 provenance span not anchor (no §8.2 surface); 448-453 DetailMode post-#106; 459-465 _chain diffed once at read; 475-480 _failure is (Subject,Text) pair; 483-488 _chainView not a URL param; 503-508 EpisodeHref via EpisodeDisplay.EventAnchorHref [plain test]; 513-514 heading+empty-text one switch [plain test]; 531-533 search debouncer takes no ceiling; 536-539 claim before subscribing, anchor in OnInitialized; 545-548 selection resets mode+failure (#106); 557-559 Project change re-anchors; 568-576 AnchorOnProject release-first re-claim (#108); 586-589 aria-pressed needs string [plain test]; 598-605 reloadDetail:false for detail-invariant triggers; 610-612 Project row read once per universe; 621-623 generation guard; 631-633 detail not nulled on list-only refresh; 661-663 SaveAsync alone may read SelectedId; 685-689 Retire/Unretire take row's Wisdom; 700-705 handler exceptions kept inline (circuit); 725-734 DeleteAsync takes ConfirmDelete's id, re-read list (no feed announce) [feed-silence: plain test]; 744 stay put on failed delete
C1 436, 440, 444, 595, 721

FILE src/Mimir.Server/Components/Episodes/EpisodeSurface.razor total=67 c1=3 c2=2 c3=62 c4=0 (salvaged, verified)
C2 231-232 | no explicit refresh after Event delete (feed subscriber) | pinned-by: EpisodeBrowserTests.DeletingAnEvent_RemovesItAlone_AndAnnouncesTheChange
C3 (bUnit render test unless noted): 13-16 one component both routes (#95); 37-40 aside renders from the branch holding the detail; 52-55 is-prose modifier usage; 64-66 show Seal moment not only span; 96-99 four #86-dropped elements absent not stubbed [doc-only]; 145-149 _loadedId answered-vs-in-flight; 152-157 _requestedId fragment-only renav costs zero queries; 160-166 _failure pair cleared by id mismatch; 177-179 feed refresh debounced WITH ceiling; 194-195 router reuses instance; 209-210 clearing counts as a read (_generation++); 220-221 generation guard; 239-248 DeleteEpisodeAsync takes ConfirmDelete's id, feed publishes; 263-268 hard-delete failures inline
C1 139, 173, 283

FILE src/Mimir.Server/Components/Layout/MainLayout.razor total=64 c1=0 c2=3 c3=61 c4=0
C2 84-86 | A projects/{id} URL always parses to a surface; missing/unrecognised tab folds onto Episodes | pinned-by: ProjectRouteTests.AProjectWithNoTab_DefaultsToEpisodes + AnUnrecognisedTab_FallsBackToEpisodes
C3 11-19 | First run is a state of the same chassis (tab strip gone, body swapped); every region reads one cascaded flag, never the DB | pinnable-by: bUnit render test
C3 50-55 | The cascade carries bool?: null is "no answer yet" and renders the populated chassis without either consumer querying | pinnable-by: bUnit render test
C3 58-63 | IsFirstRun stays null on prerender failure so the circuit pass retries; unknown shows the populated chassis | pinnable-by: bUnit render test
C3 64-69 | IsFirstRun must be [PersistentState] or the interactive pass flashes the populated chassis over the prerendered onboarding body | pinnable-by: bUnit render test
C3 75-80 | Only surface routes get the flush .is-flush body; first run, home and error pages get the plain scrolling body | pinnable-by: bUnit render test
C3 81-83, 87-95 | BodyFit is decided from the URL, not the page, so UnknownProject lands in the flush body and must wear .page-notice | pinnable-by: bUnit render test
C3 114-117 | A failed first-run probe logs and leaves the flag unset (not false) so the interactive pass asks again | pinnable-by: bUnit render test
C3 124-129 | The Episode feed is watched only while first-run shows, un-debounced, and unsubscribed for good the moment it ends | pinnable-by: bUnit render test
C3 143-145 | An Event landing mid-query schedules exactly one follow-up query rather than racing or dropping it | pinnable-by: bUnit render test
C3 169-171 | Recheck exceptions (transient Postgres, torn-down circuit) are logged, never left unobserved from the fire-and-forget callback | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Episodes/EpisodeList.razor total=65 c1=2 c2=8 c3=55 c4=0
C2 9-10 | The list is this Project's Episodes only, newest first | pinned-by: EpisodeBrowserTests.TheList_ShowsOnlyTheProjectsEpisodes_NewestFirst
C2 52-54 | Episode search is word-aware FTS over Event.tsv, never substring | pinned-by: EpisodeBrowserTests.Searching_IsWordAware_NotSubstring
C2 185-187 | A claim starts empty on both edges, and a same-holder stale token still frees the box when disposed | pinned-by: SurfaceSearchTests.ANewClaim_StartsFromAnEmptyTerm_SoNoSurfaceInheritsAnothersSearch + AnEarlierTokenFromTheSameHolder_StillReleases
C3 11-13 | The list grows live over the circuit (no polling) and the claimed header search narrows it while mounted | pinnable-by: bUnit render test
C3 21-23 | Running joins the drawn chips so every state a row can mark is filterable; Done gets no chip or row mark | pinnable-by: bUnit render test
C3 73-74 | The session id (a hash) rides as row tooltip and drill-down heading, never a row line | pinnable-by: bUnit render test
C3 122-130, 133 | Two Debouncer instances: the feed lane carries the ceiling, the search lane must not (a ceiling-armed refresh would query a half-typed term) | pinnable-by: bUnit render test
C3 148-151 | All three SurfaceSearch.Changed edges (claim, keystroke, release) schedule a search-lane refresh | pinnable-by: bUnit render test
C3 158-159 | Feed announcements are coalesced to one refresh per burst, not one per Event | pinnable-by: bUnit render test
C3 167-175 | The refresh key is (ProjectId, IsGlobal), not the id alone, because the page resolves the Project asynchronously and renders once in between | pinnable-by: bUnit render test
C3 181-184 | A Project switch sheds both narrowings (chip filter and search term) made on the outgoing Project | pinnable-by: bUnit render test
C3 197-202 | The switch's direct refresh paints the incoming Project immediately; the two debounced Changed refreshes collapsing later is accepted | pinnable-by: bUnit render test
C3 207-210 | Global's tab holds no search claim at all — the box is handed back rather than offering a search over nothing | pinnable-by: bUnit render test
C3 237-238 | A stale query result is dropped by generation guard rather than overwriting fresher rows | pinnable-by: bUnit render test
C3 257-261 | Live/Failed render as words in their own hue; the queue states render as pills, not alarms | pinnable-by: bUnit render test
C1 95, 115

FILE src/Mimir.Server/Components/Shared/ConfirmDelete.razor total=36 c1=0 c2=14 c3=22 c4=0
C2 35-44 | Selecting another record must disarm the confirmation — disarm-on-subject-change is this component's contract via SubjectKey | pinned-by: ConfirmArmingTests.BindingAnotherRecord_Disarms
C2 67-70 | A click reaching a disarmed confirmation is refused, not assumed armed | pinned-by: ConfirmArmingTests.ConfirmingWhatIsNotArmed_IsRefused
C3 1-4 | This is the one component drawing every §8.2 hard-delete confirmation, so it looks and behaves the same at every host | pinnable-by: bUnit render test
C3 23-28 | Subtle mutes only the resting button (arming is reversible); the armed state is red either way | pinnable-by: bUnit render test
C3 48-59 | OnConfirm carries SubjectKey out so a host never deletes the incoming selection off the outgoing record's prompt | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Shared/ConfirmArming.cs total=33 c1=1 c2=26 c3=6 c4=0
C2 3-10 | The confirmation's whole state lives in this pure companion so the disarm-on-subject-change rule is testable without rendering | pinned-by: ConfirmArmingTests (whole class)
C2 24-29 | Bind disarms on a different subject and is idempotent for an unchanged one (a re-render must not take the consequence away) | pinned-by: ConfirmArmingTests.BindingAnotherRecord_Disarms + RebindingTheSameRecord_StaysArmed
C2 45-56 | TryConfirm consumes the arming — true exactly once per Arm, false for a stale click delivered after Bind repointed | pinned-by: ConfirmArmingTests.ConfirmingWhatIsArmed_SucceedsExactlyOnce + ConfirmingAfterTheSubjectMoved_IsRefused
C3 13-18 | _subject is deliberately non-nullable: nothing can arm before the first Bind, so "never bound" and "disarmed" are one state | pinnable-by: doc-only
C1 21

FILE src/Mimir.Server/Components/Injections/InjectionLogTab.razor total=34 c1=0 c2=4 c3=30 c4=0
C2 14 | The §9 mark is per entry, never per line | pinned-by: InjectionBrowserTests.Marking_SticksWithVerdictAt_AndRemarkingSwitches
C2 21-22 | The head count is the query's matching population; the aside carries the whole-Project figure | pinned-by: InjectionBrowserTests.AFilteredListingThatFillsTheBound_CountsWhatMatched_NotTheWholeProject
C2 252 | SurfaceSearch.Claim resets the term on the claiming edge and nothing else does while the instance lives | pinned-by: SurfaceSearchTests.ANewClaim_StartsFromAnEmptyTerm_SoNoSurfaceInheritsAnothersSearch
C3 12-13 | The §8.3 surface is the four-pane shape: session-grouped list, received payload, §9 figures down the side | pinnable-by: bUnit render test
C3 222-225 | The search lane is debounced without a ceiling, unlike the feed-driven lanes | pinnable-by: bUnit render test
C3 228-231 | The claim anchors in OnInitialized (parameters already set), so the boundary re-anchor fires only on a real Project switch | pinnable-by: bUnit render test
C3 238-241 | A Project switch resets lane chip and selection; the reused instance makes OnParametersSetAsync the only place that can | pinnable-by: bUnit render test
C3 250-251, 253-256 | This tab must release then re-claim on a Project switch or a carried term silently narrows the incoming log (#108) | pinnable-by: bUnit render test
C3 299-301 | A stale listing query is dropped by generation guard rather than showing a filter the chips no longer claim | pinnable-by: bUnit render test
C3 309-311 | The selection is re-resolved by id after every refresh, and drops when filtered or truncated out | pinnable-by: bUnit render test
C3 316-319 | Debounced search arrives outside any event handler, so StateHasChanged must be explicit or the screen never moves | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Episodes/EpisodeDrillDown.razor total=38 c1=0 c2=4 c3=34 c4=0
C2 74-76 | The Event anchor id's writer and reader take one spelling from EpisodeDisplay | pinned-by: EpisodeDisplayTests.TheAnchorTheLinkWrites_IsTheOneTheStreamCarries_AndTheReaderOpens
C2 114 | Hard-deleting the Episode takes its Events with it | pinned-by: EpisodeBrowserTests.DeletingAnEpisode_TakesItsEventsWithIt_AndAnnouncesTheChange
C3 6-9 | The drill-down reads produced-then-stream in arrival order, and a long stream is bounded on arrival and says so | pinnable-by: bUnit render test
C3 31-32 | Produced-Wisdom links target the browsed Project's surface, never the Wisdom's own Scope | pinnable-by: bUnit render test
C3 115 | The whole-Episode delete sits apart (danger zone) from the per-Event deletes | pinnable-by: bUnit render test
C3 138-142 | Component named for the drill-down because a component named EpisodeDetail would shadow the Ui record in its own file | pinnable-by: doc-only
C3 146-149 | ProjectId parameter is the hosting surface's Project, never a produced Wisdom's Scope | pinnable-by: doc-only
C3 159-162 | A failed delete is said under the button that failed; the surface owns the text, this component owns the placement | pinnable-by: bUnit render test
C3 168-172 | The expansion belongs to the (EpisodeId, anchored Event) pair, re-read per navigation since a second Provenance link may name a deeper Event | pinnable-by: bUnit render test
C3 177-180 | The stream shows the first StreamBound Events until the curator expands it | pinnable-by: bUnit render test
C3 187-191 | Expansion is decided afresh only on a new (record, anchor) key — never reset by the feed's re-renders under a live session | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Layout/AppHeader.razor total=35 c1=0 c2=2 c3=33 c4=0
C2 103-104 | A search-box handover empties the term, dropping the outgoing surface's search | pinned-by: SurfaceSearchTests.ReleasingAClaim_ClearsTheTerm_SoTheNextSurfaceOpensUnfiltered + ANewClaim_StartsFromAnEmptyTerm
C3 10-15 | The header answers whole-install; on first run the pipeline and pull chip swap rather than sit side by side (1380px floor) | pinnable-by: bUnit render test
C3 22-25 | The one search box narrows the on-screen surface's list; unclaimed it renders disabled and says so | pinnable-by: bUnit render test
C3 81-86 | Null cascade is a real third state: the five-query pipeline is never fetched before first-run is known false | pinnable-by: bUnit render test
C3 97-98 | The header's feed debounce is ceilinged (one of the two unfiltered subscribers) | pinnable-by: bUnit render test
C3 102 | Claim/release changes this input's state, so the header re-renders on Changed | pinnable-by: bUnit render test
C3 107-110 | Feed bursts are coalesced to one five-query refresh, not one per Event | pinnable-by: bUnit render test
C3 114-116 | The cascade-set fetch is both the initial fetch and the one that ends first run — the pipeline is first asked for then | pinnable-by: bUnit render test
C3 127-130 | No pipeline query runs while first-run holds, checked before the generation is taken so a discarded refresh can't invalidate a real one | pinnable-by: bUnit render test
C3 140-142 | Overlapping pipeline queries resolve by generation guard — whichever returns second must not win | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/FirstRun/FirstRunCommands.cs total=24 c1=0 c2=24 c3=0 c4=0
C2 3-9 | One keeper for both registrations; Both is what the clipboard receives; pure so its pins run with no Postgres | pinned-by: FirstRunCommandsTests (whole class)
C2 10-13 | README states the same two registrations — change one, change the other | pinned-by: FirstRunCommandsTests.TheRegistrations_AreTheOnesTheReadmeStates
C2 16-22 | All five §4 hooks are registered; only the fire-and-forget three carry async | pinned-by: FirstRunCommandsTests.TheHookBlock_RegistersEverySpec4Hook + OnlyTheThreeFireAndForgetHooks_AreAsync + TheSynchronousHooks_CarryNoAsyncFlagAtAll
C2 45 | The MCP server registers once at user scope | pinned-by: FirstRunCommandsTests.TheMcpRegistration_IsUserScoped
C2 48-52 | Both is one clipboard payload with prose saying which half goes where | pinned-by: FirstRunCommandsTests.OneCopy_CarriesBothRegistrations_AndSaysWhichIsWhich

FILE src/Mimir.Server/Components/Wisdom/WisdomRoute.cs total=20 c1=1 c2=14 c3=5 c4=0
C2 6-12 | Lens names are the enum's own lowercased; unknown or missing lands on Active rather than erroring | pinned-by: WisdomRouteTests.AnUnknownLens_LandsOnTheDefaultListing + EveryOtherLens_RidesTheQuery_AndReadsBackAsItself
C2 15 | The lens rides the "show" query-string key | pinned-by: WisdomRouteTests.EveryOtherLens_RidesTheQuery_AndReadsBackAsItself
C2 33-37 | LensOf reads the lens back off a full URL for the route-less sidebar | pinned-by: WisdomRouteTests (incl. TheLensName_IsCaseInsensitive_SoAPastedUrlStillResolves)
C2 42 | The default lens produces no query string at all | pinned-by: WisdomRouteTests.TheDefaultLens_NeedsNoQueryAtAll
C3 22-26 | Detail links carry the browsed Project's id, never the Wisdom's own Scope — following one must not switch the curator to Global's list | pinnable-by: doc-only
C1 18

FILE src/Mimir.Server/Components/Layout/ProjectSidebar.razor total=29 c1=0 c2=1 c3=28 c4=0
C2 11 | Global is pinned on top by the project query's ordering | pinned-by: ChassisBrowserTests.TheSidebar_ListsGlobalFirst_ThenProjectsByName
C3 10, 12-16 | The second group swaps with the active surface read from the URL; on first run it gives way to the "Projects appear on their first hook" note | pinnable-by: bUnit render test
C3 45-49 | Attention rows are links only where the tab body can read the filter back off the URL (Wisdom's lenses); otherwise they stay stat rows | pinnable-by: bUnit render test
C3 118-122 | A null cascade reads as "not first run" here — the replaced group is what renders in every other state anyway | pinnable-by: bUnit render test
C3 137-138 | The sidebar's feed debounce is ceilinged (the other unfiltered subscriber) | pinnable-by: bUnit render test
C3 147-150 | Any capture change may be a new repo's first hook, so the list is re-queried and grows live, coalesced per burst | pinnable-by: bUnit render test
C3 185-186 | Stale refreshes are dropped by generation guard rather than overwriting fresher state | pinnable-by: bUnit render test
C3 208-211 | Each "Needs attention" row links to the Wisdom listing narrowed to that lens | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Layout/ProjectRoute.cs total=17 c1=0 c2=17 c3=0 c4=0
C2 3-8 | One parser for the chassis's projects/{id}/{tab} shape so sidebar and strip read the URL without a cascading parameter | pinned-by: ProjectRouteTests (whole class)
C2 11 | Anything but the three surface tabs — or nothing — lands on Episodes | pinned-by: ProjectRouteTests.AProjectWithNoTab_DefaultsToEpisodes + AnUnrecognisedTab_FallsBackToEpisodes
C2 16-21 | Null off the projects/{guid} route entirely; missing and unrecognised tabs both default | pinned-by: ProjectRouteTests.ARootPath_MatchesNothing + AMalformedProjectId_MatchesNothing
C2 36-39 | Query string and fragment are stripped before segmenting so they cannot corrupt the tab segment | pinned-by: ProjectRouteTests.AQueryString_DoesNotCorruptTheTabSegment + AQueryStringOnTheBareProjectPath_StillMatches

FILE src/Mimir.Server/Components/Health/ModelPull.cs total=16 c1=0 c2=16 c3=0 c4=0
C2 5-12 | Only a Pulling model is a pull; PercentComplete is null when Ollama reports no total, and the chip claims no figure then | pinned-by: ModelPullTests.ModelsThatAreNotPulling_AreNoPull + APullWithNoTotalReported_IsNamedWithoutAPercentage
C2 15-22 | If two models ever pull at once the first the tile lists (§11 order) is named — a pinned decision, not an accident | pinned-by: ModelPullTests.TwoModelsPullingAtOnce_NamesTheFirstTheTileLists

FILE src/Mimir.Server/Components/FirstRun/FirstRunPanel.razor total=16 c1=1 c2=0 c3=15 c4=0
C3 5-8 | First run explains itself inside the same shell, and the rendered registrations are the same FirstRunCommands constants the clipboard receives | pinnable-by: bUnit render test
C3 67-68 | The clipboard module is the port's one piece of JavaScript — the deliberate exception to an all-CSS/Blazor chassis | pinnable-by: doc-only
C3 71-74 | One fallback message covers both failure modes (refused write, call never made) since the commands stay selectable on screen | pinnable-by: bUnit render test
C3 85-87 | An import that lands after DisposeAsync releases its own module, since the panel can be swapped out mid-import | pinnable-by: bUnit render test
C3 102-103 | Clipboard failures log and show the fallback rather than becoming an error boundary | pinnable-by: bUnit render test
C1 124

FILE src/Mimir.Server/Components/Injections/InjectionDetail.razor total=14 c1=0 c2=1 c3=13 c4=0
C2 8 | The mark is the entry's, not any line's | pinned-by: InjectionBrowserTests.Marking_SticksWithVerdictAt_AndRemarkingSwitches
C3 6-7 | The detail reads in the session's order: payload, then per-line score, then the §7 formula, then the §9 mark | pinnable-by: bUnit render test
C3 167-171 | ProjectId for Wisdom links is the browsed surface's Project, never the injected Wisdom's own scope | pinnable-by: doc-only
C3 179-184 | Payload/budget/formula are built once per parameter change — expression-bodied properties rebuilt the wrapper five times per render | pinnable-by: doc-only

FILE src/Mimir.Server/Components/Pages/ProjectPage.razor total=13 c1=0 c2=0 c3=13 c4=0
C3 8-9 | The tab strip lives in the layout; this page renders only the selected tab's body | pinnable-by: bUnit render test
C3 20-24 | Only Injections plus the Episodes default remain here; both draw the pane shape and take the flush body | pinnable-by: bUnit render test
C3 48-53 | Wisdom and Episodes never reach this switch — their pages' literal segments outrank {Tab}; tabless and unrecognised land on Episodes | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Health/HealthAwareComponent.cs total=8 c1=1 c2=0 c3=7 c4=0
C3 7-12 | One base class states the subscribe-render-dispose shape so a circuit-teardown fix lands on both health consumers | pinnable-by: doc-only
C3 27 | Probes push from background threads, so the re-render must hop onto the circuit dispatcher via InvokeAsync | pinnable-by: bUnit render test
C1 21

FILE src/Mimir.Server/Components/Health/HealthPill.razor total=8 c1=0 c2=2 c3=6 c4=0
C2 156-157 | Table emptiness is carried in words, never inferred from the byte figure | pinned-by: StorageTileFactoryTests.TheSummaryNeverInfersEmptinessFromBytes
C3 5-7 | The popover is the native Popover API — light dismiss and Escape come from the platform, toggling never touches the circuit | pinnable-by: bUnit render test
C3 136-138 | The pill's dot never wears the danger hue (reserved for Delete/orphaned Provenance), and only a live claim pulses | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Health/ModelPullChip.razor total=7 c1=0 c2=2 c3=5 c4=0
C2 8-9 | Renders nothing when no model is pulling — Ready, Pending or Failed outright is not a pull; the Health popover is where those are stated | pinned-by: ModelPullTests.ModelsThatAreNotPulling_AreNoPull
C3 3-7 | A sibling of HealthPill (both observe the same probe), mounted only on first run so the header holds no health subscription it would never read for the rest of the install's life | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Pages/EpisodePage.razor total=8 c1=0 c2=0 c3=8 c4=0
C3 7-11 | One screen serves listing and drill-down routes so the surface survives each click; the literal "episodes" segment outranks ProjectPage's {Tab} | pinnable-by: bUnit render test
C3 32 | A null EpisodeId is the listing route — the surface renders its unselected placeholder | pinnable-by: doc-only
C3 41-42 | The Project is re-fetched only when ProjectId changes; a selection re-runs the hook on the same instance | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Pages/WisdomPage.razor total=6 c1=0 c2=0 c3=6 c4=0
C3 6-9 | One screen serves listing and detail routes so every existing deep link resolves; the literal "wisdom" segment outranks {Tab} | pinnable-by: bUnit render test
C3 20 | A null WisdomId is the listing route — the surface renders its unselected placeholder | pinnable-by: doc-only
C3 24 | The sidebar's "Needs attention" links land here through WisdomRoute's lens query | pinnable-by: doc-only

FILE src/Mimir.Server/Components/Layout/SurfaceTabStrip.razor total=7 c1=0 c2=0 c3=7 c4=0
C3 6-10 | The strip reads Project and tab from the URL (no cascading parameter) and renders nothing off the projects/{id} route | pinnable-by: bUnit render test
C3 55-56 | Stale count queries are dropped by generation guard rather than overwriting a fresher Project's counts | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Wisdom/KindGlyph.razor total=3 c1=0 c2=0 c3=3 c4=0
C3 3-5 | Kind is carried by shape, never hue; the glyph is aria-hidden decoration and every host puts the Kind's word beside it | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Pages/UnknownProject.razor total=8 c1=0 c2=0 c3=8 c4=0
C3 1-8 | The unresolvable-Project notice is stated once for both surface pages, and is the one .page-notice rendered inside a flush body (hence its padding/overflow) | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Pages/Home.razor total=2 c1=0 c2=0 c3=2 c4=0
C3 5-6 | A non-surface route draws its own padding via .page-notice out of a body that gives none | pinnable-by: bUnit render test

FILE src/Mimir.Server/Components/Pages/Error.razor total=3 c1=0 c2=0 c3=3 c4=0
C3 6-8 | The error page must not wear the danger hue — the Mimir layer reserves it for Delete and Orphaned; the template's "text-danger" was never defined here | pinnable-by: doc-only

FILE src/Mimir.Server/Components/App.razor total=2 c1=0 c2=0 c3=2 c4=0
C3 17-18 | The whole UI renders Interactive Server — the SignalR circuit is what pushes live health, unsealed Episodes and queue depth without polling | pinnable-by: plain test (file scan, OfflineAssetsTests-style)

### src/Mimir.Server — Capture, Harvest, Evaluation, Modules, Health, Models, Configuration, Program

FILE src/Mimir.Server/Capture/ProjectResolver.cs total=38 c1=0 c2=21 c3=17 c4=0
C2 8-12 | Match by identity, else by known root; create when new; append unseen roots; path-identity upgraded in place, id stable | pinned-by: ProjectResolverTests (whole class)
C2 26-29 | Root append goes via guarded SQL so a concurrent hook's append is never overwritten by this context's stale array | pinned-by: ProjectResolverTests.ARootAppendedByAnotherContext_SurvivesThisContextsAppend
C2 47-48 | A remote identity already owning a Project merges a path-born rival at the reported root (clone merge) | pinned-by: ProjectMergeTests.ACollidingUpgrade_MergesTheClones_RePointingEpisodes
C2 69-70 | A root-matched path-born row reporting a real remote is upgraded | pinned-by: ProjectResolverTests.APathIdentityProject_ReportingARemote_IsUpgradedInPlace
C2 131-137 | Only a path-born Project upgrades; an identity equal to the reported root (§3.1 fallback) upgrades nothing | pinned-by: ProjectResolverTests.APathIdentityProject_SeenAtASecondRootWithoutARemote_KeepsItsPathIdentity + AKnownRootWithADifferentRemoteIdentity_MatchesByRoot_AndKeepsItsStoredIdentity
C2 143 | DisplayName is the last segment of either identity form | pinned-by: ProjectResolverTests.APathIdentityProject_GetsItsDisplayNameFromTheLastSegment
C3 40-43 | The rival probe must run on every identity match, not only when a root was just appended (Harvester-born duplicates) | pinnable-by: plain test
C3 56-57 | An FK violation from a concurrent hook mid-merge rolls the whole merge back and retries | pinnable-by: plain test
C3 71-72 | The upgrade's WHERE guard makes first-upgrade-wins atomic under a racing rival | pinnable-by: plain test
C3 85-86 | An upgrade colliding on the unique identity re-reads and resolves to the surviving Project | pinnable-by: plain test
C3 110 | A lost create race detaches the local row and resumes the winner | pinnable-by: plain test
C3 116-121 | A path-born rival is found even when a remote-identity Project also holds the root (filter across every holder, path-born checked in memory) | pinnable-by: plain test

FILE src/Mimir.Server/Capture/CaptureService.cs total=30 c1=0 c2=18 c3=12 c4=0
C2 12-16 | Episode created/resumed from session id; Events appended in arrival order; capture is dumb (truncation + Seal only) | pinned-by: CaptureServiceTests (whole class)
C2 48-52 | A server-composed Remember payload is stored verbatim — never truncated or dropped | pinned-by: McpRememberServiceTests.LongContent_IsStoredVerbatim_NeverTruncated
C2 103-106 | Seal carries the hook-reported reason; session end is not an Event; duplicate SessionEnd changes nothing | pinned-by: CaptureServiceTests.SessionEnd_SealsWithTheHookReportedReason + TheFirstSealWins_ALateDuplicateChangesNothing
C2 112-113 | A stale-unsealed tracked instance falls through to the guarded update safely | pinned-by: CaptureServiceTests.ASealFromAnotherContext_BeatsAStaleDuplicate
C2 120-121 | The WHERE guard makes first-seal-wins atomic | pinned-by: CaptureServiceTests.ASealFromAnotherContext_BeatsAStaleDuplicate
C3 34-37 | The §4 single round-trip resolves the Episode once, shared between capture and recall | pinnable-by: plain test
C3 97 | A lost per-Episode seq race detaches and takes the next slot | pinnable-by: plain test
C3 122-125 | Sealing is a deliberate queue write: it sets distillation=pending (one of the two writes outside DistillationQueue) | pinnable-by: plain test
C3 174-176 | Episode create race resumes the winner; an FK violation from a concurrent clone merge re-resolves to the survivor | pinnable-by: plain test

FILE src/Mimir.Server/Capture/CaptureEndpoints.cs total=19 c1=0 c2=5 c3=14 c4=0
C2 48-52 | Recall fails open: a successful capture answers an empty injection, never an error | pinned-by: UserPromptEndpointTests.RecallFailure_StillCapturesTheEvent_AndAnswersEmpty
C3 9-13 | Async captures share one route; the two synchronous hooks get dedicated routes because they answer with content | pinnable-by: doc-only
C3 35-37 | SessionEnd fires the harvest and distillation triggers fire-and-forget; sealing never waits on either | pinnable-by: plain test
C3 43 | An unknown hook event is a 400 — the §3 Event enum is closed | pinnable-by: plain test
C3 81-85 | SessionStart (including the source:"compact" re-fire) resumes the session's Episode and answers a fresh Brief | pinnable-by: plain test

FILE src/Mimir.Server/Capture/ProjectMerger.cs total=19 c1=2 c2=11 c3=4 c4=2
C2 6-7, 9-12 | Merge re-points every reference (enumerated from the catalog, not a hand-list), unions roots, removes the loser | pinned-by: ProjectMergeTests.TheMerge_RePointsReferencesFromTablesThisCodeHasNeverHeardOf + ACollidingUpgrade_MergesTheClones_RePointingEpisodes
C2 35-36 | Root union preserves order: survivor's roots first, loser's unseen roots in accumulation order | pinned-by: ProjectMergeTests.TheMerge_KeepsEveryRootOfBothClones
C2 57-59 | Every FK referencing projects comes from the catalog, so tomorrow's tables are covered automatically | pinned-by: ProjectMergeTests.TheMerge_RePointsReferencesFromTablesThisCodeHasNeverHeardOf
C3 8 | The merge is one transaction so a crash leaves both rows intact | pinnable-by: plain test
C3 60-62 | A non-single-column FK (or one not on projects.id) must fail the merge loudly, not silently strand rows | pinnable-by: plain test
C4 25-26 | #pragma EF1002 justification: identifiers arrive pre-quoted by regclass::text/quote_ident, so raw interpolation is injection-safe
C1 93, 96

FILE src/Mimir.Server/Capture/PayloadTruncator.cs total=13 c1=3 c2=8 c3=2 c4=0
C2 11-15 | Oversized fields keep head+tail around the marker; top-level prompt stored in full; Remember-lane payloads bypass the truncator | pinned-by: PayloadTruncatorTests + McpRememberServiceTests.LongContent_IsStoredVerbatim_NeverTruncated
C2 18-19 | Cuts land on character boundaries — truncated payloads stay honest UTF-8 | pinned-by: PayloadTruncatorTests.MultiByteTextIsCutAtCharacterBoundaries_NeverCorrupted
C2 96 | A cut point must not land on a UTF-8 continuation byte | pinned-by: PayloadTruncatorTests.MultiByteTextIsCutAtCharacterBoundaries_NeverCorrupted
C3 16-17 | Assistant messages never arrive on the hook surface — an accepted v1 loss (§4 declines the transcript, ADR-0003) | pinnable-by: doc-only
C1 8, 24-25

FILE src/Mimir.Server/Capture/EpisodeFeed.cs total=11 c1=2 c2=8 c3=0 c4=1
C2 6-10 | Capture publishes after each committed write; circuits re-query; the feed carries only identities — the DB stays source of truth | pinned-by: EpisodeFeedTests (whole class) + CaptureServiceTests feed tests
C2 15 | A subscription observes every change until its handle is disposed | pinned-by: EpisodeFeedTests.ADisposedSubscription_StopsReceiving
C2 34-35 | One dead circuit must not silence the others nor abort the publishing capture | pinned-by: EpisodeFeedTests.AThrowingSubscriber_NeverSilencesTheOthers
C4 19 | <inheritdoc cref="IEpisodeFeed"/>
C1 3, 43

FILE src/Mimir.Server/Capture/HookPayload.cs total=5 c1=0 c2=0 c3=5 c4=0
C3 5-9 | Payload field reads are defensive: absent, null, non-object, or non-string all read as null, never throw (only the absent case is exercised today) | pinnable-by: plain test

FILE src/Mimir.Server/Harvest/HarvestCandidates.cs total=32 c1=2 c2=29 c3=1 c4=0
C2 9-14 | Split on H1/H2 (headingless file = one candidate), hard-capped per candidate, frontmatter type sets the whole file's kind | pinned-by: HarvestCandidatesTests (whole class)
C2 30-31 | The capped text must be non-empty — a cap landing on a surrogate pair emits no candidate | pinned-by: HarvestCandidatesTests.ACapLandingInsideASurrogatePair_NeverEmitsAnEmptyCandidate + BlankSections_ProduceNoCandidates
C2 64-67 | H1/H2 split, deeper headings stay; up to three leading spaces is a heading, four is indented code | pinned-by: HarvestCandidatesTests.H3AndDeeper_StayInsideTheirSection + HeadingsIndentedUpToThreeSpaces_StillSplit + FourSpacesOfIndent_IsACodeBlock_NotAHeading
C2 80-84 | Fences close only on the same delimiter at the opener's length or more; tilde fences also guard headings | pinned-by: HarvestCandidatesTests.ANestedShorterFence_DoesNotCloseTheOuterOne + TildeFences_AlsoGuardHeadings + HeadingsInsideCodeFences_DoNotSplit
C2 123-128 | Only a YAML-mapping-shaped block is frontmatter; a leading horizontal rule or unclosed block is body | pinned-by: HarvestCandidatesTests.AFileOpeningWithAHorizontalRule_IsAllBody_NeverSwallowedAsFrontmatter + UnclosedFrontmatter_IsTreatedAsBody
C2 152-156 | The type key counts wherever it sits (metadata-nested included); unknown or absent falls to Fact | pinned-by: HarvestCandidatesTests.FrontmatterType_MapsToKind + TopLevelFrontmatterType_AlsoMaps
C2 179 | Never cut a surrogate pair — a lone surrogate is not encodable UTF-8 | pinned-by: HarvestCandidatesTests.ACapLandingInsideASurrogatePair_NeverEmitsAnEmptyCandidate
C3 107 | Only a bare fence line (no info string) closes an open fence | pinnable-by: plain test
C1 6, 187

FILE src/Mimir.Server/Harvest/HarvestScanner.cs total=26 c1=0 c2=20 c3=6 c4=0
C2 12-15 | Scan result semantics: Items = files found, Changed = new versions stored, Gone = newly found deleted | pinned-by: HarvestScannerTests (whole class)
C2 18-22 | Every slug/memory/**/*.md content-hashed and versioned; disappeared files get gone_at; first scan is the Backfill, no special mode | pinned-by: HarvestScannerTests (whole class)
C2 35-36 | A missing harvest root refuses to scan rather than marking every item gone | pinned-by: HarvestScannerTests.AMissingHarvestRoot_ThrowsInsteadOfMarkingEverythingGone
C2 61-63 | An unreadable file was still seen — it keeps its state, never fabricated-gone | pinned-by: HarvestScannerTests.AnUnreadableFile_KeepsItsStateInsteadOfGoingGone
C2 142 | Item path is harvest-relative with forward slashes — same identity whatever the mount | pinned-by: HarvestScannerTests.TheBackfill_StoresEveryMemoryFileUnderItsProject
C2 154-158 | Mangling a known root wins exactly (hyphens and all); only an unmatched slug falls back to the demangled guess | pinned-by: HarvestScannerTests.AHyphenatedRoot_ResolvesByRemanglingKnownRoots_NotByGuessingThePath + AnUnknownSlug_CreatesAPathIdentityProjectForItsDemangledRoot
C3 103-108 | Latest-per-path is reduced in memory (no reliable EF translation) and the projection excludes Content | pinnable-by: doc-only

FILE src/Mimir.Server/Harvest/HarvestConverter.cs total=22 c1=0 c2=15 c3=7 c4=0
C2 10-17 | Every converted_at-null version goes through the gate as one batch per item, marker as finalizer — exactly once across restarts | pinned-by: HarvestConverterTests.PendingVersions_FlowThroughTheGateExactlyOnce
C2 25 | Return value counts conversions that went through the gate | pinned-by: HarvestConverterTests.PendingVersions_FlowThroughTheGateExactlyOnce
C2 52-54 | One failing item must not dam the queue; failure resurfaces after the rest convert; the null marker retries it | pinned-by: HarvestConverterTests.AFailingItem_DoesNotBlockTheItemsBehindIt
C2 77-78 | The marker commits with the item's Wisdom or not at all | pinned-by: HarvestConverterTests.AFailingItem_DoesNotBlockTheItemsBehindIt
C2 86 | EF cannot translate a TimeProvider call inside SetProperty (inlining would throw in every converter test) | pinned-by: HarvestConverterTests (runtime translation)
C3 28-30 | Pending items convert oldest-first, and gone_at is deliberately not filtered — deleted files still convert | pinnable-by: plain test
C3 70-72 | The converter's own context stays tracking-free so a failed batch leaves nothing to clear or re-insert | pinnable-by: plain test
C3 79 | The gate's save after each candidate makes near-identical sections of one file merge instead of duplicate | pinnable-by: plain test

FILE src/Mimir.Server/Harvest/HarvesterService.cs total=17 c1=1 c2=10 c3=6 c4=0
C2 8-13 | Scans on boot, on interval, and on trigger; a failed scan degrades the tile and retries soon | pinned-by: HarvesterServiceTests.TheBootScanReportsOnTheHarvesterTile + ASessionEndTrigger_CausesARescanWithoutTheTimer + AFailingScan_DegradesTheTileAndKeepsTheLastGoodFigures
C2 92-95 | Conversion failure degrades the tile without discarding the fresh scan figures; the marker resumes where it stopped | pinned-by: HarvesterServiceTests.AConversionFailure_DegradesTheTile_ButKeepsTheFreshScanFigures
C3 40-41 | When the trigger wins, the pending timer is cancelled rather than leaking one Task.Delay per SessionEnd | pinnable-by: plain test
C3 76-79 | Only shutdown cancellation stops the loop; any other OperationCanceledException is a failed scan to degrade-and-retry (keep the two filters in sync) | pinnable-by: plain test
C1 45

FILE src/Mimir.Server/Harvest/MemorySlug.cs total=13 c1=1 c2=12 c3=0 c4=0
C2 5-11 | Mangling is lossy (hyphens survive), so Mangle-over-known-root is exact and Demangle is only the best-effort fallback | pinned-by: MemorySlugTests (whole class)
C2 18 | Root matching is case-insensitive (Windows paths, drive-letter case) | pinned-by: MemorySlugTests.MatchesRoot_IgnoresDriveLetterCase
C2 38-41 | A mangled dot's doubled separator collapses to a plausible single one | pinned-by: MemorySlugTests.Demangle_CollapsesTheDoubleSeparatorAMangledDotLeaves
C1 17

FILE src/Mimir.Server/Harvest/HarvestScanTrigger.cs total=9 c1=0 c2=8 c3=0 c4=1
C2 5-8 | Every SessionEnd requests an opportunistic scan; the Harvester waits on it alongside its timer | pinned-by: HarvestScanTriggerTests + HarvesterServiceTests.ASessionEndTrigger_CausesARescanWithoutTheTimer
C2 11 | Request never blocks (SessionEnd hook path) | pinned-by: HarvestScanTriggerTests.RequestsWhileNooneWaits_CoalesceIntoOneScan
C2 14 | Wait completes when a request arrived since the last wait | pinned-by: HarvestScanTriggerTests.ARequestBeforeTheWait_CompletesIt + AWaitWithNoRequest_Blocks
C2 22-23 | Capacity one, drop-on-full: N SessionEnds coalesce into one rescan | pinned-by: HarvestScanTriggerTests.RequestsWhileNooneWaits_CoalesceIntoOneScan
C4 18 | <inheritdoc cref="IHarvestScanTrigger"/>

FILE src/Mimir.Server/Evaluation/GoldenRunner.cs total=20 c1=1 c2=17 c3=2 c4=0
C2 9-12 | Rank is 1-based, null when the expected Wisdom never ranked | pinned-by: GoldenRunnerTests.ExpectedWisdomInTopK_Passes_WithItsRank + ExpectedWisdomOffBothLegs_Fails_WithNoRank
C2 26 | Pass rate is 1.0 for an empty suite | pinned-by: GoldenRunnerTests.EmptySuite_PassesVacuously
C2 30-33, 35 | Every case replays through the shared §7 ranking, unthresholded, under its own affinity context, scored against golden-set k | pinned-by: GoldenRunnerTests (whole class)
C2 47-49 | A distinct (query, project) pair embeds and searches once, not per case | pinned-by: GoldenRunnerTests.CasesSharingAQueryAndProject_ReplayOneRanking
C2 58-60 | The runner ranks the whole tier — narrowing to the ambient universe would move the pass rate for non-ranking reasons | pinned-by: GoldenRunnerTests.ExpectedWisdomBelowK_Fails_WithItsActualRank + CasesRankUnderTheirOwnAffinityContext
C2 84 | The lifted <= holds a null rank to a fail | pinned-by: GoldenRunnerTests.ExpectedWisdomOffBothLegs_Fails_WithNoRank
C3 34 | GoldenRunner is dev-time only and deliberately carries no DI registration | pinnable-by: plain test
C3 50 | Cases run sequentially on purpose — runner and ranking share one context | pinnable-by: doc-only
C1 21

FILE src/Mimir.Server/Modules/Modules.cs total=18 c1=0 c2=0 c3=18 c4=0
C3 8-11 | Capture is passive, dumb, and never blocks a session (§4, ADR-0003) | pinnable-by: doc-only
C3 29-32 | Harvest is one-way ingestion from the read-only mount; Mimir never writes back (ADR-0002) | pinnable-by: doc-only
C3 48-51 | The Merge Gate is the single write-time entry point to the Wisdom tier; distillation runs off every hot path (ADR-0004) | pinnable-by: doc-only
C3 56-57 | MergeGate must stay a Singleton holding no scoped state (creates its own context per batch) because the §8 surface outlives request scopes | pinnable-by: plain test
C3 75-78 | The three Recall lanes all fail open | pinnable-by: doc-only (Prompt lane pinned by UserPromptEndpointTests; the module-wide claim is not)

FILE src/Mimir.Server/Modules/IMimirModule.cs total=11 c1=4 c2=0 c3=7 c4=0
C3 3-7 | A module is a boundary seam along the §2 pipeline, not a deployment unit | pinnable-by: doc-only
C3 8-9 | A module owns its services and HTTP surface; nothing else registers on its behalf | pinnable-by: doc-only
C1 10-11 (STALE: "The modules are empty today" — false since the modules were populated; deletable), 14, 17

FILE src/Mimir.Server/Modules/ModuleRegistration.cs total=4 c1=0 c2=0 c3=4 c4=0
C3 3-6 | Adding a pipeline stage means adding a class to this one list and nothing else | pinnable-by: doc-only

FILE src/Mimir.Server/Health/HealthState.cs total=11 c1=1 c2=9 c3=0 c4=1
C2 5-9 | Single source of truth: probes push, circuits subscribe and re-render — §8 live updates without polling | pinned-by: HealthStateTests (whole class)
C2 14 | Update applies the mutation and notifies subscribers | pinned-by: HealthStateTests.Update_NotifiesEverySubscriberWithTheNewSnapshot
C2 17 | A subscription observes every snapshot until disposed | pinned-by: HealthStateTests.DisposedSubscription_StopsReceivingUpdates
C2 52-53 | A dead circuit must not silence the others nor abort the pushing probe | pinned-by: HealthStateTests.AThrowingSubscriber_DoesNotStarveTheOthersOrTheUpdate
C4 21 | <inheritdoc cref="IHealthState"/>
C1 60

FILE src/Mimir.Server/Health/HealthRegistration.cs total=1 c1=1 c2=0 c3=0 c4=0
C1 5

FILE src/Mimir.Server/Models/ModelProvisioner.cs total=16 c1=0 c2=13 c3=3 c4=0
C2 8-13 | Startup provisions the §11 models: wait for Ollama, pull what is missing, narrate on the health strip | pinned-by: ModelProvisionerTests.MissingModels_ArePulled + AnUnreachableOllama_IsRetriedUntilItAnswers + PullProgress_ReachesTheTileAsItArrives
C2 51-52 | The whole strip republishes as pull progress arrives | pinned-by: ModelProvisionerTests.PullProgress_ReachesTheTileAsItArrives
C2 75 | One unusable model must not stop the others; the tile reports which | pinned-by: ModelProvisionerTests.AFailedPull_DegradesTheTileButStillProvisionsTheOthers
C2 133-135 | A terminal failed pull outranks an in-flight one — the tile carries both facts at once | pinned-by: ModelProvisionerTests.AFailedPull_IsSurfacedWhileALaterModelIsStillPulling
C2 152 | Untagged models compare as :latest | pinned-by: ModelProvisionerTests.AnUntaggedModelMatchesItsLatestTag
C3 53-55 | Progress is tracked locally, never read back off the health state — the provisioner is its tile's sole author | pinnable-by: doc-only

FILE src/Mimir.Server/Models/IModelCatalog.cs total=9 c1=2 c2=0 c3=7 c4=0
C3 3-7 | Only startup provisioning uses Ollama's native API; inference goes through the Microsoft.Extensions.AI abstractions | pinnable-by: doc-only
C3 17-18 | PercentComplete is 0-100 once a total size is known, otherwise null | pinnable-by: plain test
C1 10, 13

FILE src/Mimir.Server/Models/ModelRegistration.cs total=7 c1=0 c2=0 c3=7 c4=0
C3 10-14 | Every model call goes through M.E.AI abstractions; OllamaSharp's native API is what enables startup provisioning | pinnable-by: doc-only
C3 18-19 | An OllamaApiClient carries one selected model, so chat and embedding each get their own instance | pinnable-by: plain test

FILE src/Mimir.Server/Models/ModelProvisioningService.cs total=4 c1=0 c2=0 c3=4 c4=0
C3 3-6 | Provisioning runs once at startup, off the critical path, so the UI serves while models download | pinnable-by: plain test

FILE src/Mimir.Server/Models/OllamaModelCatalog.cs total=2 c1=0 c2=0 c3=1 c4=1
C3 31 | Manifest/verify phases carry no byte totals — report no percentage there | pinnable-by: plain test
C4 7 | <inheritdoc cref="IModelCatalog"/>

FILE src/Mimir.Server/Configuration/DistillationOptions.cs total=16 c1=1 c2=15 c3=0 c4=0
C2 11-15 | At-or-above the cosine threshold merges, below becomes new Wisdom; thresholds are cosine, never RRF-fused | pinned-by: MergeGateTests + AppSettingsTests.ShippedDistillationSection
C2 19 | The Contested flag lives ContestedDuration before the sweep clears it | pinned-by: ContestedSweepTests.OnlyFlagsPastTheContestedDuration_AreCleared
C2 22, 26, 30 | Sweep cadence 6h; stale Running reset 1h; crash-Seal 24h | pinned-by: MimirOptionsTests.DistillerKnobs_DefaultToTheSpecd6h24h1hAnd12K
C2 34-39 | Chunk budget is ranged to the distiller's context ceiling — past it would overflow, not chunk | pinned-by: MimirOptionsTests.InvalidDistillerKnobs_FailValidation + DistillerKnobs_Default
C1 6

FILE src/Mimir.Server/Configuration/RecallOptions.cs total=14 c1=4 c2=10 c3=0 c4=0
C2 13, 17 | Brief/Prompt lanes fill to at most their budget chars, wrapper included | pinned-by: MimirOptionsTests.RecallKnobs_DefaultToTheSpecdBriefBudgetAndRankingFactors + BriefServiceTests + PromptRecallServiceTests + InjectionDisplayTests
C2 21-24 | Prompt lane injects only when the best eligible cosine reaches the gate — never compared against fused scores | pinned-by: PromptRecallServiceTests.TheGateReadsCosine_ATopFusedRankBelowTheGateStaysShut
C2 28, 32, 35, 40 | Affinity boost / recency half-life / recency floor / salience boost per §7 | pinned-by: MimirOptionsTests.RecallKnobs_DefaultToTheSpecdBriefBudgetAndRankingFactors + GoldenRunnerTests.CasesRankUnderTheirOwnAffinityContext + InjectionDisplayTests
C1 5-8

FILE src/Mimir.Server/Configuration/HarvestOptions.cs total=13 c1=0 c2=9 c3=4 c4=0
C2 5-8 | First scan of a fresh database is the Backfill — same code path, no knob | pinned-by: HarvestScannerTests.TheBackfill_StoresEveryMemoryFileUnderItsProject
C2 20 | Interval rescans steady-state; SessionEnd scans opportunistically between | pinned-by: MimirOptionsTests.HarvestKnobs_DefaultToTheComposeMountAndTheSpecd5Minutes + HarvesterServiceTests
C2 24-27 | One harvested candidate is hard-capped mechanically; the gate's LLM rewrite is what compacts | pinned-by: HarvestConverterTests.OversizedSections_ArriveAtTheGateCapped + AppSettingsTests.ShippedHarvestSection
C3 13-16 | Root is the read-only bind mount of the host's ~/.claude/projects; on-host runs point it at that directory | pinnable-by: doc-only

FILE src/Mimir.Server/Configuration/CaptureOptions.cs total=12 c1=0 c2=12 c3=0 c4=0
C2 5-12 | 4KB cap kept as 3KB head + 1KB tail, loss deliberate, original size always recorded; cap decides when, head/tail what survives | pinned-by: MimirOptionsTests.PayloadCapKnobs_DefaultToTheSpecd4K3K1K + PayloadTruncatorTests (whole class)
C2 17, 22, 26 | At-or-under-cap stored whole; head bytes survive the start; tail bytes survive the end | pinned-by: PayloadTruncatorTests
C2 31 | The head+tail check sums as long so near-int.MaxValue knobs cannot wrap around it | pinned-by: MimirOptionsTests.HeadPlusTailBeyondTheCap_FailsValidation

FILE src/Mimir.Server/Configuration/MimirOptionsRegistration.cs total=9 c1=0 c2=3 c3=6 c4=0
C2 6-8 | The §11 knob table binds in one place; defaults are property initialisers validated by data annotations | pinned-by: MimirOptionsTests (whole class)
C3 9-10 | Options are validated at startup so a bad knob fails the boot rather than surfacing later (dropping ValidateOnStart leaves every current test green — they validate on access) | pinnable-by: plain test
C3 11-14 | A new §11 section is one options class plus one AddSection line, nothing else | pinnable-by: doc-only

FILE src/Mimir.Server/Configuration/ModelOptions.cs total=8 c1=0 c2=4 c3=4 c4=0
C2 13 | Endpoint defaults to the Compose service name | pinned-by: AppSettingsTests.ShippedModelsSection_MatchesTheCodeDefaults
C2 17, 21 | Distiller qwen3:8b; Embedding qwen3-embedding:0.6b at 1024 dims | pinned-by: MimirOptionsTests.Models_DefaultToTheSpecdQwen3Pair
C2 28 | Provisioned is every model pulled on startup | pinned-by: ModelProvisionerTests.MissingModels_ArePulled
C3 5-8 | All model access goes through M.E.AI backed by OllamaSharp; these names are what provisioning pulls | pinnable-by: doc-only

FILE src/Mimir.Server/Configuration/ServerOptions.cs total=7 c1=0 c2=0 c3=7 c4=0
C3 5-7 | The service is localhost only (§11/§12 trust boundary) | pinnable-by: doc-only
C3 12-15 | The Port knob applies only when ASPNETCORE_URLS is unset; under Compose the container listens on 8080 and the host publishes this port | pinnable-by: plain test

FILE src/Mimir.Server/Configuration/SearchOptions.cs total=7 c1=1 c2=2 c3=4 c4=0
C2 17 | PerLegTopN caps each leg's candidates before fusion | pinned-by: GoldenRunnerTests.ExpectedWisdomOffBothLegs_Fails_WithNoRank
C2 21 | A GoldenCase passes when its expected Wisdom ranks within GoldenSetK | pinned-by: GoldenRunnerTests
C3 5-8 | Fused RRF scores order candidates and nothing else — every threshold in the system is a cosine, never a fused score (the §3 score-scale invariant, stated system-wide but pinned only per-site) | pinnable-by: doc-only
C1 14

FILE src/Mimir.Server/Program.cs total=3 c1=0 c2=0 c3=3 c4=0
C3 22-24 | Default binding is 127.0.0.1 on the configured port; explicit ASPNETCORE_URLS wins; no HTTPS/auth — localhost is the v1 trust boundary | pinnable-by: plain test

### src/Mimir.Cli and src/Mimir.Contracts

FILE src/Mimir.Cli/McpServer.cs total=33 c1=0 c2=22 c3=11 c4=0
C2 13-15 | Reply text relayed verbatim; a dead Mimir answers an honest MCP tool error, never silence | pinned-by: McpServerTests.ADeadServer_AnswersAnHonestToolError_NotSilence
C2 26-30 | initialize always answers the one served protocol version (2025-06-18), never an echo, because earlier revisions allow batching | pinned-by: McpServerTests.Initialize_AnswersTheOneServedProtocolVersion_NeverAnEcho
C2 33 | Client-side kind enum must be the §3 four | pinned-by: McpServerTests.ARememberCall_WithAnUnknownKind_FailsClientSide_WithoutAnyPost
C2 94-95 | A valid-JSON non-object line answers -32600 with null id, not a crash | pinned-by: McpServerTests.AValidJsonNonObjectLine_AnswersInvalidRequest_AndTheLoopSurvives
C2 112 | A notification (no id) gets no response, even for unknown methods | pinned-by: McpServerTests.UnknownMethodsAndGarbage_AnswerJsonRpcErrors_ButUnknownNotificationsStaySilent
C2 138-139 | Malformed tools/call params answer -32602, never throw and kill the stdio loop | pinned-by: McpServerTests.MalformedToolsCallParams_AnswerInvalidParams_AndTheLoopSurvives
C2 245-246 | A 200 with an unreadable body degrades to the same honest tool error as downtime | pinned-by: McpServerTests.ASuccessResponseWithANonJsonBody_AnswersAnHonestToolError
C2 274-275 | since is normalized to UTC before reaching the server | pinned-by: McpServerTests.ASinceWithANonUtcOffset_ReachesTheServerAsUtc
C2 300-303 | Tool-level failures use MCP isError inside a successful result, not a JSON-RPC error | pinned-by: McpServerTests.ADeadServer_AnswersAnHonestToolError_NotSilence
C3 8-12 | CLI stays dependency-free (hand-rolled NDJSON JSON-RPC); Project resolved once from server cwd per §7.1 | pinnable-by: plain test
C3 23 | MCP request timeout (30 s) is deliberately generous vs the hooks' 3 s; nothing here blocks a session (§1) | pinnable-by: doc-only
C3 58-62 | Project falls back to CLAUDE_PROJECT_DIR only when cwd is no repository at all (§7.1) | pinnable-by: plain test

FILE src/Mimir.Cli/RemoteIdentity.cs total=19 c1=0 c2=19 c3=0 c4=0
C2 3-13 | Every scheme/credential/separator spelling of one remote normalizes to one identity; only host and drive letter lowercased (case-merge is irreversible, case-split healable, #17) | pinned-by: RemoteIdentityTests (whole class)
C2 22-23 | C:\ and C:/ spellings of one local directory land on one identity | pinned-by: RemoteIdentityTests.EverySpellingOfOneLocalWindowsRemote_IsOneIdentity
C2 40 | scp form: a ':' before any '/' separates host from path | pinned-by: RemoteIdentityTests
C2 49 | Credentials are everything up to the authority's last '@' and are stripped | pinned-by: RemoteIdentityTests
C2 63-66 | Windows-path detection mirrors git's has_dos_drive_prefix; bare "c:path" stays scp host "c" | pinned-by: RemoteIdentityTests.AColonWithNoSeparatorAfterIt_IsStillAnScpHostNotADrive

FILE src/Mimir.Cli/ProjectLocator.cs total=18 c1=2 c2=8 c3=8 c4=0
C2 5 | Identity and root travel with every CLI POST | pinned-by: HookCommandTests.TheWholeStdinTravelsAsThePayload_WithHostResolvedIdentity
C2 8-14 | origin (else alphabetically first remote) normalized; no-remote → root path, non-repo → cwd; never throws; cancellation honored | pinned-by: ProjectLocatorTests (whole class)
C3 17 | Each git invocation gets a 2 s per-call ceiling under the hook's overall cap | pinnable-by: doc-only
C3 22-23 | Toplevel and remote lookups start in parallel to fit the prompt hook's 500 ms budget (§11) | pinnable-by: doc-only
C3 28 | The remote task is always awaited so a hung git is killed, never orphaned | pinnable-by: doc-only
C3 84 | Both pipes are drained during the wait; a full unread stderr buffer can wedge git | pinnable-by: doc-only
C3 90 | stderr's task is observed so an I/O fault routes into the catch | pinnable-by: doc-only
C3 95-96 | On any failure a still-running git is killed and must not outlive the hook | pinnable-by: doc-only
C1 57, 114

FILE src/Mimir.Cli/HookCommand.cs total=14 c1=0 c2=10 c3=4 c4=0
C2 7-12 | Relay stdin JSON with host-resolved identity; sync hooks print the reply, capture hooks print nothing; everything fails open, exit 0 | pinned-by: HookCommandTests (whole class)
C2 29 | Fail open (§4): no server, slow server, bad stdin — all the same silent exit 0 | pinned-by: HookCommandTests.GarbageStdin_StillExitsZeroSilently + ADeadServer_MeansExitZeroNoOutputWellUnderTheCap
C2 37-39 | The synchronous stdin read is parked on a worker thread so the cap can cancel it | pinned-by: HookCommandTests.AStdinThatNeverEnds_StillExitsZeroWithinTheCap
C3 15 | The hard cap on any hook round-trip is 3 s (§11) — the constant's value itself is unasserted (a mutation to 10 s stays green) | pinnable-by: plain test
C3 43 | Missing session_id means stay silent rather than guess an Episode | pinnable-by: plain test
C3 80-81 | An unknown hook event is dropped, not relayed for the server to guess at | pinnable-by: plain test

FILE src/Mimir.Cli/Program.cs total=12 c1=3 c2=0 c3=9 c4=0
C3 4-6 | `hook` exits 0 on every path including argument mistakes and a malformed MIMIR_URL; `mcp` may fail loudly | pinnable-by: plain test
C3 30-32 | HttpClient.Timeout stays pinned to the cap as a backstop against a future un-threaded token | pinnable-by: plain test
C3 48-50 | MCP stdio must be no-BOM UTF-8 with "\n" framing or non-ASCII remembered content mojibakes permanently | pinnable-by: plain test
C1 1-3

FILE src/Mimir.Contracts/Health/HealthSnapshot.cs total=59 c1=15 c2=39 c3=5 c4=0
C2 26 | Pending is the state at boot, before anything has reported | pinned-by: HealthStateTests.StartsPending_BeforeAnythingHasReported
C2 29 | Working means reporting but mid-flight (models still pulling) | pinned-by: ModelProvisionerTests.WhilePulling_TheTileIsWorkingAndNamesWhatItIsDoing
C2 35 | Degraded covers both reachable-but-unhappy and unreachable entirely | pinned-by: StorageTileFactoryTests.AnUnreachableDatabase_DegradesTheTileAndSaysWhy
C2 63 | PercentComplete is null outside a total-reporting pull | pinned-by: ModelPullTests
C2 85-88 | A failed distillation run goes Degraded but keeps the last good figures | pinned-by: DistillerServiceTests.AFailingEpisode_IsParkedFailed_AndDegradesTheTile
C2 102 | QueueDepth is sealed Episodes still owed distillation, null until known — NOTE the line's "(pending + running)" phrasing is STALE: Failed counts too (see Taxonomy gaps) | pinned-by: DistillerServiceTests
C2 105 | LastRunAt marks the last attempt finishing well or badly, null until one has | pinned-by: DistillerServiceTests
C2 109-112 | A failed scan goes Degraded but keeps the last good figures, labelled stale | pinned-by: HarvesterServiceTests.AFailingScan_DegradesTheTileAndKeepsTheLastGoodFigures
C2 126 | LastScanAt is the last successful scan's finish, null until one has | pinned-by: HarvesterServiceTests
C2 136-139 | Storage tile discovers whatever tables exist rather than naming domain tables | pinned-by: PostgresStorageProbeTests (whole class)
C2 154-157 | Every public-schema table appears; a partitioned table appears once under its parent | pinned-by: PostgresStorageProbeTests
C2 166-169 | TotalBytes is heap+indexes+TOAST rolled up across partitions, exact, and the fallback figure when Occupancy is Unknown | pinned-by: PostgresStorageProbeTests + StorageTileFactoryTests
C2 172-180 | Three-valued on purpose; Unknown ≠ Empty and must never render as it; Unknown pinned to 0 so the C# default is honest | pinned-by: StorageTileFactoryTests.TheDefaultOccupancyIsUnknown + WhenAnyTableIsUnknown_TheSummaryMakesNoOccupancyClaim
C2 183, 186, 189 | Unknown renders distinctly from Empty; Empty and Populated are each proved by EXISTS in this snapshot | pinned-by: StorageTileFactoryTests + PostgresStorageProbeTests.AnEmptyTableIsReportedEmpty_NotUnknown
C3 161-165 | TableFootprint carries no row count: under §10 keep-forever an exact count is an unbounded scan and every cheap estimate misreported (ADR-0006) | pinnable-by: doc-only
C1 3, 23, 32, 39, 50, 56, 66, 72, 75, 78, 81, 99, 123, 129, 132

FILE src/Mimir.Contracts/Mcp/McpSearchRequest.cs total=13 c1=2 c2=11 c3=0 c4=0
C2 3-7 | Deliberate recall reaches everything regardless of scope; the requester's Project only anchors the affinity boost and the Injection row | pinned-by: McpSearchServiceTests.FusedResults_ReachOtherProjectsWisdom_AndEpisodeEvents_AndLogTheInjection
C2 10 | SessionId is the MCP server's pseudo id; stdio MCP never sees a real one | pinned-by: McpServerTests.ASearchCall_PostsTheProjectAndPseudoSession_AndRelaysTheReplyText
C2 21, 24, 27, 30, 33 | Project filter narrows both legs; Kind narrows to the four kinds; Since gates on the instant; IncludeEpisodes defaults true; Retired only with IncludeRetired | pinned-by: McpSearchServiceTests filter tests + McpServerTests.ASearchCall
C1 13, 16

FILE src/Mimir.Contracts/Hooks/HookEventRequest.cs total=10 c1=4 c2=6 c3=0 c4=0
C2 5-8 | Identity and root travel with every request; resolution is host-side, the server never runs git (§3.1) | pinned-by: HookCommandTests.TheWholeStdinTravelsAsThePayload_WithHostResolvedIdentity
C2 11 | SessionId is the Episode key — one Episode per session (ADR-0003) | pinned-by: CaptureServiceTests.SessionStartTwice_ResumesTheSameEpisode
C2 26 | Payload is the full stdin JSON untouched; capture is dumb, the server truncates | pinned-by: HookCommandTests + CaptureServiceTests
C1 14, 17, 20, 23

FILE src/Mimir.Contracts/Mcp/McpRememberRequest.cs total=8 c1=2 c2=6 c3=0 c4=0
C2 3-7 | Bound server-side to the most recently active unsealed Episode, else straight to the Merge Gate; a deliberate save is never dropped | pinned-by: McpRememberServiceTests (whole class)
C2 18 | Kind must be one of the four Wisdom kinds | pinned-by: McpRememberServiceTests.AnUnknownKind_NamesTheVocabulary_AndWritesNothing
C1 10, 13

FILE src/Mimir.Contracts/Mcp/McpToolReply.cs total=5 c1=0 c2=5 c3=0 c4=0
C2 3-7 | Text is composed server-side so the Injection row's chars counts exactly what the session received; the CLI relays it verbatim | pinned-by: McpSearchServiceTests + McpServerTests.ASearchCall

FILE src/Mimir.Contracts/Hooks/UserPromptReply.cs total=5 c1=0 c2=5 c3=0 c4=0
C2 3-6 | The one UserPromptSubmit round-trip records the prompt Event and returns any Prompt-lane injection for the CLI to print | pinned-by: UserPromptEndpointTests (whole class) + HookCommandTests.UserPromptSubmit_IsOneRoundTripThatPrintsTheInjection
C2 9 | Empty Injection means inject nothing (print nothing) | pinned-by: HookCommandTests.AnEmptyInjection_PrintsNothingAtAll

FILE src/Mimir.Contracts/Hooks/SessionStartReply.cs total=5 c1=0 c2=4 c3=1 c4=0
C2 3-6 | SessionStart creates or resumes the Episode and returns the Brief for the CLI to print | pinned-by: CaptureServiceTests + HookCommandTests.SessionStart_PostsToItsRouteAndPrintsTheBrief
C3 9 | An empty Brief means print nothing at session start | pinnable-by: plain test

FILE src/Mimir.Contracts/Hooks/HookEvents.cs total=5 c1=0 c2=5 c3=0 c4=0
C2 3-7 | SessionStart/SessionEnd are hook events, not §3 Event types: SessionStart creates/resumes the Episode, SessionEnd Seals it | pinned-by: CaptureServiceTests

FILE src/Mimir.Contracts/Mcp/McpTimelineRequest.cs total=3 c1=1 c2=2 c3=0 c4=0
C2 6, 9 | Project filter narrows the timeline by display name or identity; Since keeps only Episodes started at or after the instant | pinned-by: McpTimelineServiceTests.ProjectAndSinceFilters_NarrowTheTimeline
C1 3

### tests/Mimir.Server.Tests — harness and root

FILE tests/Mimir.Server.Tests/PostgresTestBase.cs total=102 c1=47 c2=21 c3=34 c4=0
C2 15-21 | One harness: DB emptied before each test; the clean slate is a property of the harness, not a per-class convention (#20/#22) | pinned-by: PostgresTestBaseTests (pollution pairs)
C2 367-370 | Reset empties the database before each test; a derived override wraps base.InitializeAsync | pinned-by: PostgresTestBaseTests.EveryTableHoldsOnlyThisTestsRows_First/Second
C2 386-395 | Truncate every mapped table CASCADE; restore the pristine migration-sourced Global seed fresh each reset (mutations must not leak; dropping the HasData seed fails the pin) | pinned-by: PostgresTestBaseTests.AMutatedGlobalRow_DoesNotOutliveItsTest_First/Second + AfterTheReset_TheGlobalPseudoProjectIsTheOnlyProject
C3 22-26 | Members are private-protected because internal types (MergeGate, the fakes) are handed out in a one-assembly suite | pinnable-by: doc-only
C3 100-104 | MergeGate is composed only here; the one hand-built gate is MergeGateGuardTests, which needs a never-connecting factory | pinnable-by: doc-only
C3 124-127 | A caller overriding RecallOptions must hand the same instance to the ranking, or the test pins a value the ranked rows were never scored with | pinnable-by: doc-only
C3 131-137 | AddThrowawayStorage mirrors AddMimirStorage (both registrations, Singleton options, #23); the connection string is read on the test thread so the no-Postgres skip is not an unobserved exception | pinnable-by: plain test / doc-only (restated in CLAUDE.md)
C3 147-151 | Seeder gives unique identity/root per call; DisplayName is NOT unique — name projects apart when filtering by it | pinnable-by: doc-only
C3 210-212 | Salient comes from the caller, never derived, or the seeder restates CaptureService's salience rule | pinnable-by: doc-only
C3 396-398 | Table list comes from the EF model (a later entity auto-empties), but a second HasData seed anywhere is truncated and NOT restored — adding one means extending ResetAsync | pinnable-by: doc-only
C3 408-409 | Raw-not-interpolated SQL is safe: names are model-sourced and TRUNCATE takes no parameters | pinnable-by: doc-only
C1 30-33, 38, 41, 44, 47, 50, 53-56, 66, 69, 79-82, 89, 92-96, 114-117, 220-223, 298-302, 309-315, 345

FILE tests/Mimir.Server.Tests/PostgresTestBaseTests.cs total=18 c1=0 c2=18 c3=0 c4=0
C2 6-12 | The harness pinning itself: the pollution pairs make a broken reset go red deterministically on every machine, in either order | pinned-by: whole class
C2 43-48 | Global is the truncate's sole survivor — assert pristine, then mutate as a resolver/merger would, and let the sibling assert first | pinned-by: AMutatedGlobalRow_DoesNotOutliveItsTest_First/Second
C2 60-64 | One row per mutable table, then whole-table counts; the pair is provably the same test twice | pinned-by: EveryTableHoldsOnlyThisTestsRows_First/Second

FILE tests/Mimir.Server.Tests/SurfaceChassisTests.cs total=104 c1=0 c2=52 c3=52 c4=0
C2 5-17 | #119 chassis lives once in the token layer; four properties (defined in mimir.css, no unlicensed scoped styling, no ::deep, no vendored collision) | pinned-by: whole class
C2 84-102 | ScopedDeltas is the complete licence inventory, each delta held to exactly its listed declarations | pinned-by: NoScopedStylesheet_StylesAHoistedClass + EveryLicensedDelta_IsStillADelta
C2 163-166 | A licence whose rule has gone is standing permission to write a duplicate back | pinned-by: EveryLicensedDelta_IsStillADelta
C2 184-189 | A new ::deep means a shared rule written back into a scoped file | pinned-by: NoScopedStylesheet_ReachesIntoAChildComponent
C2 202-211 | A Nocturne sync naming a hoisted class must go red, not land with a silently changed cascade | pinned-by: NoHoistedClass_IsNamedByTheVendoredSystem
C3 32-39 | Which surfaces draw which tier (detail plumbing on two child-component surfaces; pane-danger/pane-error only on the two hard-delete surfaces, #106) | pinnable-by: bUnit render test
C3 71-76 | is- modifiers are excluded from ownership because the base class names the owned thing | pinnable-by: doc-only
C3 240-245 | The scan flattens at-rules so a one-line @media-nested rule is caught | pinnable-by: plain test
C3 294-305 | Legality is decided by the subject (rightmost compound), catching respellings while own-markup-under-chassis stays legal | pinnable-by: plain test
C3 328-332 | Declaration split on ; assumes no semicolon inside a chassis value | pinnable-by: plain test
C3 342-350 | Defines anchors at line start/comma so a compound never matches inside a longer one | pinnable-by: plain test
C3 361-366 | Names is deliberately blunter than Defines; the lookahead stops .chip answering for .chip-count | pinnable-by: plain test

FILE tests/Mimir.Server.Tests/ThrowawayDatabaseFixture.cs total=18 c1=3 c2=8 c3=7 c4=0
C2 27-34 | GlobalSeed is read once pre-mutation and stays migration-sourced, so dropping the HasData seed fails the harness's pin | pinned-by: PostgresTestBaseTests
C3 8-14 | One migrated throwaway database per class, dropped on dispose, skipping when no Postgres is reachable | pinnable-by: plain test
C1 21, 24, 37

FILE tests/Mimir.Server.Tests/CapturedLog.cs total=11 c1=0 c2=0 c3=10 c4=1
C3 5-9 | Only warnings are captured; asserting below Warning would pin phrasing, not behaviour | pinnable-by: doc-only
C3 16-20 | IsEnabled answers true for every level so IsEnabled-guarded branches still run under test | pinnable-by: plain test
C4 37 | <inheritdoc cref="CapturedLog"/>

FILE tests/Mimir.Server.Tests/OfflineAssetsTests.cs total=11 c1=0 c2=11 c3=0 c4=0
C2 5-15 | ADR-0001: no shipped stylesheet may fetch a remote resource, over both roads CSS reaches a browser (wwwroot + scoped bundle) | pinned-by: whole class

FILE tests/Mimir.Server.Tests/TestPostgres.cs total=8 c1=1 c2=0 c3=7 c4=0
C3 3-8 | The test-Postgres convention lives in one place; drifting copies would turn a config change into silent skips | pinnable-by: doc-only
C3 14 | The admin connection targets the development server, not a test database | pinnable-by: doc-only
C1 19

FILE tests/Mimir.Server.Tests/DisconnectedContextFactory.cs total=6 c1=0 c2=0 c3=6 c4=0
C3 6-11 | Guard tests build over a never-connecting factory so they fail (not skip) on a machine with no Postgres | pinnable-by: doc-only

FILE tests/Mimir.Server.Tests/CssText.cs total=6 c1=0 c2=0 c3=6 c4=0
C3 5-10 | Both stylesheet scans must strip comments or a commented-out remote URL / selector-in-prose falsely fails them | pinnable-by: plain test

FILE tests/Mimir.Server.Tests/FixtureContextFactory.cs total=5 c1=0 c2=0 c3=5 c4=0
C3 6-10 | Adapts the fixture to IDbContextFactory and skips like every other Postgres-backed path when no database is reachable | pinnable-by: doc-only

### tests — Capture

FILE tests/Mimir.Server.Tests/Capture/CaptureServiceTests.cs total=21 c1=0 c2=16 c3=5 c4=0 (salvaged, verified)
C2 11-15 | Class pins spec §4 capture: session hooks -> one Episode, ordered Events, truncated payloads, Seal with SessionEnd reason (ADR-0003) | pinned-by: whole class
C2 77 | Async hooks can outrun SessionStart; capture never drops what it can attach (§4) | pinned-by: AnEventForAnUnseenSession_CreatesTheEpisodeOnDemand
C2 152-153 | First-seal-wins under real concurrency | pinned-by: ASealFromAnotherContext_BeatsAStaleDuplicate
C2 176-177 | PostToolUse fire-and-forget; straggler after SessionEnd still captured | pinned-by: AStragglerEventAfterTheSeal_IsStillCaptured
C2 186-187 | Every committed capture write announces on the feed; no-op stays quiet (§8.2) | pinned-by: the four feed tests
C2 240-243 | Identity follows the repository (§3.1): two clones -> one Project with both roots | pinned-by: SessionsInTwoClonesOfOneRepo_EndUpUnderOneProjectWithBothRoots
C3 271-275 | Harness contract: Request() must generate unique identity/root per call or §3.1 root-matching welds a test's second session onto its first | pinnable-by: plain test

FILE tests/Mimir.Server.Tests/Capture/ProjectResolverTests.cs total=18 c1=0 c2=17 c3=1 c4=0 (salvaged, verified)
C2 7-12 | Class pins §3.1 resolution semantics | pinned-by: whole class
C2 55-57 | Only a path-born Project is upgraded | pinned-by: AKnownRootWithADifferentRemoteIdentity_MatchesByRoot_AndKeepsItsStoredIdentity
C2 71-73 | Concurrent root appends merge | pinned-by: ARootAppendedByAnotherContext_SurvivesThisContextsAppend
C2 96-98 | Identity upgrade fixes the same row | pinned-by: APathIdentityProject_ReportingARemote_IsUpgradedInPlace
C2 114-115 | Rootless hook's path identity reveals nothing | pinned-by: APathIdentityProject_SeenAtASecondRootWithoutARemote_KeepsItsPathIdentity
C3 141 | Harness contract: Identity() unique per call — not pinned | pinnable-by: plain test

FILE tests/Mimir.Server.Tests/Capture/ProjectMergeTests.cs total=12 c1=0 c2=11 c3=1 c4=0 (salvaged, verified)
C2 7-12 | Class pins §3.1 clone merge | pinned-by: whole class
C2 40-45 | Merge re-points FKs enumerated from the database catalog; scratch table deliberately not dropped | pinned-by: TheMerge_RePointsReferencesFromTablesThisCodeHasNeverHeardOf
C3 95 | Harness contract: Identity() unique per call | pinnable-by: plain test

FILE tests/Mimir.Server.Tests/Capture/UserPromptEndpointTests.cs total=6 c1=0 c2=5 c3=1 c4=0
C2 15-19 | The §4 round-trip captures the Event and answers the injection; recall failing still leaves a successful capture answering empty (§7 fail-open) | pinned-by: whole class
C3 22 | The prompt shares no word with the test Wisdom so only the vector leg ranks | pinnable-by: doc-only

FILE tests/Mimir.Server.Tests/Capture/PayloadTruncatorTests.cs total=6 c1=0 c2=6 c3=0 c4=0
C2 8-11 | §4 fidelity: 3 KB head + 1 KB tail with marker, prompts whole, original size always recorded | pinned-by: whole class
C2 30 | 5,000 ASCII bytes leaves exactly 904 dropped after head 3,072 + tail 1,024 | pinned-by: AnOversizedField_Keeps3KHeadPlus1KTailWithTheMarker
C2 75 | 1,707 three-byte '€' (5,121 bytes) forces cuts near both limits | pinned-by: MultiByteTextIsCutAtCharacterBoundaries_NeverCorrupted

FILE tests/Mimir.Server.Tests/Capture/EpisodeFeedTests.cs total=6 c1=0 c2=6 c3=0 c4=0
C2 5-8 | The feed makes §8.2's Episode list live; one dead subscriber never silences the rest | pinned-by: whole class
C2 44-45 | A tearing-down circuit's failure must not stop other circuits or abort the publishing capture request | pinned-by: AThrowingSubscriber_NeverSilencesTheOthers

### tests — Storage, Evaluation, Configuration, Models, Health

FILE tests/Mimir.Server.Tests/Storage/PostgresStorageProbeTests.cs total=34 c1=1 c2=27 c3=6 c4=0
C2 8-13 | ADR-0006's traps are only pinnable against real Postgres; scratch tables die with the class's throwaway database | pinned-by: whole class
C2 25-28 | Statistics report an analyzed-empty-then-populated table as empty | pinned-by: AnalyzedWhileEmptyThenPopulated_ReportsPopulated
C2 41-45 | n_live_tup still reports rows after a full DELETE, exactly when a §8.2 user checks their deletion | pinned-by: PopulatedThenFullyDeleted_ReportsEmpty
C2 65-67 | pg_partition_tree returns zero rows for a plain table, so an unconditional rollup sizes it at 0 | pinned-by: APlainTableReportsItsRealSize
C2 77-78 | pg_tables returns parents AND children, double-counting partitioned tables | pinned-by: APartitionedTableIsDiscoveredOnceUnderItsParentName
C2 99-105 | Size/occupancy invariant needs both sides seeded (truncated mapped tables keep index pages; text columns bring an 8 KB TOAST metapage) | pinned-by: AZeroByteTableIsNeverReportedPopulated
C3 14-19 | Scratch tables outlive the per-test reset, so no catalog-counting or only-table assertion may live in this class | pinnable-by: doc-only
C1 144

FILE tests/Mimir.Server.Tests/Storage/WisdomSearchAmbientTests.cs total=19 c1=0 c2=19 c3=0 c4=0
C2 11-17 | The ambient universe's eligibility matrix is asserted against both methods so a fork of the shared clause cannot leave them disagreeing | pinned-by: whole class
C2 43-46 | Top-N (50) exceeds the seeding so equality is the whole matrix in both directions | pinned-by: AmbientUniverse_SearchAndList_AgreeOnTheFullEligibilityMatrix
C2 56 | Foreign rows must outrank ambient on both legs or the filter-position mutation is invisible | pinned-by: AmbientUniverse_RestrictsBeforeThePerLegLimit_NotAfterFusion
C2 68-70 | Restriction after the LIMIT would leave ambient recall empty while eligible matches sit deeper | pinned-by: AmbientUniverse_RestrictsBeforeThePerLegLimit_NotAfterFusion
C2 87-90 | The real §8.2 orphaning path: episode hard-delete cascades Provenance away and the universe keeps the orphan | pinned-by: AmbientUniverse_SearchAndList_AgreeOnTheFullEligibilityMatrix

FILE tests/Mimir.Server.Tests/Storage/WisdomSearchTests.cs total=10 c1=0 c2=10 c3=0 c4=0
C2 10-14 | §3 hybrid search: per-leg top-N, RRF for order only, cosine rides the vector leg, non-Retired filter; tiny top-N makes leg membership observable | pinned-by: whole class
C2 29-31 | The dual-leg row must lead and the in-neither-leg row must be absent | pinned-by: RrfFusion_RanksADualLegRowAboveEitherSingleLegRow
C2 46-47 | Fused values are rank-fusion ordinals, never comparable to a cosine threshold (§3) | pinned-by: FusedScores_AreRankFusionValues_NeverACosineScale

FILE tests/Mimir.Server.Tests/Storage/StorageQueriesTests.cs total=10 c1=0 c2=10 c3=0 c4=0
C2 8-9 | The fresh-database empty state returns null so it costs no second round trip | pinned-by: NoTables_ProducesNoQuery
C2 34-35 | ADR-0006: occupancy never counts — an exact count is unbounded under §10 keep-forever | pinned-by: OccupancyNeverCounts
C2 62-64 | Discovery reads pg_class (not pg_tables) to exclude partition children and roll size into the parent | pinned-by: DiscoveryExcludesPartitionChildren_SoAPartitionedTableIsCountedOnce
C2 73-75 | The CASE on relkind is load-bearing: an unconditional pg_partition_tree rollup reports every plain table as 0 bytes | pinned-by: DiscoveryRollsUpPartitionSizes_OnlyForPartitionedParents

FILE tests/Mimir.Server.Tests/Storage/StorageTileFactoryTests.cs total=8 c1=0 c2=8 c3=0 c4=0
C2 11 | The no-tables state is Ready, not an error | pinned-by: AFreshlyMigratedDatabase_IsReadyWithNoTables
C2 44 | A genuinely empty database must not read the same as a populated one | pinned-by: WhenEveryTableIsEmpty_TheSummarySaysSo
C2 56-57 | An Unknown table means the summary makes no occupancy claim at all | pinned-by: WhenAnyTableIsUnknown_TheSummaryMakesNoOccupancyClaim
C2 70-71 | Occupancy is carried in words, never derived from the byte figure | pinned-by: TheSummaryNeverInfersEmptinessFromBytes
C2 80-81 | default(TableOccupancy) stays Unknown so an enum reorder cannot make Empty the accidental default | pinned-by: TheDefaultOccupancyIsUnknown

FILE tests/Mimir.Server.Tests/Storage/ProvenanceDeletionTests.cs total=7 c1=1 c2=6 c3=0 c4=0
C2 6-11 | §3 deletion contract: only §8.2 hard delete removes Provenance; orphaned Wisdom survives; deleting Wisdom cascades its chain and Provenance (§10) | pinned-by: whole class
C1 68

FILE tests/Mimir.Server.Tests/Evaluation/GoldenSuiteTests.cs total=16 c1=3 c2=5 c3=8 c4=0
C2 13-17 | §9 golden suite replays every GoldenCase in the development database; one failing case fails the test | pinned-by: whole class
C3 18-20 | The ollama trait keeps this suite out of CI's zero-skip run, which never has Ollama | pinnable-by: plain test
C3 21-25 | Must NOT inherit PostgresTestBase — an emptied throwaway would sweep zero cases and pass forever | pinnable-by: plain test
C1 29, 89, 102

FILE tests/Mimir.Server.Tests/Evaluation/GoldenRunnerTests.cs total=11 c1=0 c2=10 c3=1 c4=0
C2 11-16 | §9 runner: each case replays through the shared §7 ranking, unthresholded, under its own affinity context, passing within golden-set k | pinned-by: whole class
C2 73-74 | Per-leg top-N of 1 crowds the expected row off both legs so it never ranks | pinned-by: ExpectedWisdomOffBothLegs_Fails_WithNoRank
C2 90-91 | At k=1 only the case's own Project affinity boost lifts the expected row past the nearer Global one | pinned-by: CasesRankUnderTheirOwnAffinityContext
C3 19 | The query shares no word with any test Wisdom so only the vector leg ranks | pinnable-by: doc-only

FILE tests/Mimir.Server.Tests/Configuration/MimirOptionsTests.cs total=9 c1=0 c2=9 c3=0 c4=0
C2 8-10 | The §11 knob table is normative; every default asserted is quoted from the spec | pinned-by: whole class
C2 48 | Compose passes knobs as env vars; the __ separator must reach the same options | pinned-by: OptionsBindFromDoubleUnderscoreEnvironmentVariables
C2 94-97 | Head+tail beyond the cap fails validation; near-int.MaxValue rows would wrap int addition around the check | pinned-by: HeadPlusTailBeyondTheCap_FailsValidation
C2 130 | ChunkTokens past num_ctx would overflow, not chunk | pinned-by: InvalidDistillerKnobs_FailValidation

FILE tests/Mimir.Server.Tests/Configuration/AppSettingsTests.cs total=4 c1=0 c2=4 c3=0 c4=0
C2 6-9 | appsettings.json restates the §11 defaults; this is what stops the two drifting apart | pinned-by: whole class

FILE tests/Mimir.Server.Tests/Models/FakeModelCatalog.cs total=7 c1=3 c2=0 c3=4 c4=0
C3 6-9 | Every method records its calls so tests can assert a present model was NOT re-pulled | pinnable-by: doc-only
C1 20, 23, 26

FILE tests/Mimir.Server.Tests/Models/ModelProvisionerTests.cs total=5 c1=1 c2=4 c3=0 c4=0
C2 64 | The acceptance-criteria demo watches this progress sequence on the tile | pinned-by: PullProgress_ReachesTheTileAsItArrives
C2 120-121 | A failed pull is terminal and must not hide behind Working while a multi-gigabyte pull finishes | pinned-by: AFailedPull_IsSurfacedWhileALaterModelIsStillPulling
C2 138 | Two retry intervals must elapse before the third (successful) list call | pinned-by: AnUnreachableOllama_IsRetriedUntilItAnswers
C1 196

FILE tests/Mimir.Server.Tests/Health/HealthStateTests.cs total=1 c1=0 c2=1 c3=0 c4=0
C2 72 | A dead Blazor circuit must never take down health reporting for the rest of the app | pinned-by: AThrowingSubscriber_DoesNotStarveTheOthersOrTheUpdate

### tests/Mimir.Cli.Tests

FILE tests/Mimir.Cli.Tests/HookCommandTests.cs total=12 c1=4 c2=8 c3=0 c4=0
C2 9-13 | The §4 fail-open rules are the contract: a dead Mimir never breaks or slows a session | pinned-by: whole class
C2 38-40 | Console.In reads synchronously beneath its async surface; an un-capped read would hang the session (§4 forbids that) | pinned-by: AStdinThatNeverEnds_StillExitsZeroWithinTheCap
C1 147, 167, 177, 195

FILE tests/Mimir.Cli.Tests/RemoteIdentityTests.cs total=11 c1=0 c2=11 c3=0 c4=0
C2 5-8 | §3.1: every spelling of one repository's remote lands on one identity, because identity makes two clones one Project | pinned-by: whole class
C2 31-33 | Only the host is lowercased — lowercasing the path could irreversibly merge two case-sensitive repositories; a split is the healable direction (#17) | pinned-by: OnlyTheHostIsLowercased_OwnerAndRepoKeepTheirCase
C2 46-47 | C:\ and C:/ are one directory, not an SSH host, or a local bare remote splits into two Projects | pinned-by: EverySpellingOfOneLocalWindowsRemote_IsOneIdentity
C2 66-67 | git only treats letter:separator as a DOS path; bare "c:path" stays scp-form host "c" | pinned-by: AColonWithNoSeparatorAfterIt_IsStillAnScpHostNotADrive

FILE tests/Mimir.Cli.Tests/McpServerTests.cs total=11 c1=3 c2=8 c3=0 c4=0
C2 8-12 | The MCP lane is deliberate: a dead Mimir answers an honest isError tool error, never hook-style silence | pinned-by: whole class
C2 19-20 | Affirming 2025-03-26 would license the client to batch; the handshake answers a served version | pinned-by: Initialize_AnswersTheOneServedProtocolVersion_NeverAnEcho
C2 174 | A proxy/captive portal answering 200 with HTML becomes an honest tool error | pinned-by: ASuccessResponseWithANonJsonBody_AnswersAnHonestToolError
C1 217-218, 237

FILE tests/Mimir.Cli.Tests/ProjectLocatorTests.cs total=8 c1=0 c2=6 c3=2 c4=0
C2 5-8 | §3.1 steps 1 and 3 against real git: which remote wins, and the path fallback | pinned-by: whole class
C2 94-95 | An expired cap means "no repository information", never an exception (§4 fail-open) | pinned-by: AnAlreadyCancelledCap_StillAnswersWithThePathFallback
C3 23 | Windows briefly holds .git files; a leaked temp dir is acceptable in cleanup | pinnable-by: doc-only
C3 26 | git marks some object files read-only; same cleanup tolerance | pinnable-by: doc-only

### tests — Ui and Components

FILE tests/Mimir.Server.Tests/Ui/WisdomDisplayTests.cs total=114 c1=2 c2=112 c3=0 c4=0
C2 6-11 | §8.1 detail words/arithmetic are pure so pins run without Postgres | pinned-by: whole class
C2 27-30 | Event-less provenance falls back to session start, not blank | pinned-by: AnEpisodeProvenance_IsNamedByWhenTheSessionStarted
C2 53-57 | An all-null Provenance row still renders words rather than throwing | pinned-by: AProvenanceLinkingToNothing_StillReadsAsSomething
C2 67-70 | The closing note renders only where a row can open an Episode | pinned-by: TheProvenanceNote_PromisesTheEpisodeOnlyWhereThereIsOne
C2 94-99 | Merged reads "reinforced"; every other cause reads as its member name | pinned-by: TheCauseBadge_ReadsMergedAsReinforced_AndEveryOtherCauseAsItself
C2 109-113 | Legend is enum-built: every §3 cause, §3 order, distinct meanings | pinned-by: TheLegend_DefinesEveryCauseTheDomainHas
C2 123-130 | Diff fixture leaves only the substitution free to move; removed precedes added | pinned-by: TheDiff_MarksTheWordsThatWent_AndThenTheWordsThatArrived
C2 146-153 | Diff reproduces the new text exactly and accounts for every old word | pinned-by: TheDiff_DrawsExactlyTheNewText_AndAccountsForEveryWordOfTheOld
C2 172-178 | Struck runs keep a separator at all three edge shapes | pinned-by: TheDiff_KeepsAStruckRunClearOfTheWordsBesideIt
C2 199-203 | The first version diffs as plain Kept, not wholesale arrival | pinned-by: TheFirstVersion_HasNothingToDifferFrom_AndReadsPlain
C2 211-217 | Past DiffWordBound the diff says "changed" wholesale; at the bound it still marks words | pinned-by: TheDiff_PastItsWordBound_SaysTheWholeTextChanged
C2 239-244 | Chain sorts newest-first itself; seeded oldest-first so a dropped sort reddens | pinned-by: TheChain_ReadsNewestFirst_AndDiffsEachVersionAgainstTheOneBelowIt
C2 275-280 | An unsaved draft heads the chain as the trimmed, timestamp-less version it would become | pinned-by: AnUnsavedDraft_HeadsTheChain_AsTheVersionItWouldBecome
C2 300-305 | The pending row diffs against the caller's current text, not the head version | pinned-by: AnUnsavedDraft_IsMeasuredAgainstWhatTheWisdomSays_NotAgainstItsHeadVersion
C2 320-321 | A draft equal to the current text is a no-op whatever the head version holds | pinned-by: AnUnsavedDraft_IsMeasuredAgainstWhatTheWisdomSays
C2 330-334 | The gate's own no-op set decides whether a pending row exists | pinned-by: ADraftThatWouldSaveNothing_IsNotAVersion
C2 365-368 | The Recall note counts unmarked entries and says marks are per injection (§9) | pinned-by: TheRecallNote_CountsTheUnjudgedEntries_AndSaysWhatAMarkIsLeftOn
C2 383-384 | The sentence names the total its remainder is of | pinned-by: TheRecallNote_CountsTheUnjudgedEntries
C2 400-404 | The reinforcement bar clamps at both edges of its fixed width | pinned-by: TheReinforcementBar_FillsOneSegmentPerConfirmation_AndStopsAtItsWidth
C2 430-435 | Save disabled only on blank/identical-after-trim; stored-text whitespace still saves | pinned-by: SavingIsPointless_OnBlankTextAndOnTextThatAlreadySaysThis
C2 448-452 | The editor paragraph states the gate's exact terms | pinned-by: TheEditorExplains_WhatTheGateWillDo_InTheGatesOwnTerms
C1 361, 416

FILE tests/Mimir.Server.Tests/Ui/DebouncerTests.cs total=71 c1=1 c2=45 c3=25 c4=0
C2 7-12 | Real timers, not a TimeProvider seam: cancellation is pinned against what production waits on | pinned-by: whole class
C2 64-66 | Debounces rather than throttles: two spaced signals are two runs | pinned-by: AQuietGap_LetsTheNextSignalRunOnItsOwn
C2 82-83 | Dispose mid-window cancels the pending run | pinned-by: DisposingMidBurst_RunsNothingThatWasStillWaitingOutItsDelay
C2 98-104 | A failing action is logged; asserted on the log because "next signal ran" stays green with the catch deleted | pinned-by: AFailingAction_IsReported_NotLeftForNobodyToObserve
C2 116-117 | A superseded (cancelled) run logs nothing | pinned-by: ASupersededRun_IsNotReportedAsAFailure
C2 131-134 | A Schedule racing Dispose is refused, not armed unowned | pinned-by: ASignalRacingDispose_IsRefusedRatherThanArmingATimerNobodyCancels
C2 152-155 | Pre-Dispose schedules were still in-delay so cancelled; post-Dispose refused — zero survive | pinned-by: ASignalRacingDispose_IsRefused
C2 162-163 | The ceiling exists so a live burst is not starved of refreshes | pinned-by: ABurstPastTheCeiling_RunsDuringIt_NotOnlyOnceItGoesQuiet
C2 170-174 | Bounded 2–4 runs: at-least-2 pins the restarting ceiling clock, at-most-4 keeps it a debounce | pinned-by: ABurstPastTheCeiling_RunsDuringIt
C2 180-183 | No-ceiling instances run nothing mid-burst; the exclusion is pinned as behaviour | pinned-by: ThatSameBurst_WithNoCeilingConfigured_RunsNothingUntilItGoesQuiet
C2 191-192 | The trailing edge still arrives after quiet, so drop-everything can't pass | pinned-by: ThatSameBurst_WithNoCeilingConfigured
C2 202-205 | A ceiling multiple ≤0 throws at construction rather than firing per keystroke | pinned-by: ACeilingOfZeroOrLess_IsRefused_RatherThanTurningTheDebounceOff
C3 13-22 | Ceiling-pair margins need the burst gap to stay under the delay; a slow box eats the 10× margin, hence CeilingDelay | pinnable-by: doc-only
C3 25 | Delay is sized so the suite is quick but a burst is really one burst | pinnable-by: doc-only
C3 28 | LongEnough = 10× Delay: a run absent by then was never coming | pinnable-by: doc-only
C3 31-35 | CeilingDelay keeps BurstGap an order of magnitude inside the delay | pinnable-by: doc-only
C3 38-39 | BurstGap sits well under the delay so a pure trailing edge never elapses mid-burst | pinnable-by: doc-only
C3 210-215 | BurstAsync is wall-clock-bounded so slow timers still span the ceiling | pinnable-by: doc-only
C1 227

FILE tests/Mimir.Server.Tests/Ui/WisdomBrowserTests.cs total=46 c1=0 c2=46 c3=0 c4=0
C2 8-14 | §8.1 queries and curation actions against real Postgres | pinned-by: whole class
C2 17-20 | Project arm alone seeded: only dropping scope_project_id=@project reddens it | pinned-by: SelectingAProject_ListsItsOwnWisdom_AndNoOtherProjects
C2 35-38 | Global arm alone seeded: only dropping the Global clause reddens it | pinned-by: SelectingAProject_AlsoListsGlobal_TheSetASessionThereRecalls
C2 51 | Global's ambient universe is itself | pinned-by: SelectingGlobal_ListsGlobalAlone
C2 245-248 | The aside names Provenance by moment and cwd; seeded away from Episode times so a wrong column reddens | pinned-by: TheProvenanceDrillDown_CarriesTheMomentAndTheWorkingDirectory
C2 273-278 | Lane figures span every Project; distinct counts so swapped arms redden | pinned-by: TheDetail_CountsEveryLaneThatRecalledIt_AcrossEveryProject
C2 305 | The §9 mark is per entry, counting against every carried line | pinned-by: TheDetail_CountsTheMarksLeftOnTheEntriesThatCarriedIt
C2 329-332 | Figures exclude entries that carried only other Wisdom | pinned-by: TheDetail_CountsNoEntryThatCarriedAnotherWisdomAlone
C2 350-353 | Every lane keeps a zero row rather than vanishing | pinned-by: TheDetail_OfNeverRecalledWisdom_StillNamesEveryLane
C2 369-375 | Chain ends read off rows, not length; recency ≠ foot, gap in chain seeded | pinned-by: TheDetail_ReadsBothEndsOfTheChain_OffItsRowsRatherThanItsLength
C2 470-473 | The SUT wires a real Merge Gate, where the edit's re-embed/version/lock live | pinned-by: Editing_AppendsAnEditedVersion_AndReEmbedsTheNewText

FILE tests/Mimir.Server.Tests/Ui/EpisodeDisplayTests.cs total=37 c1=1 c2=36 c3=0 c4=0
C2 6-10 | §8.2 list words are pure so pins run without Postgres | pinned-by: whole class
C2 16-17 through 341-342 | Eighteen block comments each restating the rule its adjacent test pins (state words, durations, bounds, anchors, seal phrasing) | pinned-by: the adjacent named tests (see file)
C1 351

FILE tests/Mimir.Server.Tests/Components/Shared/ConfirmArmingTests.cs total=32 c1=0 c2=32 c3=0 c4=0
C2 5-10 | #106 disarm-on-record-change, pinned at the latch seam since nothing renders components | pinned-by: whole class
C2 37-40, 53-57, 70-75, 85-88, 101-103, 117-120 | Each block restates the rule its adjacent test pins (disarm, stay-armed, stale-click refusal both paths, one-shot, resting) | pinned-by: the adjacent named tests

FILE tests/Mimir.Server.Tests/Ui/InjectionBrowserTests.cs total=30 c1=0 c2=30 c3=0 c4=0
C2 7-12 | §8.3 injection log against real Postgres: listing, marks, precision, promote | pinned-by: whole class
C2 15-19 | Spread is bound+1 so the bound must cut exactly one and the ranking picks which | pinned-by: MostRecalled_RanksThisWeeksCarriedWisdom_BoundedAndForgettingLastWeeks
C2 266-267 | The cut entry's mark still feeds precision — the figure itself, not just its inputs | pinned-by: TheListing_BoundsToTheMostRecentEntries_PrecisionCountsThemAll
C2 332-333 | Both items have Provenance, so only Event salience tells them apart | pinned-by: Items_CarryTheSalienceBoostTheirScoreTookFromSection7
C2 365 | The aside is whole-Project whatever the search box says (§9) | pinned-by: TheSearch_NarrowsOnQueryContext_AndNeverMatchesABriefWhichHasNone
C2 404-405 | Every lane keeps a chip at zero | pinned-by: TheLaneFilter_NarrowsTheListing_WhileTheChipsCountEveryLane
C2 436-440 | A filled-bound narrowed listing counts its matches, not the Project total | pinned-by: AFilteredListingThatFillsTheBound_CountsWhatMatched_NotTheWholeProject
C2 495 | Hand-written and other-Project cases don't count as promoted-from-this-log | pinned-by: ThePromotedCaseCount_IsThisProjectsCasesGrownFromEntries
C2 511-513, 523-524, 534 | Fixture-shape rationale: distinct counts one over the bound; seeded weakest-first and interleaved; busiest Wisdom one day outside the window | pinned-by: MostRecalled_RanksThisWeeksCarriedWisdom_BoundedAndForgettingLastWeeks

FILE tests/Mimir.Server.Tests/Ui/SurfaceSearchTests.cs total=25 c1=0 c2=22 c3=3 c4=0
C2 5-9 | Claim rules are pure so they can fail without Docker | pinned-by: whole class
C2 51-55 | The incoming claim wins; the outgoing surface's late release is a no-op | pinned-by: AnOverlappingClaim_Wins_AndTheOutgoingReleaseIsANoOp
C2 73-79 | A new claim starts from an empty term — no surface inherits another's search (#94, #108) | pinned-by: ANewClaim_StartsFromAnEmptyTerm_SoNoSurfaceInheritsAnothersSearch
C2 91-95 | Held by holder identity: a same-holder earlier token stays live and its dispose releases the box | pinned-by: AnEarlierTokenFromTheSameHolder_StillReleases_SoASurfaceReleasesBeforeReClaiming
C3 96-98 | Surfaces must release before re-claiming — the ordering itself is uncaught here without bUnit | pinnable-by: bUnit render test

FILE tests/Mimir.Server.Tests/Ui/EpisodeBrowserTests.cs total=21 c1=0 c2=21 c3=0 c4=0
C2 8-12 | §8.2 queries and sensitive-content hard deletes against real Postgres | pinned-by: whole class
C2 30, 209 | Seeded out of asserted order so a dropped ORDER BY reddens (#77 fixture rule) | pinned-by: TheList_ShowsOnlyTheProjectsEpisodes_NewestFirst + TheDrillDown_NamesTheWisdomThisEpisodeProduced_NewestConfirmationFirst
C2 81, 93-94, 111-113, 143-144, 229-230, 248-249, 280-281 | Each restates the rule its adjacent test pins | pinned-by: the adjacent named tests

FILE tests/Mimir.Server.Tests/Ui/InjectionDisplayTests.cs total=20 c1=2 c2=18 c3=0 c4=0
C2 7-14 | §8.3 pure presentation, deliberately Postgres-free so it fails where Docker isn't | pinned-by: whole class
C2 53-55 | The rebuild never re-measures against the budget — recorded items were injected | pinned-by: Payload_IsNotBoundedByAnyBudget_TheRecordedItemsAreWhatAlreadyFitted
C2 142 | mimir_search is capped by result count; quoting a char budget would invent it | pinned-by: Budget_IsTheLanesOwnCharBudget_AndNothingForMcp
C2 149-150 | Score precision must separate fused scores near a hundredth | pinned-by: Score_KeepsEnoughPrecisionToTellTwoFusedScoresApart
C2 181-184 | Carried-nothing and carried-only-dead are distinct messages; retirement isn't named falsely | pinned-by: CannotPromote_TellsCarriedNothingApartFromCarriedOnlyDeadLines
C1 236, 239

FILE tests/Mimir.Server.Tests/Ui/ChassisBrowserTests.cs total=16 c1=0 c2=16 c3=0 c4=0
C2 7-13 | Every chassis number; each query seeded with a counting and a non-counting row so a dropped predicate half reddens a specific test | pinned-by: whole class
C2 170-175 | Attention figures count Global too — each is its link's list length (#91) | pinned-by: WisdomAttention_CountsGlobalToo_SoEachFigureIsItsOwnLinksList
C2 267-269 | First-run stays false on the Project's existence, even with every Episode deleted (§8.2) | pinned-by: FirstRun_StaysFalse_OnceIntroduced_EvenWithEveryEpisodeDeleted

FILE tests/Mimir.Server.Tests/Components/FirstRun/FirstRunCommandsTests.cs total=15 c1=0 c2=12 c3=3 c4=0
C2 5-7 | The #90 registrations pinned as constants the panel renders | pinned-by: whole class
C2 26-29 | Whole registration object asserted: the key-and-command-in-order version stayed green under reorder | pinned-by: TheSynchronousHooks_CarryNoAsyncFlagAtAll
C2 39-40 | Exactly three async flags, order-of-keys-proof | pinned-by: OnlyTheThreeFireAndForgetHooks_AreAsync
C2 50-52 | README must contain both registrations verbatim (AppSettingsTests shape) | pinned-by: TheRegistrations_AreTheOnesTheReadmeStates
C3 8-10 | Assert per line, never whole-string — raw literals carry the checkout's line endings (CRLF vs LF) | pinnable-by: doc-only (restated in CLAUDE.md)

FILE tests/Mimir.Server.Tests/Ui/UiRegistrationTests.cs total=12 c1=0 c2=12 c3=0 c4=0
C2 6-9 | Descriptors are counted, nothing resolved, so it runs without Postgres | pinned-by: whole class
C2 14-21 | No UI service registered twice (#91/#94 dup was runtime-invisible, #101); amend the test if IEnumerable<T> composition is ever wanted | pinned-by: EveryUiService_IsRegisteredExactlyOnce

FILE tests/Mimir.Server.Tests/Ui/LikePatternTests.cs total=11 c1=0 c2=11 c3=0 c4=0
C2 5-9 | The one search escape, pure and Postgres-free | pinned-by: whole class
C2 24-25, 31-32, 40-41 | Metacharacter/escape-order/ESCAPE-character rationale, each pinned by its adjacent test | pinned-by: AMetacharacter_IsEscapedSoItMatchesItself + TheEscapeItself_IsEscapedFirst + TheEscapeCharacter_IsTheOneThePatternWasBuiltWith

FILE tests/Mimir.Server.Tests/Components/Wisdom/WisdomRouteTests.cs total=9 c1=0 c2=9 c3=0 c4=0
C2 6-10 | One keeper of §8.1 URLs: writer and reader must agree (#91) | pinned-by: whole class
C2 40-43 | An unknown ?show= falls back to the default listing, like ProjectRoute's tab | pinned-by: AnUnknownLens_LandsOnTheDefaultListing

FILE tests/Mimir.Server.Tests/Components/Health/ModelPullTests.cs total=8 c1=0 c2=8 c3=0 c4=0
C2 6-9 | Pull readout from a health snapshot alone — runs where a first run happens (no Postgres) | pinned-by: whole class
C2 38-41 | Two models pulling: the first in §11 declaration order is the decision, not an accident | pinned-by: TwoModelsPullingAtOnce_NamesTheFirstTheTileLists

FILE tests/Mimir.Server.Tests/Ui/EventPayloadTests.cs total=4 c1=0 c2=4 c3=0 c4=0
C2 5-8 | Drill-down reshapes stored payloads: pretty JSON, marker visible, no crash on odd input | pinned-by: whole class

FILE tests/Mimir.Server.Tests/Components/Layout/ProjectRouteTests.cs total=4 c1=0 c2=4 c3=0 c4=0
C2 5-8 | Parse is pure relative-path reading, so it must run everywhere without Postgres | pinned-by: whole class

### tests — Distillation, Recall, Harvest

FILE tests/Mimir.Server.Tests/Distillation/MergeGateTests.cs total=67 c1=7 c2=44 c3=16 c4=0
C2 8-13 | §6 gate: no-match inserts at reinforcement 1/v1 with Provenance; cosine ≥0.80 goes to arbiter; thresholds read the vector leg's cosine | pinned-by: whole class
C2 100-101, 140-141, 230-231, 324, 362-363, 389-390 | Rule restatements beside their pinning tests (FTS-vs-cosine, one-row-per-Event union, Global no-vouch, provenance union, Global-split degrade, no mechanical fallback) | pinned-by: the adjacent named tests
C2 443-444, 466-468, 499-501, 527-528, 540-543, 570-572, 604-606, 685-688, 696-698 | Fixture-shape rationale blocks, each of which an assertion would go red without (rollback with a saved admission, real marker write, caller-tracker isolation, dispose-as-rollback, empty-batch no-lock, staged lock race, chain race, retire/edit axes, re-stamped RetiredAt) | pinned-by: the adjacent named tests
C3 729-740 | The blocked-session probe must poll pg_stat_activity (transactionid locks carry no database oid in pg_locks) and name both advisory+transactionid waits (#70) | pinnable-by: doc-only (restated in CLAUDE.md)
C3 780-783 | The test finalizer writes the conversion marker on the gate's own batch context, mirroring HarvestConverter's §5 shape | pinnable-by: doc-only
C1 624, 717, 754, 773-776

FILE tests/Mimir.Server.Tests/Distillation/DistillationRunTests.cs total=27 c1=0 c2=27 c3=0 c4=0
C2 10-16 | §6 queue turn: Seal→pending→done with Event Provenance; unusable answer→failed with nothing admitted; one batch per Episode | pinned-by: whole class
C2 27-29 | Events seeded newest-first so only the run's ORDER BY can produce seq order (#77 fixture rule) | pinned-by: ASealedPendingEpisode_DistillsToDone_WithEventProvenance
C2 41-43, 73-74, 91-93, 113-118, 147-149 | Rule/fixture restatements beside their pinning tests (whole-stream seam, explicit empty scripts, Failed-not-Done, in-batch rollback, gate-as-reduce) | pinned-by: the adjacent named tests

FILE tests/Mimir.Server.Tests/Distillation/DistillationQueueTests.cs total=24 c1=0 c2=24 c3=0 c4=0
C2 9-15 | Queue surface: the depth figure, the state guard on failure parking, and the two recovery paths sharing one implementation | pinned-by: whole class
C2 18-23, 55-61, 71-72, 110-111 | Depth membership, sweep-vs-boot cutoffs, clock-step-back fixture, done-not-parked rationale | pinned-by: the adjacent named tests

FILE tests/Mimir.Server.Tests/Distillation/WisdomEditNoOpTests.cs total=14 c1=0 c2=14 c3=0 c4=0
C2 5-11 | The §8.1 no-op set is exactly three, has one statement (MergeGate.NoOpOf), and is pinned Postgres-free because it decides a button's clickability | pinned-by: whole class
C2 20-21, 29-33 | Blank settled before any read; only the draft is trimmed, never the stored text | pinned-by: BlankText_IsANoOp_WhateverTheWisdomSays + TextAlreadySaying_ThisIsANoOp_ComparedAgainstWhatIsStored

FILE tests/Mimir.Server.Tests/Distillation/FakeEmbeddings.cs total=12 c1=2 c2=0 c3=10 c4=0
C3 7-12 | Unmapped texts hash to deterministic near-orthogonal unit vectors (|cos| ≲ 0.1 at 1024 dims), far on either side of the 0.80 gate — the determinism the gate tests rest on | pinnable-by: plain test
C3 27-30 | OnGenerate fires as a batch is served (the gate's first step), so a test can change the world exactly as an Admission begins | pinnable-by: plain test
C1 21, 24

FILE tests/Mimir.Server.Tests/Distillation/FakeDistiller.cs total=12 c1=2 c2=0 c3=10 c4=0
C3 6-15 | An exhausted script throws rather than answering nothing, so "distilled to none" must be an explicit Enqueue() and an unscripted call is a loud failure — what keeps one-call-per-Episode falsifiable (#77; no assertion pins the throw itself) | pinnable-by: plain test
C1 22, 25

FILE tests/Mimir.Server.Tests/Distillation/DistillerServiceTests.cs total=12 c1=5 c2=7 c3=0 c4=0
C2 16-21 | §6 worker loop end to end: boot distillation, trigger-only wake, failure parking degrading the tile, dead-process claim re-queued on boot | pinned-by: whole class
C2 76 | The fake clock never ticks, so only the trigger can wake the worker — what makes the trigger assertion falsifiable | pinned-by: ASealTrigger_WakesTheWorkerWithoutTheTimer
C1 127-128, 130-131, 154

FILE tests/Mimir.Server.Tests/Distillation/EpisodeDistillerTests.cs total=11 c1=0 c2=11 c3=0 c4=0
C2 9-13 | §6 Distiller: prompt carries the labelled Event stream and /no_think; candidates come back with kind, scope, capped text, [eN]-mapped provenance | pinned-by: whole class
C2 123-124, 152-155 | Chunk label isolation; a failed chunk lets no partial list out | pinned-by: AnOversizedEpisode_IsDistilledPerChunk + AGoodChunkThenAnUnparseableOne_Throws_LettingNoPartialListOut

FILE tests/Mimir.Server.Tests/Distillation/MergeArbiterTests.cs total=8 c1=0 c2=8 c3=0 c4=0
C2 8-13 | Arbiter contract: JSON-mode prompt with both texts and /no_think in, strictly-parsed verdict out, rewrites capped at 500, unusable answers throw | pinned-by: whole class
C2 97-98 | The schema rides ResponseFormat so Ollama constrains generation to it, not merely asks for JSON | pinned-by: ThePrompt_CarriesBothTexts_NoThink_AndTheVerdictSchema

FILE tests/Mimir.Server.Tests/Distillation/MergeGateGuardTests.cs total=6 c1=0 c2=6 c3=0 c4=0
C2 7-12 | Pre-flight guards live outside PostgresTestBase, over DisconnectedContextFactory, so deleting a guard goes red on machines without Postgres | pinned-by: whole class

FILE tests/Mimir.Server.Tests/Distillation/DistillationSweepServiceTests.cs total=6 c1=0 c2=6 c3=0 c4=0
C2 13-16 | The sweep's boot pass runs DistillationSweep and, having grown the queue, pokes the worker's trigger | pinned-by: whole class
C2 69-70 | The poke proves the pass ran and reported growth; WaitAsync (not a poll) makes a missing poke fail loudly | pinned-by: TheBootPass_RequeuesFailedEpisodes_AndPokesTheWorker

FILE tests/Mimir.Server.Tests/Distillation/TestVectors.cs total=5 c1=1 c2=0 c3=4 c4=0
C3 3-6 | WithCosine(c) = [c, √(1−c²), 0, …] is a unit vector whose cosine against Basis is exactly c — the geometry every gate/rank test's constants rely on | pinnable-by: plain test
C1 11

FILE tests/Mimir.Server.Tests/Distillation/FakeArbiter.cs total=5 c1=0 c2=0 c3=5 c4=0
C3 6-10 | An unscripted call rules Agreement on the existing text — the merge that changes no wording — keeping match-path tests focused on the mechanics they assert | pinnable-by: plain test

FILE tests/Mimir.Server.Tests/Distillation/EpisodeChunkerTests.cs total=5 c1=0 c2=5 c3=0 c4=0
C2 6-9 | §6 chunking: chronological token windows, nothing lost, Remember Events riding in every chunk | pinned-by: whole class
C2 30 | Constants straddle: 100 tokens each against 250 puts exactly two per chunk | pinned-by: AnOversizedEpisode_SplitsChronologically_LosingNothing

FILE tests/Mimir.Server.Tests/Distillation/FakeChatClient.cs total=4 c1=4 c2=0 c3=0 c4=0
C1 5-8

FILE tests/Mimir.Server.Tests/Distillation/DistillationSweepTests.cs total=4 c1=0 c2=4 c3=0 c4=0
C2 9-12 | §6 sweep: failed re-queues, stale running resets, idle unsealed crash-Seal, done never touched, folded §6.4 Contested clear rides along | pinned-by: whole class

FILE tests/Mimir.Server.Tests/Distillation/ContestedSweepTests.cs total=4 c1=0 c2=4 c3=0 c4=0
C2 9-12 | §6.4 flag lifetime: a Contested flag standing 14 days clears; younger flags and everything else untouched | pinned-by: whole class

FILE tests/Mimir.Server.Tests/Recall/QueryRankingTests.cs total=22 c1=0 c2=22 c3=0 c4=0
C2 10-16 | §7 ranking service: fused rank × record factors, affinity as caller input, no threshold of its own, each method names its Candidate Universe | pinned-by: whole class
C2 19, 37, 50, 61-62, 68-73, 86-87, 117-118 | Fixture/rule restatements beside their pinning tests (vector-only vocabulary, fused-edge arithmetic, membership-not-annotation, #58 crowd-out tombstone, FTS-only null cosine) | pinned-by: the adjacent named tests

FILE tests/Mimir.Server.Tests/Recall/BriefServiceTests.cs total=22 c1=1 c2=21 c3=0 c4=0
C2 13-17 | Brief (§7): ambient universe, brief_score order, native-content exclusion, every actual injection logs and empty decisions don't | pinned-by: whole class
C2 30, 126, 161-163, 178-179, 193-194 | Score arithmetic, budget straddles (#83), warning-in-wrapper, degraded-empty honesty | pinned-by: the adjacent named tests
C2 232-235, 237-239 | SlowClock/AutoAdvance mechanics that make the tripwire's "2.1s" deterministic | pinned-by: Brief_ComposedPastTheTimeThreshold_CarriesTheWarning_AndLogsIt
C1 225

FILE tests/Mimir.Server.Tests/Recall/InjectionWrapperTests.cs total=21 c1=0 c2=21 c3=0 c4=0
C2 6-14 | §7 wrapper: authority-disclaiming header, tags, filled to the caller's budget in the caller's order; deliberately Postgres-free | pinned-by: whole class
C2 79-81, 93, 106-107, 127-128, 136-139 | Budget-straddle rationale (#83) beside each pinning test | pinned-by: the adjacent named tests

FILE tests/Mimir.Server.Tests/Recall/PromptRecallServiceTests.cs total=16 c1=0 c2=14 c3=2 c4=0
C2 11-16 | Prompt lane (§7): cosine gate over the ambient universe, ranked injection in budget, every injection logs the prompt, empty decisions leave no trace | pinned-by: whole class
C2 19, 42, 74-75, 86-88, 132 | Fixture arithmetic and NaN-gate rationale beside their pinning tests | pinned-by: the adjacent named tests
C3 144-145 | The test hands one RecallOptions to both ranking and service so an override reaches the whole path, not half of it | pinnable-by: doc-only (mirrors PostgresTestBase.CreateQueryRanking's caveat)

FILE tests/Mimir.Server.Tests/Recall/RecallScoringTests.cs total=16 c1=0 c2=16 c3=0 c4=0
C2 6-11 | §7 score formulas factor by factor; every constant asserted is quoted from the §11 knob table | pinned-by: whole class
C2 28, 34, 36, 38, 52, 56-57, 77, 87, 94 | Per-test constant derivations | pinned-by: the adjacent named tests

FILE tests/Mimir.Server.Tests/Recall/InjectionLogTests.cs total=14 c1=0 c2=14 c3=0 c4=0
C2 7-13 | The one keeper of the §7 recording rules: empty-trace in both shapes, and the row surviving decisions write | pinned-by: whole class
C2 28-29, 39-41, 111-112 | Items-vs-candidates, notice-not-an-injection, Episodes-only-still-logs | pinned-by: the adjacent named tests

FILE tests/Mimir.Server.Tests/Recall/McpSearchServiceTests.cs total=13 c1=1 c2=12 c3=0 c4=0
C2 12-17 | mimir_search (§7): fused Wisdom+Episode results, deliberate reach, documented filters, non-empty answers log lane=MCP, empty leave no trace | pinned-by: whole class
C2 20-21, 97-98, 149-150 | Fixture vocabulary split; filter-before-LIMIT (#67); honest emptiness via off-vocabulary kind | pinned-by: the adjacent named tests
C1 169

FILE tests/Mimir.Server.Tests/Recall/McpRememberServiceTests.cs total=13 c1=0 c2=13 c3=0 c4=0
C2 11-16 | mimir_remember (§4, §7.1): salient save on the most recently active unsealed Episode; else through the Merge Gate — never dropped | pinned-by: whole class
C2 23, 70-74, 91 | Activity-not-start-order; RequestAborted decoupling; 10 KB past the §4 cap | pinned-by: the adjacent named tests

FILE tests/Mimir.Server.Tests/Recall/BriefTripwireTests.cs total=12 c1=0 c2=12 c3=0 c4=0
C2 5-12 | The #72 tripwire is pure arithmetic pinned here, not through a compose (25,001 seeded rows cost minutes of HNSW maintenance); BriefServiceTests pins the wiring | pinned-by: whole class
C2 32, 57-59 | Exceeds-not-reaches; the size leg exists for the machine fast enough to walk a huge corpus inside the wall clock | pinned-by: the adjacent named tests

FILE tests/Mimir.Server.Tests/Recall/InjectionLabelTests.cs total=9 c1=0 c2=9 c3=0 c4=0
C2 6-10 | The one §7 label line, pure by construction so the date rule fails on every machine | pinned-by: whole class
C2 13-16 | The +13:00 value names a different calendar day than its UTC reading, so formatting in the value's own offset cannot pass | pinned-by: Line_RendersTheConfirmedDateInUtc_NotTheValuesOwnOffset

FILE tests/Mimir.Server.Tests/Recall/McpTimelineServiceTests.cs total=4 c1=0 c2=4 c3=0 c4=0
C2 6-9 | mimir_timeline (§7): Episodes newest first with seal state, narrowed by project and since | pinned-by: whole class

FILE tests/Mimir.Server.Tests/Harvest/HarvestScannerTests.cs total=21 c1=7 c2=10 c3=3 c4=1
C2 11-15 | §5 against real Postgres and a real tree: files become HarvestedItems, edits re-version keeping priors, deletions set gone_at; the first scan IS the Backfill | pinned-by: whole class
C2 66-67 | A Project whose known root mangles to the slug wins over the lossy demangle guess (§5) | pinned-by: AHyphenatedRoot_ResolvesByRemanglingKnownRoots_NotByGuessingThePath
C2 184-186 | A briefly unreadable file keeps its state; marking it gone would fabricate a deletion and resurrect a spurious version next scan | pinned-by: AnUnreadableFile_KeepsItsStateInsteadOfGoingGone
C3 25-27 | Create the temp root only after base.InitializeAsync: the no-Postgres skip throws there and xUnit runs no DisposeAsync, leaking a directory per test per run | pinnable-by: doc-only
C4 226 | Guard exists only to satisfy the platform analyzer (RestoreMode is never constructed on Windows)
C1 18, 205-208, 242, 263

FILE tests/Mimir.Server.Tests/Harvest/HarvesterServiceTests.cs total=16 c1=6 c2=9 c3=1 c4=0
C2 17-21 | §5 service loop: boot scan reports on the tile; a SessionEnd trigger rescans with the fake clock never ticking | pinned-by: whole class
C2 81-82, 114-115 | Timer-cannot-rescan arithmetic; scan-succeeds-conversion-fails split | pinned-by: ASessionEndTrigger_CausesARescanWithoutTheTimer + AConversionFailure_DegradesTheTile_ButKeepsTheFreshScanFigures
C3 37 | Same base-first ordering rule as HarvestScannerTests: a temp root created before the skip outlives it | pinnable-by: doc-only
C1 106, 143-144, 148-149, 171

FILE tests/Mimir.Server.Tests/Harvest/HarvestConverterTests.cs total=11 c1=0 c2=11 c3=0 c4=0
C2 11-15 | §5 handoff: every null-marker version flows through the gate exactly once; re-harvested equivalent content reinforces | pinned-by: whole class
C2 22-23, 33-34, 113-114 | Backfill-born rows, count-asserted-first (ShouldAllBe passes on empty), failure-after-the-rest | pinned-by: PendingVersions_FlowThroughTheGateExactlyOnce + AFailingItem_DoesNotBlockTheItemsBehindIt

FILE tests/Mimir.Server.Tests/Harvest/MemorySlugTests.cs total=8 c1=0 c2=8 c3=0 c4=0
C2 5-9 | §5 slug mapping: mangling is lossy, so mangling a known root is exact and demangling an unknown slug is best-effort | pinned-by: whole class
C2 47-49 | Demangle is best-effort by design; accurate mapping is MatchesRoot against known roots | pinned-by: Demangle_CollapsesTheDoubleSeparatorAMangledDotLeaves

FILE tests/Mimir.Server.Tests/Harvest/HarvestCandidatesTests.cs total=8 c1=0 c2=8 c3=0 c4=0
C2 6-9 | §5 mechanical conversion: H1/H2 sections, frontmatter type→kind, the 2,000-char hard cap, no LLM anywhere | pinned-by: whole class
C2 197-198, 235-236 | Horizontal-rule-not-frontmatter; four-backtick fence documenting three-backtick syntax | pinned-by: AFileOpeningWithAHorizontalRule_IsAllBody + ANestedShorterFence_DoesNotCloseTheOuterOne

FILE tests/Mimir.Server.Tests/Harvest/HarvestScanTriggerTests.cs total=2 c1=0 c2=2 c3=0 c4=0
C2 21-22 | Ten sessions ending during one scan mean one rescan, not ten | pinned-by: RequestsWhileNooneWaits_CoalesceIntoOneScan

## Class-3 clusters worth reading as groups

The full class-3 list is inline above (every `C3` row). The biggest clusters, for planning the
sweep's re-homing work:

1. **Blazor surface behaviour with no render tests** — much the largest cluster: the two salvaged
   surfaces (`WisdomSurface.razor` 154 lines, `EpisodeSurface.razor` 62), plus `MainLayout`,
   `EpisodeList`, `AppHeader`, `ProjectSidebar`, `InjectionLogTab`, `EpisodeDrillDown`,
   `ConfirmDelete`, `SurfaceTabStrip`, `FirstRunPanel`, the Pages. Nearly all `pinnable-by: bUnit
   render test` — this cluster is the concrete payoff case for the decided bUnit adoption.
   Recurring per-surface rules: generation guards on stale queries, debounce lanes (feed ceilinged
   / search not), SurfaceSearch release-then-re-claim on Project switch (#108), ConfirmDelete
   subject-key plumbing, first-run cascade tri-state.
2. **Concurrency/race machinery in Capture** — `ProjectResolver` (6 blocks), `CaptureService` (4),
   `DbRaces.cs` (the whole file, 26 lines, c2=0): retry bounds, guarded-update atomicity,
   FK-violation-during-merge recovery. Mostly `plain test` (some races are forceable with two
   contexts, as existing tests show), the retry-bound constants doc-only.
3. **Debouncer discipline** — 57 of its 105 comment lines: lock discipline, token-read-inside-gate,
   the measured #112 race, ceiling constant provenance. Mostly doc-only; the repo's densest
   self-declared-unpinnable cluster.
4. **MergeGate embedding-outside-lock rules** (89-92, 100-102, 188-191) — the only unpinned rules
   in the gate's otherwise heavily pinned transaction story; all `plain test` (script
   FakeEmbeddings to return a short batch).
5. **Schema rationale in MimirDbContext** (25 lines) — index consumers, cascade-behavior choices,
   HNSW-over-IVFFlat; the delete-behavior rules are plain-testable, index rationale doc-only.
6. **Module/architecture statements** (`Modules.cs`, `IMimirModule.cs`, `ModuleRegistration.cs`,
   `StorageRegistration.cs`, `ServerOptions`/`Program.cs` trust boundary) — ADR-adjacent prose;
   doc-only, much of it already stated in ADRs or CLAUDE.md.
7. **CLI process hygiene** (`ProjectLocator` pipe-draining/kill rules, `Program.cs` UTF-8/BOM
   framing, `HookCommand` cap value) — mixed plain-test/doc-only; the 3 s cap constant is
   asserted nowhere (a mutation to 10 s stays green).
8. **Harness contracts in test files** — seeder-uniqueness caveats (three Capture suites),
   PostgresTestBase's RecallOptions same-instance rule, GoldenSuiteTests' must-not-inherit rule,
   Harvest suites' base-first temp-root ordering. Plain-testable in part; several are really
   documentation of the harness's shape.

## Class 4 (tooling) summary

- `<inheritdoc/>` chains: 8 hand-written (EpisodeDistiller.cs:109, DistillationTrigger.cs:19,
  EpisodeFeed.cs:19, HealthState.cs:21, HarvestScanTrigger.cs:18, OllamaModelCatalog.cs:7,
  PostgresStorageProbe.cs:7, CapturedLog.cs:37) plus the generated migration files' chains.
  With no GenerateDocumentationFile, these serve IDE hover/navigation only.
- `#pragma` justifications: exactly one hand-written pair — ProjectMerger.cs:25-26 (EF1002, with
  its injection-safety justification comment). The migrations' 612/618 pairs are generated.
- Generated files: 7 migrations + 7 Designer files + the snapshot, 46 comment lines total, all C4
  except InitialSchema.cs:20-21 (a hand-written C3 rule inside a generated file — see gaps).
- Build-enforced justification: WisdomBrowser.cs:149-151 (internal-because-CS0051) — classified C4
  here because the compiler enforces it, though it fits no C4 sub-kind cleanly.
- HarvestScannerTests.cs:226 — a guard existing only to satisfy the platform analyzer.

## Taxonomy gaps

Kinds of comment the 4-class scheme does not cleanly hold, observed independently by several
classifiers (fresh pass and the failed run's sweepers converged on most of these):

1. **Stale, rule-contradicting comments** — actively wrong prose needing *correction*, not
   deletion or re-homing: `HealthSnapshot.cs:102` ("pending + running" — Failed counts too, and
   DistillerServiceTests pins that), `IMimirModule.cs:10` ("The modules are empty today").
   Neither C1-delete nor C3-rehome is the right verb.
2. **Issue-breadcrumb / decision-record citations** riding inside rule blocks ("(#106)", "(#89)",
   "#101 D6") — provenance of the rule, not the rule; deleting the block loses the only pointer
   from code to the decision record.
3. **Cross-file sync obligations** ("change X, change Y too"): RecallScoring↔InjectionDisplay
   formula prose, ChassisBrowser.Queued↔DistillationQueue predicate, EventPayload's marker regex↔
   PayloadTruncator, BriefTripwire's "3s"↔HookCommand.Cap (cross-assembly), MimirDbContext's
   partial-index↔queue predicate, HarvesterService's two cancellation filters. Rules about future
   edits; each end may be pinned but nothing pins their *agreement*. Some are mechanically
   pinnable (one test running both sides over the same input), some are not.
4. **Deliberate-separation notes** (InjectionLabel vs McpTexts date rules; "not a lane-facing
   seam") — deleting the comment invites the unification/rename it forbids. Same family:
   naming-rationale comments (EpisodeDistiller's "Not 'Options'").
5. **Empirical measurement records** — evidence no test reproduces: Debouncer's "#112 measured six
   escapes in 800,000 racing calls", PostgresStorageProbe's measured NULL-size and snapshot
   behavior, WisdomSearch's #72 benchmark numbers, "Npgsql refuses non-UTC", "pgvector yields NaN
   on zero-norm". Deleting loses the evidence, and a test would be measuring Postgres, not Mimir.
6. **Self-declared-unpinnable defense-in-depth notes** (Debouncer 101-110, BriefService's
   hydration re-assert, QueryRanking's window) — a C3 whose honest pinnable-by is "nothing";
   they document *why no test exists*.
7. **Empty-catch / deliberate-swallow justifications with no compiler hook** (HealthState.cs:59,
   EpisodeFeed.cs:43, ProjectLocator, HookCommand) — pragma-justification spirit, no pragma;
   load-bearing against a well-meaning "fix".
8. **Hand-written comments inside generated files** (InitialSchema.cs:20-21) — a re-scaffold
   silently drops them; neither C3 nor C4 captures that fragility.
9. **Rule-bearing string literals, not comments** — the §6 prompt instruction blocks
   (EpisodeDistiller.cs:88-107, MergeArbiter.cs:43-59) carry more normative content than most
   doc-comments and are invisible to a comment-line inventory (though partially pinned by prompt
   tests). The sweep should not mistake "comments handled" for "normative prose handled".
10. **Deliberate-restatement pairs** (DistillationQueue.cs:20-24 ↔ the two guarded outside writes;
    WisdomBrowser's EditAsync ↔ MergeGate) — CLAUDE.md's own convention: second statements exist
    to be grep targets, so per-block classification cannot express their coupled deletability.
11. **Meta-point for the sweep's decision record**: CLAUDE.md declares doc-comments the carriers
    of normative rules ("Doc comments carry normative rules … grep for the other statements"),
    so "C2 = deletable because a test pins it" conflicts with the repo's documented convention.
    Whether a pinned rule's prose statement should be deleted at all is a policy decision the
    sweep must make first, not per-file judgement.

Also surfaced while classifying (not comment classes, but test-gap findings the sweep should not
lose): AppSettingsTests has per-knob holes (PromptBudgetChars, PromptGateCosine, AffinityBoost,
GoldenSetK asserted nowhere); dropping `.ValidateOnStart()` leaves every options test green;
`CaptureEndpoints`' non-prompt routes (unknown-hook 400, SessionEnd trigger fan-out, compact
re-fire) have no tests at all; "Global Wisdom never earns affinity even under a Global affinity
context" is untested; neither MCP `Since`-normalization path is exercised server-side.

