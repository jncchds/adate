# Relationship Sim — Spike 0 Handoff

Local-first, picture+text relationship sim. LLM-driven characters, generated
imagery, day/calendar structure, endings at breakup or marriage.

**This document scopes Spike 0 only** (image pipeline proof), but specifies the
structural seams the full system needs so nothing has to be torn out later.

---

## 1. Non-negotiable architectural invariants

These are cheap now and very expensive to retrofit. Honor them even where the
spike doesn't use them.

### 1.1 Composite, never generate whole scenes
Backgrounds and character sprites are **separate images**, stacked as layers in
the browser. Never generate "character standing in cafe" as one image.

- Backgrounds: generated once per location, cached forever
- Sprites: transparent-background PNGs, cached by `(char, outfit, pose, expression, ceiling)`
- Compositing happens client-side as absolutely-positioned `<img>` layers,
  never server-side flattening

### 1.2 The LLM never owns state
(Not exercised in Spike 0 — no LLM — but the boundary must exist.)

Deterministic C# owns all numbers and facts. The LLM writes prose and returns
schema-constrained JSON deltas which C# validates and clamps before commit.

### 1.3 The LLM never writes prompts
The LLM emits a structured **scene intent**:

```csharp
record SceneIntent(string LocationId, TimeOfDay Time, string Outfit,
                   string Pose, string Expression, Framing Framing);
```

`IPromptCompiler` turns intent + character record into a prompt string. One
implementation per prompt dialect (booru tags vs natural language). This is why
style packs are swappable.

### 1.4 Everything is scoped to a save
Every table carries `save_id` from day one, even with exactly one save. Adding
it later means rewriting every query.

### 1.5 Style pack is data, not code
A style pack is a JSON manifest naming checkpoint, LoRAs, dialect, sampler
settings, consistency strategy, resolutions, matting model, supported content
ceilings. Spike 0 ships exactly one pack and hardcodes the selection, but reads
it through the manifest loader.

**A pack is locked per save.** Switching checkpoints destroys character
consistency.

### 1.6 GPU arbitration seam
Spike 0 has one GPU consumer (ComfyUI) so `IGpuLease` is a no-op. It must still
exist and wrap every GPU call. On a 12GB card the real system swaps the LLM and
the image model in and out of VRAM, and that logic has to slot in without
touching call sites.

```csharp
public interface IGpuLease {
    Task<IAsyncDisposable> AcquireAsync(GpuConsumer consumer, CancellationToken ct);
}
// Spike 0: NoOpGpuLease. Later: SwapGpuLease (12/16GB), CoResidentGpuLease (24GB+).
```

### 1.7 Content-addressed image cache
Filename = `sha256(all generation parameters)`. Cache lookup and HTTP caching
become the same mechanism. Serve from `wwwroot/img/{hash}.png` with immutable
headers.

### 1.8 Content ceiling is threaded through from the start
`enum Ceiling { PG13, Suggestive, Explicit }` on the save record and on every
`sprite_cache` row. Spike 0 hardcodes PG13, but the column must exist — cached
sprites generated at one ceiling must never be served into a session at another.

### 1.9 Every character record has an explicit adult age
Set at creation, validated, injected into every generation prompt. Required
because tag-based checkpoints associate terms like "petite" and "youthful" with
juvenile features and will drift without an explicit anchor.

---

## 2. Spike 0 scope

### In
1. Attribute form: eyes, hair color, hair style, skin tone, build, height, one distinguishing feature
2. Compile to prompt via `IPromptCompiler` (booru dialect), fixed token order
3. Generate 4 candidate portraits — same prompt, 4 seeds
4. Player picks one → stored as the **anchor** with its seed
5. Generate 6 expression sprites from the anchor via IP-Adapter, alpha background
6. Generate 1 location background
7. Scene viewer: background layer + sprite layer, buttons to swap expression

