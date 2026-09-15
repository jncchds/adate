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
| `Game.Llm` | Scene, reaction and epilogue writers, memory, one client per LLM provider (OpenAI-compatible, OpenAI, Ollama, Google AI) |
| `Game.Play` | The game without a frontend: world, studio, jobs; composed by `AddGame` |
| `Game.Host` | Blazor Server app, and the `measure` harness |
| `Game.App` | Avalonia frontend shared by every native head |
| `Game.Desktop` | Avalonia head for Windows, Linux and macOS |
| `Game.Android` | Avalonia head for Android, built as an APK (not in the solution) |
| `Game.Cli` | Console harness for the image pipeline |

`Game.Core` depends on nothing. Everything else depends on it. `Game.Play` depends on the
libraries; both frontends depend on `Game.Play`, so neither holds a game rule.

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

### 4. The native app

The game runs inside the app, and reaches the language model and Z-Image over the network. The
shipped addresses point at the home GPU box; change them under **Servers and storage**.

```bash
dotnet run --project src/Game.Desktop
```

The Android APK needs the `android` workload and JDK 17 or 21:

```bash
dotnet publish src/Game.Android -c Release -f net10.0-android -o artifacts/android
```

## Releases

Push a branch named `release/<version>` (for example `release/0.1.0`). The release workflow runs the
tests, builds the desktop app for Windows, Linux and macOS, the Android APK and the web host, and
publishes them as the GitHub release `v<version>`. Pushing the branch again replaces that release.

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
