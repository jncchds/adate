# Decisions

Departures from [HANDOFF.md](../HANDOFF.md), and the reasoning. The handoff stays as
written; this file records where the implementation knowingly differs and why.

---

## 1. `AnchorImagePath` became `AnchorImageHash`

**Handoff §4** gives `ImageRequest` an `AnchorImagePath` for the IP-Adapter reference.

That assumes ComfyUI shares a filesystem with the game. It does not: the image service is
independently addressable and normally runs on a different machine. A local path is
meaningless to a remote ComfyUI.

The anchor is uploaded via `POST /upload/image` — an endpoint the handoff's §6 protocol list
omits — and referenced by the name ComfyUI returns. The request carries the anchor's content
hash so the cache key still distinguishes one anchor from another.

## 2. `ImageRequest` gained `PackFingerprint`

Not in the handoff's §4 sketch, and required for correctness.

The cache is content-addressed on generation parameters (§1.7), but the checkpoint, LoRA set
and sampler settings live in the style pack, not in the request. Without the pack in the key,
an identical prompt and seed under a different checkpoint would collide with cached art from
the previous one — and being content-addressed, it would never be regenerated.

The fingerprint is a hash of the pack manifest's raw JSON, so any edit invalidates art made
under the previous version.

## 3. `IPromptCompiler` gained a `RenderTarget` parameter

**Handoff §4** gives `CompilePositive(appearance, intent, pack)`.

One intent cannot serve both a sprite and a background. A sprite is matted to alpha and
composited over a separately generated background, so it must contain no scenery — anything
generated there survives matting as a fringe, which §2 names as the fastest way to destroy
the composite. A background is cached for the life of the save, so a stray figure in one is
permanent.

`RenderTarget` makes that distinction explicit rather than leaving it to whoever builds the
intent to remember.

## 4. BiRefNet no longer needs a custom node

**Handoff §6** says to do background removal inside the graph with a BiRefNet or InspyreNet
custom node.

ComfyUI ships BiRefNet natively as of PR #12747. Only the model file is needed, in
`ComfyUI/models/background_removal/`, from `Comfy-Org/BiRefNet` on HuggingFace. The
architectural instruction is unchanged and still correct — matting happens inside the graph
and the client only ever receives a PNG with alpha — but the custom node dependency is gone.

---

# Choices the handoff left open

## Style pack: SD1.5, not SDXL

The 12GB target has to hold the image model and the LLM at once, because swapping between
them would cost seconds on every turn that generates art.

| | SD1.5 @ 768px | SDXL @ 1024px |
|---|---|---|
| Image peak with IP-Adapter | ~4.0 GB | ~7–8 GB |
| Qwen3-8B Q4_K_M + 8k KV | ~6.0 GB | ~6.0 GB |
| **Total** | **~10 GB — fits** | **~14 GB — does not** |

SDXL-family checkpoints look better. They do not fit. SD1.5 is also where IP-Adapter Plus and
ControlNet are most mature, which is what Spike 0 is actually testing.

The 24GB profile deliberately keeps the same pack. A pack is locked per save (§1.5), so a
save created on a large machine must stay playable on a small one. Extra VRAM buys LLM
context, not different art.

## The LLM is any OpenAI-compatible endpoint

There is no official LM Studio container image — it is an Electron desktop app, and while
`lms server start` gives a headless mode, containerising it is not vendor-supported.

It does not matter, because LM Studio exposes an OpenAI-compatible API on `:1234/v1`. The
game binds to that API shape behind a configured URL, so LM Studio, Ollama, llama.cpp and
vLLM are interchangeable. The compose stack ships llama.cpp for machines without LM Studio;
pointing the game at a native LM Studio instead is a configuration change and nothing more.

## Websocket per generation, not persistent

Generation is bursty. Sprites are cached forever by `(character, outfit, pose, expression,
ceiling)`, so in steady-state play most turns generate nothing at all. A persistent socket
would idle almost permanently while needing reconnect and resubscribe logic to earn its keep.

The socket is opened *before* the prompt is queued — a fast graph can finish before a
later-opened socket attaches, losing the completion message entirely — and it is treated as
an optimisation, never as truth. `/history` is authoritative, and covers both a dropped
socket and nodes served from ComfyUI's own execution cache, which emit no frame.

## ComfyUI-Manager *and* pinned nodes

Not a compromise. The Manager is installed unpinned so the stack stays usable as a general
ComfyUI install; the nodes the game itself depends on are cloned at pinned refs so a node
author pushing a breaking change cannot silently alter generation.

`cubiq/ComfyUI_IPAdapter_plus` has been maintenance-only since April 2025. It still works and
is still the right choice for anime IP-Adapter Plus, but it is the dependency most likely to
need replacing — which is exactly why the pin matters.

---

# Known open items

- `COMFY_IMAGE` is on the rolling `cu124-slim` tag. Pin it to a dated tag once one is
  confirmed to contain native BiRefNet.
- `IPADAPTER_REF` is `main`. Pin it to a commit verified on real hardware — pinning to an
  unverified sha is worse than not pinning.
- The three workflow graphs do not exist yet; they need a running ComfyUI to author and
  export. See [workflows/README.md](../workflows/README.md).
- The ComfyUI client has never spoken to a real ComfyUI server. The protocol tests are the
  closest thing to proof until it does.
