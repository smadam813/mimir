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

The walking skeleton: the Compose topology, the server shell, and a health strip whose Ollama and
Storage tiles are real. The Distillation and Harvester tiles are inert placeholders, and there is
no Project sidebar yet — Capture, Harvest, Distillation and Recall arrive with the tickets that
own them.

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
| `src/Mimir.Cli` | The host companion (`mimir hook`, `mimir mcp`) — stubbed |
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
