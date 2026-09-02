# adate

Local-first, picture-and-text relationship sim. LLM-driven characters, generated imagery,
day/calendar structure, endings at breakup or marriage.

**Currently at Spike 0** — the image pipeline proof. See [HANDOFF.md](HANDOFF.md) for the
architecture and for what Spike 0 does and does not cover.

## Shape

Three independently addressable services. They are not assumed to share a machine, and each
is reached through a configured URL.

```
┌─────────────┐        ┌──────────────────────────────────┐
│  Game.Host  │        │        GPU box (compose/)        │
│  Blazor +   │───────▶│  ComfyUI  8188                   │
│  SQLite     │        │  LLM      8080  (OpenAI-shaped)  │
│  (any box)  │───────▶│  embed    8081                   │
└─────────────┘        └──────────────────────────────────┘
```

| Project | Contents |
|---|---|
| `Game.Core` | Domain types, `SceneIntent`, `IPromptCompiler`, `IGpuLease`, style pack model |
| `Game.Imaging` | `IImageProvider`, ComfyUI client, workflow patching, content-addressed cache |
| `Game.Data` | SQLite schema, migrations, repositories |
| `Game.Llm` | Empty by design — Spike 0 has no LLM |
| `Game.Host` | Blazor Server app |
| `Game.Cli` | Console harness for the image pipeline |

`Game.Core` depends on nothing. Everything else depends on it. `Game.Host` depends on
everything.

## Running

### 1. The GPU box

```bash
cd compose
docker compose --env-file profiles/12gb.env up -d
```

Provisioning installs custom nodes and downloads ~11 GB of models on first boot, then exits.
See [compose/README.md](compose/README.md).

### 2. Check it from the game machine

```bash
ADATE_Comfy__BaseAddress=http://gpu-box:8188 dotnet run --project src/Game.Cli -- stats
```

Reports ComfyUI's version and free VRAM per device.

### 3. The game

```bash
ADATE_Comfy__BaseAddress=http://gpu-box:8188 dotnet run --project src/Game.Host
```

## Status

| | |
|---|---|
| Solution, project layout, dependency directions | done |
| ComfyUI client (queue, websocket, history, view, upload, free) | validated against a real server |
| Content-addressed image cache | done |
| Booru prompt compiler + style pack loader | done |
| SQLite schema, migrations, repositories | done |
| Blazor form, candidate grid, composite viewer | done |
| The three ComfyUI workflow graphs | written and validated on hardware |
| Anything LLM | not started, by design |

76 tests, and the client has now been exercised against real hardware.

## Departures from HANDOFF.md

Four, all deliberate. Recorded in [docs/decisions.md](docs/decisions.md).

## Spike 0 results

Measured on hardware, not predicted: [docs/spike0-findings.md](docs/spike0-findings.md).
The pipeline, the matting and character consistency all pass. IP-Adapter turned out to be
unnecessary and actively harmful; identity comes from tags plus a fixed seed, pose from a
ControlNet skeleton. About 9s per sprite.
