# adate inference stack

Runs on the GPU box. The game does not run here — it connects over the network.

## What it provides

| Service | Port | Purpose |
|---|---|---|
| `comfy` | 8188 | ComfyUI, image generation |
| `llm` | 8080 | OpenAI-compatible chat completions |
| `embeddings` | 8081 | OpenAI-compatible embeddings |

All three are independently addressable. The game is configured with a URL per
service and does not care whether they share a machine.

## Requirements

- NVIDIA GPU, 12 GB VRAM or more
- Docker with the NVIDIA container toolkit
- On Windows: Docker Desktop with the WSL2 backend and GPU support enabled
- ~11 GB of disk for the default model set

## First run

```bash
cp .env.example .env          # only needed for gated/private model repos
docker compose --env-file profiles/12gb.env up -d
```

The `provision` container runs first and must finish before ComfyUI starts. It
installs custom nodes and downloads models, and is idempotent — later boots skip
everything already present. Watch it with:

```bash
docker compose --env-file profiles/12gb.env logs -f provision
```

## Verifying from the game machine

```bash
ADATE_Comfy__BaseAddress=http://<gpu-box>:8188 dotnet run --project src/Game.Cli -- stats
```

That reports the ComfyUI version and free VRAM per device, which is the
HANDOFF §8 step 1 question — "confirm VRAM headroom" — answered without opening a
browser.

## Profiles

`profiles/12gb.env` is the product target and is the one that matters. Everything
stays resident in VRAM; no model is ever swapped, because swapping between the
image model and the LLM would cost seconds on every turn that generates art.

`profiles/24gb.env` uses the **same style pack** on purpose. A pack is locked per
save (HANDOFF §1.5), so a save created on a large box must stay playable on a
12 GB one. The extra VRAM buys a longer LLM context, not different art.

## Custom nodes

Two classes, deliberately:

- **ComfyUI-Manager**, unpinned. This stack is usable as a general ComfyUI install;
  add whatever you like through its UI and provisioning will not touch it.
- **The game's own dependencies**, at pinned refs, so a node author pushing a
  breaking change cannot silently alter generation.

`cubiq/ComfyUI_IPAdapter_plus` has been maintenance-only since April 2025. It still
works and is still the right choice for anime IP-Adapter Plus, but it is the single
dependency most likely to need replacing, which is why the pin matters.

## Models

See `provision/models.tsv`. HuggingFace-first: every default entry resolves without
an account or a token. CivitAI entries are supported and will use `CIVITAI_TOKEN`
if one is set, but nothing the game needs is hosted there.

Notable: **BiRefNet needs no custom node.** ComfyUI ships it natively as of PR
\#12747; only the model file is required, in `models/background_removal/`. The
HANDOFF §6 note about installing a BiRefNet or InspyreNet node is out of date.

## Known open questions

- `COMFY_IMAGE` is pinned to the rolling `cu124-slim` tag. Pin it to a dated tag
  once one is confirmed to contain native BiRefNet.
- `IPADAPTER_REF` is `main`. Pin it to a commit verified on real hardware; pinning
  to an unverified sha is worse than not pinning.
- The LLM and embedding model choices are provisional. Nothing in Spike 0 uses
  either — `Game.Llm` is empty by design.
