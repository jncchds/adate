# Spike 0 findings

Measured on an RTX 5090, ComfyUI 0.34.2 / cu130. Every comparison holds seed, prompt and
inputs fixed except the named variable.

**Conclusion: the consistency strategy the handoff planned around is the wrong one, and the
baseline it dismissed is the right one.**

---

## The pipeline that works

| Concern | Mechanism | Cost |
|---|---|---|
| Identity | Appearance tags + a fixed per-character seed | free |
| Expression | Weighted mood tags, e.g. `(laughing:1.2), open mouth, closed eyes` | free |
| Pose and framing | ControlNet OpenPose, one authored skeleton per pose slot | ~1 s |
| Transparency | BiRefNet in-graph, native to ComfyUI | ~1 s |
| **Total** | Illustrious XL v2.0 @ 832×1216, 30 steps | **~9 s per sprite** |

No IP-Adapter. Nothing is patched into the UNet, which is why outfit, pose and expression all
stay controllable.

---

## How that was reached

### Character consistency was never the problem

HANDOFF §2 expected identity drift to be the risk, with IP-Adapter Plus as the answer and a
per-character LoRA as the fallback. Measured, identity holds fine from tags and a fixed seed
alone: six expressions from one seed read as one person, and four different seeds against the
same tags read as four different people. That second result is what makes the handoff's
four-candidate pick meaningful — **the anchor is a seed, not an image**, which the schema
already anticipated with `character.anchor_seed`.

### IP-Adapter actively damages the art

The decisive test was an ablation at one seed:

| Configuration | Result |
|---|---|
| Tags only | correct dark red hair, black skirt, clean shading |
| + ControlNet only | **identical quality**, plus a clean alpha matte |
| + IP-Adapter only | oversaturated magenta, flat posterised shading, hair no longer red |
| + both | same damage |

ControlNet is harmless. IP-Adapter Plus SDXL is what breaks Illustrious. Cause not
established — a CLIP-vision mismatch is the usual suspect for exactly this colour shift — but
it does not matter, because identity does not need it.

This also retires the earlier finding that IP-Adapter locked the outfit at every weight that
preserved identity. It was a real observation about a component we no longer use.

### Illustrious fixed what prompt tuning could not

Counterfeit V2.5 ignored outfit tags (everything came out tactical/military), ignored framing
tags, and never rendered a declared attribute like freckles. Illustrious honoured all three on
the first try with no prompt tricks. Three of the four recorded Spike 0 failures were the
checkpoint, not the technique.

### The 148 s sprite was a disk problem, not a compute one

ComfyUI evicted the ControlNet and IP-Adapter after every generation and re-read them from a
spinning disk on the next. The overhead tracked adapter size almost exactly linearly at
~24 s/GB, and repeat runs never got faster. `--highvram` on the 24 GB profile stops the
eviction: cold start is ~270 s while ~13 GB is read off the disk, and every run after that is
~9 s.

Do not set `--highvram` on the 12 GB profile; there, keeping everything resident is an
out-of-memory error rather than an optimisation.

---

## Ruled out

- **IP-Adapter Plus Face** (`ip-adapter-plus-face_sd15`). Worse on every axis on anime:
  muddier, more distant, unreadable faces, outfit still locked.
- **Cropping the anchor to head and shoulders.** No effect on the outfit lock; the adapter
  transfers more than what is literally in frame.
- **Lowering adapter weight.** Buys one axis of control at the cost of identity, with no
  setting that gives both.
- **`comfyui_controlnet_aux`.** Failed to install, and turned out to be unnecessary: ComfyUI
  ships `SDPoseKeypointExtractor` and `SDPoseDrawKeypoints` natively. SDPose needs its own
  checkpoint with a heatmap head.

---

## Consequences for the design

**A style pack is now a much smaller thing.** With no adapter, a pack is a checkpoint, a
sampler configuration, a prompt vocabulary and a matting model. `ConsistencyStrategy` for the
Illustrious pack is `SeedAndTags`.

**Pose slots are authored content.** A skeleton is extracted once per pose and checked in,
then reused by every expression in that set. That is what lets the scene viewer crossfade
between expressions without the body moving underneath.

**`AnchorImageHash` is no longer needed by this pack**, though it stays on `ImageRequest` for
the SD1.5 pack and for any future strategy that does reference an image.

---

## Still open

- The six-expression set has not been rendered through the final pipeline and judged as a
  crossfade. That is the remaining Spike 0 acceptance test.
- `StudioOptions.StylePackId` still points at `counterfeit-anime`; the Blazor app has never
  been run against Illustrious.
- `CharacterStudio` still passes an anchor image hash for sprites. Under `SeedAndTags` it
  should pass the character's stored `anchor_seed` and a pose skeleton instead.
- Peak VRAM under `--highvram` is unmeasured. Sampling `system_stats` proved too coarse;
  `nvidia-smi` on the box is the way to get a real number.
