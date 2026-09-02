# Spike 0 findings

Measured on an RTX 5090, ComfyUI 0.34.2 / cu130, Counterfeit V2.5 fp16, SD1.5.
Every comparison below holds seed, anchor and prompt fixed except the named variable.

---

## Passed

**The pipeline works end to end.** Queue, websocket, history, upload, view, matting,
compositing. Warm timings: portrait 3.5–4.4 s at 512×768, background 4.0 s at 768×512.
Cold start ~100 s for the first image of a session. Four candidates land near 16 s against
the HANDOFF §2 budget of 60 s.

**Alpha matting is clean.** PNG colour type 6, ~66% transparent, 1.9% partial-alpha edge
feathering, individual hair strands survive. This was the criterion HANDOFF §2 warns
destroys the illusion fastest, and it is not a problem.

**Identity holds.** At anchor weight 0.75 the character reads unambiguously as one person
across six expressions. Consistency was never the thing that failed.

---

## Failed

### 1. Expressions do not differentiate at identity-preserving weights

At weight 0.75, six different mood tags produced six near-identical faces. The adapter pins
the face, not just the hair and palette.

**Fixed.** Weight 0.45 plus emphasis on the mood tag — `(laughing:1.4), open mouth, closed
eyes` rather than `laughing` — yields six clearly distinct expressions with identity intact.
Emphasis and weight are both required; neither alone is enough.

### 2. Composition drifts between expressions

With seed, anchor and every other tag held fixed, changing only the mood tag still moves the
pose and the framing — one expression came back with an arm raised.

This matters more than it looks. The scene viewer crossfades between cached sprites on an
expression change, which only reads as one character emoting if the body underneath does not
move. Sprites that differ in pose crossfade as two different drawings.

**Not yet fixed.** See recommendation below.

### 3. Outfit is not controllable

Four different outfit tags produce the same tactical-anime clothing. Two independent causes,
and fixing either alone does nothing:

- **Checkpoint bias.** Counterfeit V2.5 renders this prompt shape as tactical/military
  regardless. Visible in the very first portrait, generated before any IP-Adapter existed.
  Needs `military uniform, tactical gear, harness, straps` and similar in the negative.
- **Adapter lock.** IP-Adapter Plus is a full-image reference and carries the anchor's
  clothing. Only releases near weight 0.05, where identity is gone.

The requested summer dress appears only with both the anti-bias negatives *and* a weight low
enough to abandon identity.

### 4. Framing tags are weak

`upper body`, `portrait`, `close-up` and `face focus` all produced half- and full-body
results. Prompt-level framing control is unreliable on this checkpoint.

---

## Ruled out

**IP-Adapter Plus Face** (`ip-adapter-plus-face_sd15`). The hypothesis was that a
face-trained adapter would carry identity without the reference's clothing. It is worse on
every axis: muddier output, more distant framing, unreadable faces, and the outfit stays
locked. It is CLIP-based and so nominally anime-safe, but it degrades this checkpoint badly.

**Cropping the anchor to head and shoulders.** No effect on the outfit lock. The adapter
transfers more than what is literally in frame.

**Lowering the adapter weight alone.** Trades identity for control on a single axis with no
setting that gives both.

---

## Recommendation

Three of the four failures share one root cause: the prompt is the only lever for
composition, and it is a weak one. The fix is to stop asking the prompt to control pose and
framing at all.

**Add ControlNet OpenPose**, with a fixed skeleton per pose slot:

| Concern | Controlled by |
|---|---|
| Identity | IP-Adapter Plus at ~0.45 against the approved anchor |
| Expression | Weighted mood tags |
| Pose and framing | ControlNet OpenPose skeleton, fixed per pose slot |
| Outfit | Outfit tags, with the checkpoint's bias negated |

A fixed skeleton makes every expression in a set land on the same body in the same frame,
which is exactly what the crossfade needs, and it removes the load the prompt currently
fails to carry. It also lets framing become a real setting rather than a hopeful tag.

This is the standard approach for visual-novel sprite sets, and SD1.5 has by far the most
mature ControlNet ecosystem — which was already part of why SD1.5 was chosen over SDXL.

**Status: untested.** It needs `control_v11p_sd15_openpose`, a ControlNet node in the sprite
graph, and a set of reference skeletons. Everything above it in this document is measured.

### If that fails

The HANDOFF §2 fallback ladder ends at per-character LoRA trained from approved sprites.
That moves identity into the model weights and frees the prompt entirely, which would solve
the outfit problem as well. It is materially more work per character — a training step
between character creation and first play — and is a design decision, not a tuning one.
