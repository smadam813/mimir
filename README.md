# mimir

The memory and wisdom Claude Code deserves.

Mimir is a fully-offline memory service for a single machine, giving every Claude Code session one
shared brain across all projects. Sessions are captured as Episodes, distilled into Wisdom by local
models, and recalled automatically.

The buildable specification is [`docs/spec/mimir-v1.md`](docs/spec/mimir-v1.md); the vocabulary it
uses is normative and lives in [`CONTEXT.md`](CONTEXT.md).

## Running it

```sh
docker compose up
```

Then browse to <http://127.0.0.1:6464>.

On a clean checkout the first run pulls `qwen3:8b` and `qwen3-embedding:0.6b` — several gigabytes,
and the only progress you get is the Ollama tile in the health strip, which goes from *pulling* to
*ready* live. Postgres and Ollama keep their data in the `pgdata` and `ollama` volumes, so this
happens once.

Compose needs `USERPROFILE` set to your home directory; Windows sets it for you. See
[`.env.example`](.env.example) for the handful of things you can override.

### What works today

The walking skeleton plus the capture path: the Compose topology, the server shell, a health strip
whose Ollama and Storage tiles are real, and `mimir hook` recording sessions as Episodes with
their Events (see below). The Distillation and Harvester tiles are inert placeholders, and there
is no Project sidebar yet — Harvest, Distillation and Recall arrive with the tickets that own
them.

## Capturing your sessions

Claude Code talks to Mimir through the `mimir` CLI (spec §13), a self-contained single-file
executable. Build it once:

```sh
dotnet publish src/Mimir.Cli
```

and put the result (`src/Mimir.Cli/bin/Release/net10.0/win-x64/publish/mimir.exe`) somewhere on
your `PATH` — or use its full path in the commands below. It reads `MIMIR_URL` for the service
address and defaults to `http://127.0.0.1:6464`.

Register the hooks in your **user-level** `~/.claude/settings.json` per the spec §4 table — the
capture hooks are async fire-and-forget; SessionStart and UserPromptSubmit are synchronous because
their replies (the Brief and the Prompt-lane injection, both arriving with the Recall tickets) are
printed into the session:

```json
{
  "hooks": {
    "SessionStart": [
      { "hooks": [{ "type": "command", "command": "mimir hook SessionStart" }] }
    ],
    "UserPromptSubmit": [
      { "hooks": [{ "type": "command", "command": "mimir hook UserPromptSubmit" }] }
    ],
    "PostToolUse": [
      { "matcher": "*", "hooks": [{ "type": "command", "command": "mimir hook PostToolUse", "async": true }] }
    ],
    "Stop": [
      { "hooks": [{ "type": "command", "command": "mimir hook Stop", "async": true }] }
    ],
    "SessionEnd": [
      { "hooks": [{ "type": "command", "command": "mimir hook SessionEnd", "async": true }] }
    ]
  }
}
```

From then on every session lands as an Episode: created at SessionStart, Events appended per
prompt and tool use, Sealed with its reason at SessionEnd. Everything fails open — hooks cap at
3 s and always exit 0, so sessions never break or slow when Mimir is down (spec §1). With Mimir
stopped the hooks simply print nothing and your session proceeds bare.

## Developing

```sh
dotnet test          # the full suite
dotnet run --project src/Mimir.Server
```

Run outside Compose and the server listens on `Mimir:Server:Port` (6464) directly. Inside Compose
the container listens on 8080 and the host publishes 6464, per spec §12 — if you change the
published port, change it in `compose.yaml`.

Both need a Postgres to talk to; `docker compose up -d postgres ollama` is the easy way. Both are
published on localhost, and the `Development` config points at them, so `dotnet run` works against a
half-started stack.

Most of the suite needs nothing running. The storage integration tests do — they pin behaviour that
only a real Postgres exhibits (see ADR-0006) — and they skip themselves with an explanation when
none is reachable. Point them elsewhere with `MIMIR_TEST_POSTGRES`.

Layout, per spec §2:

| Project | What it is |
|---|---|
| `src/Mimir.Server` | The modular monolith: pipeline modules, hosted workers, and the Blazor UI |
| `src/Mimir.Cli` | The host companion — `mimir hook` is live, `mimir mcp` arrives with Recall |
| `src/Mimir.Contracts` | DTOs shared by the two |

Configuration follows the spec §11 knob table. Each section is an options class under
`src/Mimir.Server/Configuration` with its documented default baked in, bound and validated in
`MimirOptionsRegistration`; `appsettings.json` restates the same values so the shipped config
documents itself, and a test keeps the two from drifting.

Schema changes are EF Core migrations:

```sh
dotnet ef migrations add <Name> --project src/Mimir.Server --output-dir Storage/Migrations
```

They are applied automatically at startup.
