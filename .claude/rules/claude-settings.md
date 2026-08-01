---
paths:
  - ".claude/settings.json"
---

# Editing `.claude/settings.json`

Every shell rule is written twice, `Bash(...)` and `PowerShell(...)`, and adding one without its twin silently un-gates the rule in half of this repo's sessions. The tool a session reaches for decides the rule prefix, `CLAUDE_CODE_USE_POWERSHELL_TOOL` routes Windows sessions to the PowerShell tool, and the CLI itself emits both forms for a single specifier. A `Bash`-only entry in the **ask** list is the bad case: the guard reads as present and is absent exactly where the hazard lands.

Rules are prefix STRING matches with no flag-level analysis, so write the prefix at the *subcommand* and let the flags fall inside it. `Bash(git reset --hard:*)` is anchored on `--hard` sitting immediately after `reset`, and `git reset -q --hard HEAD~1` — a spelling this repo's own transcripts contain — walks straight past it. The whole ask list is therefore subcommand-wide, and the cost of that breadth was measured before it was accepted: `git reset` appears 7 times across 120 transcripts, `git clean` once.

Use the `:*` suffix, uniformly. A trailing ` *` is equivalent, and a bare command with no suffix is an exact match — but `Bash(cmd sub*)` with no space is *also* a prefix wildcard, one that additionally matches `cmd subx`. Three spellings, two meanings, and the difference is a space. Sticking to `:*` removes the question. It matches the bare command too, so a `Bash(docker compose ps)` / `Bash(docker compose ps:*)` pair is one dead line, not two entries.

Two gaps no pattern closes, so don't read the ask list as a fence:

- `git -C <dir> checkout ...` routes around every rule here, because the prefix no longer starts at the subcommand.
- The list gates spellings, not intent. It exists for one scenario — an agent that has deliberately broken a file mid-mutation-check and reaches for the quickest restore — and buys a prompt at that moment, nothing more.

**No deny rules, ever.** A committed deny merges across scopes and cannot be overridden even locally, so it is the one list that can leave a contributor's checkout worse off than no settings file at all.

## What is deliberately absent

Check this list before "fixing" an omission; each was measured and declined.

- **Anything the harness already vets.** `gh pr view`, `gh pr checks --watch` (the one CLAUDE.md mandates), `gh issue view/list`, `gh run view/list`, `git status/log/diff`, `ls`/`cat`/`grep` and the rest are matched against a built-in table that enumerates safe flags per subcommand. A prefix rule for one of those is not a no-op — it is *wider* than the built-in, because it reinstates the flags the table screens out.
- **`gh api`** (290 calls, the single most-run command) — genuinely not in that table, and left out on the same ground as `psql`: it is a general-purpose client for the whole GitHub API, and `--method DELETE` shares its prefix with every read.
- **`docker exec mimir-postgres-1 psql ...`** (180 calls) — arbitrary execution in the container; an allowlisted `psql` can `DROP` as easily as `SELECT`.
- **`git fetch`** (92 calls) — a refspec such as `git fetch origin '+refs/heads/*:refs/heads/*'` force-updates local branches, discarding local-only commits, and no prefix scoping excludes it. Recoverable only via reflog, which would put a more destructive operation on the allow list than the `git reset --hard` the ask list gates.
- **`dotnet run --project src/Mimir.Server`** (57 calls) — a long-running foreground process, and one left running locks `Mimir.Contracts.dll`/`Mimir.Server.dll` so the next build fails MSB3027. A prompt is the checkpoint.
- **`dotnet tool install --global csharp-ls`** — one-time human setup, not a session action.

The `dotnet test`/`build`/`restore` entries *are* accepted arbitrary host execution, and knowing that is the point of the paragraph. `dotnet test --logger:X;<path>` loads an arbitrary assembly, `/p:CustomBeforeMicrosoftCommonTargets=<path>` runs arbitrary MSBuild targets, and the rules are not scoped to this checkout. Pinning them to `Mimir.slnx` would break the ~100 per-project `--filter` invocations that are the point of having them and would not close the `-p:` hole anyway. The precondition for any of it is a session already running attacker-controlled text.