### Out
No LLM, no Ollama, no memory, no day loop, no meters, no NPCs, no cast
generation, no acts, no temptation/friendship systems, no ceiling UI, no LoRA
training, no multiple style packs.

### Success criteria — decide pass/fail against these, don't rationalize
- The 6 sprites read as the same person to a stranger. 2+ misses out of 6 = strategy failure.
- Declared attributes survive all 6 sprites. Hair color drifts first — watch it.
- Alpha matting is clean on hair edges. Bad alpha destroys the composite illusion faster than bad faces.
- 4 candidates < 60s; 6 sprites < 90s on target hardware.
- The composite reads as a scene, not a cutout on a photo.

### Fallback ladder if consistency fails
1. Raise IP-Adapter weight
2. Lock a fixed seed per expression slot
3. Tighten/repeat identity tags in the prompt
4. Jump to per-character LoRA (train from approved sprites)

Note: IP-Adapter **FaceID** and InstantID rely on InsightFace embeddings trained
on real faces and perform poorly on anime. For the anime pack use **IP-Adapter
Plus** (full-image reference). FaceID/InstantID are for the photoreal pack later.

---

## 3. Project layout

Create all projects now, even near-empty ones. The dependency directions are the
point.

```
src/
  Game.Core/        domain types, SceneIntent, IPromptCompiler, IGpuLease,
                    StylePack manifest model, Ceiling, character records
  Game.Imaging/     IImageProvider, ComfyUI client, workflow patching,
                    cache addressing
  Game.Llm/         EMPTY IN SPIKE 0 — create the project, add
                    Microsoft.Extensions.AI reference, nothing else
  Game.Data/        SQLite access, schema, repositories
  Game.Host/        Blazor Server app, DI wiring, static image serving
tests/
  Game.Imaging.Tests/
workflows/          ComfyUI API-format JSON + node id maps
stylepacks/         JSON manifests
compose/            docker-compose.yml + .env profiles
```

Dependency rule: `Game.Host` → everything; `Game.Imaging`/`Game.Llm`/`Game.Data`
→ `Game.Core`; `Game.Core` → nothing.

---

## 4. Key interfaces

```csharp
public interface IImageProvider {
    Task<GeneratedImage> GenerateAsync(ImageRequest req, CancellationToken ct);
}

public record ImageRequest(
    string WorkflowId,          // "portrait" | "sprite" | "background"
    string Positive,
    string Negative,
    long Seed,
    int Width, int Height,
    string? AnchorImagePath,    // IP-Adapter reference
    double? AnchorWeight,
    Ceiling Ceiling);

public interface IPromptCompiler {
    string CompilePositive(CharacterAppearance appearance, SceneIntent intent, StylePack pack);
    string CompileNegative(StylePack pack, Ceiling ceiling);
}

public record CharacterAppearance(
    int Age, string EyeColor, string HairColor, string HairStyle,
    string SkinTone, string Build, string Height, string DistinguishingFeature);
```

`CompilePositive` must emit tokens in a **fixed, deterministic order**. Prompt
reproducibility is the foundation of everything downstream.

---

## 5. Data schema (Spike 0)

```sql
CREATE TABLE save (
  id TEXT PRIMARY KEY,
  style_pack_id TEXT NOT NULL,
  ceiling INTEGER NOT NULL DEFAULT 0,
  created_utc TEXT NOT NULL
);

CREATE TABLE character (
  id TEXT PRIMARY KEY,
  save_id TEXT NOT NULL REFERENCES save(id),
  age INTEGER NOT NULL,
  appearance_json TEXT NOT NULL,
  anchor_image_hash TEXT,
  anchor_seed INTEGER,
  lora_path TEXT                  -- unused in spike, reserved
);

CREATE TABLE sprite_cache (
  hash TEXT PRIMARY KEY,          -- sha256 of generation params
  save_id TEXT NOT NULL,
  character_id TEXT NOT NULL REFERENCES character(id),
  outfit TEXT NOT NULL DEFAULT 'default',
  pose TEXT NOT NULL DEFAULT 'standing',
  expression TEXT NOT NULL,
  ceiling INTEGER NOT NULL,
  path TEXT NOT NULL,
  created_utc TEXT NOT NULL
);

CREATE TABLE background_cache (
  hash TEXT PRIMARY KEY,
  save_id TEXT NOT NULL,
  location_id TEXT NOT NULL,
  time_of_day TEXT NOT NULL,
  path TEXT NOT NULL
);
```

