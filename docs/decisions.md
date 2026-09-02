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

## Subject is style-pack data, not an enum

HANDOFF 4's compiler sketch hardcodes `1girl` and `mature female`. That made a male love
interest impossible to express, and the game needs both per playthrough.

`CharacterAppearance` now carries a `Subject` key, resolved against a `subjects` block in the
style pack. An enum in `Game.Core` would have been simpler and type-checked, but the tokens
behind each key are checkpoint-specific — measured, `1boy, solo, adult` renders androgynous on
Illustrious and only reads as an adult man once `male focus, mature male` is present with
`1girl, feminine` negated — so they belong with the other pack vocabulary. Adding a subject,
or supporting a checkpoint that needs different wording, is then data rather than a release.

An unknown subject throws rather than falling back. Rendering a male love interest as a woman
because a pack was missing a key is worse than failing the render.

`CompileNegative` gained a `subject` parameter for the same reason: a PG13 list of
female-coded terms did not stop a bare male chest, so each subject carries its own.

Appearance is persisted as JSON, so this needed no migration. A row written before this change
deserialises with a blank subject and fails `Validate` with a message naming the field, which
is the right outcome for a save that predates the concept.

## Outfits and expression phrasing are pack vocabulary too

The studio was passing `casual clothes` as the sprite outfit and the raw slot name as the
expression. Both are placeholder-shaped and both cost measurable consistency: the vague outfit
came back as a different shirt on one of six frames, and bare mood words moved posture rather
than just the face.

Concrete outfits now live on the subject profile, since the right garments differ per subject,
and expression phrasing lives in a pack-level `expressions` map. Both are dialect-specific --
`(crying:1.2), sad, tears` is booru phrasing and means nothing to a natural-language
checkpoint -- so neither belongs in `CharacterStudio`.

The loader rejects a pack missing either. They read as optional polish and are not: the
crossfade is the acceptance criterion, and both of these break it.

## The style pack names its own workflows

`CharacterStudio` hardcoded `"portrait"`, `"sprite"` and `"background"`, which silently meant
the SD1.5 graphs. A pack and its graphs are one unit — an SDXL pack cannot run an SD1.5 graph —
so the binding is a `workflows` block in the pack, and switching packs switches graphs with it.

## Pose skeletons are shipped content

`content/poses/*.png`, copied into the image store on first use, because that is where the
image provider reads pose inputs from. Authored once and reused by every expression in a set:
a skeleton that varied per expression would move the body between frames, which is the thing
the crossfade cannot absorb.

The framing tag has to match the skeleton. A full-body skeleton against an `upper body` prompt
measured 73-77% silhouette overlap where the matched pair measured 93-96%.

## Age bands: the clamp is derived, never configured

A game chooses two things — its minimum character age (16 for a teen-romance setting, 18 for
an adult one) and whether adult content is enabled. It does not choose whether a character
under 18 is limited to PG13. That is computed from the character's own age in
`ContentPolicy.Resolve`, takes no parameter, and no configuration value reaches it.

`Resolve` is the only way to obtain a `ContentDecision`, and a decision is what the generation
path needs, so there is no route from a character record to an image that skips it.
`CharacterStudio` never reads a configured ceiling directly: the configured value is a maximum,
not a decision.

Enforced a second time in the schema, because a cache row outlives the process that wrote it
and is what a later session reads back. Migration 002 refuses to store a sprite above PG13 for
a character under 18, and refuses to lower an age below 18 once such art exists.

Intimacy that exceeds what may be depicted resolves to a fade, not an error — a story reaching
for a moment it cannot show should cut away. For a character under 18 that is the only outcome
intimacy ever has, and the narration is constrained with it: an undepicted scene narrated in
detail has not been faded to black in any meaningful sense.

## Minor-safety negatives cannot live in a ceiling tier

`young` and `baby face` were in the PG13 negative list. Once a game may set its floor at 16,
PG13 is the tier that renders teenagers, and terms describing how a teenager looks cannot live
in the tier that has to render them.

They moved to `alwaysNegative`, applied to every character render at every ceiling with no
path that omits them, and the loader refuses a pack that declares none. The tier lists are now
free to describe their own tier, and the tiers that depict intimacy — adults only by
construction — carry the strongest anti-juvenile terms, because that is where getting it wrong
matters most.

This is prompt-level mitigation, not a guarantee. The checkpoint is Danbooru-trained and will
comply with whatever survives the negatives, so the structural clamp above is what the design
actually rests on.

## Age is tags, not a number

`BooruPromptCompiler` emitted `{N} years old`. Danbooru has no such tag, and measured, it did
nothing: 19 and 65 rendered the same young woman, 1.97% of pixels apart. The same contrast in
booru vocabulary — `mature female` against `old woman, wrinkles, elderly` — moves 9.79%, and
11.91% weighted. Six times the effect from changing the words.

So the HANDOFF 1.9 age anchor was real in intent and inert in practice, and every conclusion
drawn from it needed revisiting. `SubjectProfile` now carries `ageBands`, ordered tag sets
keyed by a minimum age, because the right vocabulary differs per subject (`old woman` against
`old man`) and per checkpoint. The loader refuses a pack whose bands do not reach down to the
absolute floor, so a character with no band fails at load rather than at render.

The 16 band deliberately emits `young adult` rather than any juvenile vocabulary. Pushing a
generator toward juvenile features is exactly what this project should not do, the content
clamp already holds those characters at PG13, and the narrative carries the age. A 16-year-old
rendering as a young adult is the safe direction to be wrong in.

This also retires `age` as a reason to prefer one checkpoint over another: all three scored
equally badly on the numeric form and comparably well on the tag form.

## The content ceiling gates the positive prompt, not just the negative

Measured on Illustrious and NoobAI: `swimsuit, beach` with `nsfw, nude, cleavage, revealing
clothes, suggestive, underwear, lingerie` in the negative rendered a revealing bikini anyway,
moving 6.4% and 4.8% against the same prompt with those negatives removed. The same models
remove a cafe from a background at 24-29% when asked, so negatives are not weak in general —
they lose specifically to a positive asking for the opposite about the subject.

Scene intent is written by the LLM, so subtraction was always the wrong shape: by the time
the term is in the positive prompt it has already won. Packs carry `restrictedPositive` and
the compiler drops restricted terms out of outfit, pose and expression — the only three
fields the LLM writes — before the prompt is assembled. Everything else in that prompt is
authored content or a player-declared attribute and is not filtered.

Terms are dropped silently and individually rather than throwing. An LLM proposing one
unusable outfit should cost the scene that outfit, not fail the turn.

**The line is coverage, not revealingness.** An earlier draft restricted `swimsuit`,
`bikini` and `cleavage` and was wrong: ordinary swimwear is not what a ceiling is for. PG13
permits anything covered; Suggestive adds underwear-as-outerwear and sexual posing; Explicit
is uncovered or a sex act. The over-broad male terms — `open shirt`, `bare pectorals`,
`shirtless` — were dropped for the same reason, since keeping them while permitting a bikini
would apply the rule unequally.
