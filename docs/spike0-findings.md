# Spike 0 findings

Measured on an RTX 5090, ComfyUI 0.34.2 / cu130. Every comparison holds seed, prompt and
inputs fixed except the named variable.

**Conclusion: the consistency strategy the handoff planned around is the wrong one, and the
baseline it dismissed is the right one.**

---

## The pipeline that works

| Concern | Mechanism | Cost |
|---|---|---|
| Subject | Pack-supplied anchor tags per subject, e.g. female or male | free |
| Identity | Appearance tags + a fixed per-character seed | free |
| Expression | Weighted mood tags, e.g. `(laughing:1.2), open mouth, closed eyes` | free |
| Pose and framing | ControlNet OpenPose at strength 0.95 to the end of sampling | ~1 s |
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

## The acceptance test

Six expressions, one seed, one skeleton, rendered through the final pipeline and measured as
alpha-mask overlap against the neutral frame. Overlap is the crossfade criterion: whatever
does not overlap is body that appears in one frame and not the other, and ghosts during the
fade.

| Pose conditioning | Overlap with neutral |
|---|---|
| strength 0.55, ends at 80% of sampling | 85.7 - 89.1% |
| strength 0.95, ends at 100% of sampling | 93.0 - 95.9% |

**Identity and matting pass outright.** Six frames read as one person, in one outfit, with a
clean matte, for both a female and a male character.

**Pose stability passes only at the higher setting.** At 0.55 the skeleton is a suggestion:
the torso lands but arms and framing wander, and a tenth of the sprite differs frame to frame.
At 0.95 the head, torso and crop hold still. What still moves is arms and loose hair, which is
where the residual 5% lives, so a crossfade is close to clean rather than clean. Whether that
is good enough is a judgement to make against a real crossfade in the viewer, not against a
contact sheet.

The male set was rendered at 0.55 only and drifts further than the female one, mostly in
camera distance. It has not been re-measured at 0.95.

## Both love interest genders

Illustrious renders men without any difficulty, but the anchor wording matters as much as it
does for the age anchor, and in the same direction:

| Anchor | Result at one seed, identical appearance tags |
|---|---|
| `1boy, solo, adult` | androgynous, reads young |
| `+ male focus` | marginally older |
| `+ mature male`, with `1girl, feminine` negated | clearly an adult man |

So the subject anchor is pack data, keyed per subject, exactly like the quality prefix and the
ceiling negatives. `1girl` and `mature female` were hardcoded in the compiler until this
point, which meant a male love interest could not be expressed at all.

A PG13 ceiling built only from female-coded terms is also not a PG13 ceiling: `cleavage` and
`nipples` did not stop a bare male chest. The ceiling lists now carry male-coded terms too.

## The candidate picker varies tags, not seeds

Three seeds against identical tags produce the same character in three poses. Three tag sets
at one seed produce three different characters. The seed is what holds one character together
across expressions; it is too weak to be the axis a player picks along.

This qualifies the earlier four-seed result rather than overturning it. Those four faces did
differ, but they differ in framing and hair flow far more than in who they are.

Framing is also unstable across seeds -- `cowboy shot` gave a tight crop at one seed and a
full body at another -- so candidate portraits want the same skeleton the sprites use, or the
player is comparing candidates that are not framed alike.