SQLite in WAL mode. Raw `Microsoft.Data.Sqlite` is fine for the spike; EF Core
can come later. Keep schema in versioned `.sql` migration files from day one.

Later tables (do not build now, listed so nothing conflicts): `rel_state`,
`canon`, `memory`, `promise`, `schedule`, `turn_log`, `milestone`, `friendship`,
`rejected_stance`, `temptation_arc`, `secret`, `suspicion`, `npc_opinion`,
`player_profile`.

---

## 6. ComfyUI integration

This is the bulk of the real work. Get it running standalone before touching
Blazor.

- Export workflows from the ComfyUI UI in **API format** (not the normal save)
- Store alongside a node-id map so C# never hardcodes graph structure:

```json
{
  "workflowFile": "sprite_ipadapter.json",
  "inputs": {
    "positive": "6.inputs.text",
    "negative": "7.inputs.text",
    "seed":     "3.inputs.seed",
    "anchor":   "12.inputs.image"
  }
}
```

Protocol:
1. `POST /prompt` with the patched graph → returns `prompt_id`
2. Subscribe `ws://comfy:8188/ws?clientId={guid}`; completion is an `executing`
   message with a null `node` for your `prompt_id`
3. `GET /history/{prompt_id}` → output filenames
4. `GET /view?filename=...&subfolder=...&type=output` → bytes

Do background removal **inside the Comfy graph** (BiRefNet or InspyreNet node)
so the client receives a PNG with alpha and never handles matting.

Also implement `POST /free {"unload_models":true,"free_memory":true}` — unused in
Spike 0, required by `SwapGpuLease` later.

---

## 7. Compose

```yaml
services:
  comfy:
    image: yanwk/comfyui-boot:cu124
    ports: ["8188:8188"]
    volumes:
      - ./models:/root/ComfyUI/models
      - ./output:/root/ComfyUI/output
    deploy:
      resources:
        reservations:
          devices: [{ driver: nvidia, count: 1, capabilities: [gpu] }]

  game:
    build: ../src/Game.Host
    env_file: .env.${PROFILE:-12gb}
    ports: ["8080:8080"]
    volumes: ["./data:/data"]
    depends_on: [comfy]
```

Add an `llm` service (Ollama) commented out with a note, so the shape is visible.
AMD needs ROCm image variants.

---

## 8. Build order

1. Compose up ComfyUI. Download checkpoint + IP-Adapter + matting model.
   Produce one image by hand in the web UI. **Confirm VRAM headroom before
   writing any C#.**
2. Export the three workflows in API format. Run each by hand with varied inputs.
3. `Game.Imaging` ComfyUI client. Prove it from a console app — submit, await
   websocket, fetch bytes — before touching Blazor.
4. `IPromptCompiler` + attribute form + 4-candidate grid.
5. Anchor → 6 sprites → composite viewer.

Steps 1–3 hold all the surprises. Step 4 is an afternoon.

---

## 9. Blazor notes

- **Blazor Server**, not WASM. Everything is local; state belongs next to SQLite
  and the GPU. SignalR is also how streamed text and async image-ready pushes
  arrive later.
- Composite as stacked absolutely-positioned `<img>` layers. Expression change =
  CSS crossfade between two cached sprites, zero server work.
- Long generations must not block the circuit: queue the job, push completion
  over SignalR, hold the previous sprite until it lands.
