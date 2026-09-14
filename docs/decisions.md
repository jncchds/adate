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

## Proportion negatives for women, and not for men

Illustrious exaggerates female figures by default: with the pack exactly as shipped, a
clothed standing sprite came back with an oversized bust and, at some seeds, heavy thighs.
The female subject now negates `large breasts, huge breasts, gigantic breasts, curvy, wide
hips, thick thighs, chibi, big head`.

No positive terms were added. `slim` and `petite` are what this checkpoint associates with
juvenile features, which is the direction HANDOFF 1.9 exists to push away from, so the fix is
subtraction only.

Measured through `xl-sprite` with the `standing` skeleton at 0.95 and the pack's sampler, on
ComfyUI 0.34.5, prompts assembled the way `BooruPromptCompiler` would. Each row is one seed,
identical except for the added negatives:

| Case | Mean RGB diff | Silhouette IoU | Figure area |
|---|---|---|---|
| Female 25+, seed 42 | 3.30% | 93.3% | -3.7% |
| Female 25+, seed 7 | 16.82% | 81.5% | -5.0% |
| Female 18+, seed 42 | 5.33% | 96.9% | -1.2% |

All three read as the same adult with a proportionate figure. The 18+ row is the one that
decided it: it is the weakest maturity anchor the pack emits, and it still reads as a young
adult rather than younger. Seed 7 shows the cost -- at some seeds the change reaches past the
figure, there into skirt colour and length, which the pack leaves unspecified.

The same approach does not work for men. Two sets were tried:

| Case | Set | Mean RGB diff | Silhouette IoU | Figure area |
|---|---|---|---|---|
| Male 25+, seed 42 | full | 2.38% | 88.3% | -6.6% |
| Male 25+, seed 7 | full | 6.47% | 93.0% | -1.5% |
| Male 18+, seed 42 | full | 9.37% | 89.5% | -6.5% |
| Male 18+, seed 7 | full | 5.56% | 87.7% | +7.8% |
| Male 25+, seed 42 | narrow | 11.58% | 88.9% | -5.5% |
| Male 25+, seed 7 | narrow | 5.28% | 93.0% | -2.7% |
| Male 18+, seed 42 | narrow | 9.90% | 90.5% | -6.2% |

Full is `muscular, muscular male, bara, pectorals, abs, broad shoulders, chibi, big head`;
narrow is `bara, huge pectorals, chibi, big head`. Both reduce bulk at 25+, and both turn the
18+ seed-42 render into the narrow-shouldered, soft-faced figure the male subject anchor was
written to get away from. Removing `broad shoulders` did not help, so it is not one term doing
it: on this checkpoint the muscular prior and the adult-male read are the same direction, and
negating one weakens the other. The male subject is unchanged.

If male bulk needs addressing, the lever is more likely the 18+ band's `(mature male:1.0)`
weight than a negative -- but that is a separate measurement, not a conclusion.

## Appearance is chosen from pack vocabulary, and candidates are nearby looks

The new-game form took free text for every attribute, and the candidate picker rendered the
same prompt at four seeds. Spike 0 had already measured that picker as weaker than it read:
seeds alone gave one character in four poses, not four looks.

Appearance is now picked from choices each subject declares in the pack (`features`), with free
text kept only for the distinguishing feature. Each choice may list `near` neighbours, and the
candidates are the appearance exactly as declared plus alternatives that move one or two of
hair colour, hair style and eye colour to a neighbour -- spread across features, and all at one
seed. The seed is what Spike 0 found holds a character together; tag sets at one seed gave
different people, and a neighbour is the smallest tag change there is.

Build, height and skin tone are never varied. The words near build and height are where
juvenile-coded vocabulary lives, and an alternative the player did not ask for must never drift
that way; changing skin tone is not a slight variation of the person described. Moving from free
text to choices also takes `slim` and `petite` out of the input entirely -- the form's old default
build was `slim`.

The approved candidate's appearance replaces the declared one, because sprites are compiled from
the stored record and would otherwise describe someone other than the portrait. The write refuses
a change of age or subject in the same statement that performs it.

The loader refuses a subject missing any feature, a `near` entry that is not itself a choice of
that feature, neighbours on a feature that is never varied, and any choice containing an
always-negative term. The alternatives have not yet been judged on the GPU box: whether one
neighbour at a fixed seed reads as "slightly different" rather than "the same" or "someone else"
is a measurement, and this entry records the design rather than its result.

## Z-Image mattes on the server, and its stack is pinned in the repo

The Z-Image API returned opaque PNGs, but sprites are composited over separately generated
backgrounds (HANDOFF 1.1), so they need alpha. HANDOFF 6's rule still holds — the game never
mattes — so matting went into the server: `/generate` takes `"background": "remove"`, and the
provider asks for it only for workflows listed in `ZImageOptions.MatteWorkflows`.

BiRefNet runs in the same process as the pipeline. A second hop through ComfyUI's native
BiRefNet was the alternative, and would have made ComfyUI a runtime dependency of a game that
otherwise does not need it.

Measured in the running container on the RTX 5090, on a 768x1152 full-body render:

| | |
|---|---|
| Load | 21.5 s cold, once at startup |
| First inference | 5.9 s, paid by a warm-up call at startup |
| Warm inference | 0.16-0.36 s |
| VRAM peak | 1.72 GB, beside Z-Image Turbo |
| Mask | 71.9% transparent, 26.5% opaque, 1.6% soft edge |

Composited over a checkerboard, the hair edges are soft with no background fringe and the gaps
between hair and arms are clear.

The matting model name enters the cache key for matted workflows only. A background's address
does not move when the matting model does.

The stack moved into `compose/zimage/` with everything that can drift pinned: DiffSynth-Studio
at the commit the working image was built from, the torch, transformers, timm and kornia
versions it resolved to, and BiRefNet's Hugging Face revision. The last pin matters most:
BiRefNet loads with `trust_remote_code`, so an unpinned revision executes whatever Python was
pushed to that repository last.

## The art direction stays anime, prompted directly

Three alternatives to prompting Z-Image for anime were tried and rejected. All were rendered
on Z-Image Turbo from the same three subjects: a woman sprite, a man sprite and a cafe
background, with fixed seeds. Sprites were matted and composited over their background.

**Photoreal render plus a cartoon filter.** Photoreal Z-Image output is excellent and
composites well. A classic OpenCV cartoon filter (bilateral smoothing, k-means colour
quantisation, adaptive-threshold edges) turned it into a hard-inked comic look, and fine
background detail into black speckle. It reads as action comic, not romance.

**Photoreal render restyled through img2img.** `/generate` gained `input_image_id` and
`denoising_strength` for this. Six romance-leaning style prompts were tried at 0.55, 0.75,
0.85, 0.92 and 1.0. Turbo runs 8 steps, and the style only takes over when most of them are
re-run:

| Strength | Result |
|---|---|
| 0.55-0.75 | Indistinguishable from the photo |
| 0.85-0.92 | Subjects restyle unevenly: in one scene the man turns cartoon while the woman stays photoreal |
| 1.0 | Real styles, but the input image is ignored, so it is plain prompting |

There is no strength at which the photo's identity survives and the style lands. As a filter,
img2img does not work on this model. The endpoint stays, for the §0.3 fallback of img2img from an
approved portrait, where the goal is holding a picture rather than restyling it.

**Glossy 3D, prompted directly.** Six variants: animated feature, game character, toon 3D,
otome 3D, stylised CGI and soft doll. Two findings ruled the direction out:

* Every man sprite came back with a second figure, even with `single person, solo` in the
  prompt, and matting kept it. The woman's seed was clean every time. So it is seed and framing
  dependent, and the style made it worse than in 2D.
* Three of the six gave the 28- and 30-year-old subjects oversized heads, rounded features and
  small bodies. That is the juvenile-coded read HANDOFF 1.9 exists to push away from. The other
  three leaned young as well.

Two lessons carry into the `zimage-anime` pack whatever its style wording:

* Style words move more than style. "Korean webtoon" changed both subjects' ethnicity. Style
  sentences must be checked against the declared appearance, not assumed neutral.
* Extra figures in a sprite are a real failure mode on Z-Image, and matting preserves them.
  Every sprite measurement needs a check for a second salient figure.

## Z-Image holds a sprite set together without a pose skeleton

`zimage-anime` compiles natural-language prompts (`NaturalPromptCompiler`) and uses
`SeedAndPrompt`: one seed per character, an unchanging identity description, and no
skeleton. Z-Image refuses a skeleton anyway. The question was whether expressions move the
body, as `angry` did in Phase 1, once nothing holds it.

Measured through the real game flow against the deployed server, once per character: new-game
form, four candidates, approve the declared look, six sprites, background. Each seed is derived
from the character id. IoU was taken on 4x-downsampled alpha masks (alpha > 128). A region is
an opaque blob of at least 0.5% of the frame. The bar is at least 85% vs neutral on 5 of 6
frames, with one figure per sprite.

| Character | Frames >= 85% | IoU vs neutral | Worst pair | Regions |
|---|---|---|---|---|
| Woman, 24, red long straight hair, green eyes, pale skin, average build, freckles | 6 of 6 | 92.5-97.3% | laughing/sad 90.6% | 1 in all |
| Man, 30, dark brown short messy hair, brown eyes, fair skin, athletic, tall, trimmed beard | 6 of 6 | 92.4-96.2% | neutral/angry 92.4% | 1 in all |
| Woman, 45, black ponytail, brown eyes, brown skin, toned, tall, glasses | 6 of 6 | 94.6-98.7% | laughing/angry 92.7% | 1 in all |
| Man, 22, blonde ponytail, blue eyes, tanned skin, plump, average height, small scar | 6 of 6 | 86.7-95.0% | neutral/sad 86.7% | 1 in all |

**All four pass.** Illustrious with the skeleton at 0.95 measured 93-96%, so the prompt holds
the body about as well as the skeleton did. The youngest man is the loosest set: sad and
surprised dip his head and shoulders, landing at 86.7% and 87.2%.

No sprite had a second figure, including both men. In the style experiments the male seed grew
one in every 3D variant. The sprite sentence ("only this one person… no other people") and 2D
anime framing are the likely difference, but that was not isolated.

By eye, every set is one person in one outfit, with the pack's default outfit in all 24 frames.
All six expressions read on every character. Every image took 2.7-3.5 s.

**What the measurement does not cover: declared attributes that did not survive.**
Silhouette overlap only proves the body holds still. It says nothing about whether the image is
the character that was declared, and several declarations were lost:

| Declared | Rendered | Kind |
|---|---|---|
| brown skin (woman, 45) | fair skin in portrait and all sprites | skin tone ignored |
| tanned skin (man, 22) | light golden at most | skin tone weakened |
| plump build (man, 22) | athletic | build ignored |
| 45 years old, "middle-aged … forties or fifties" | reads early thirties | age band weak |
| trimmed beard (man, 30) | on the portraits, gone from every sprite | distinguishing feature dropped |
| freckles (woman, 24); small scar (man, 22) | barely visible | distinguishing feature weak |

Hair colour, hair style, eye colour and glasses held on every character. Everything the
2D-anime prior already favours survived, and everything it pulls against (darker skin, a heavier
build, visible age, facial hair on a sprite) was softened or lost. The skin-tone result is the
one that matters most. A player's explicit choice silently not rendering is a correctness bug,
not a style quirk. It is also a bias toward pale anime defaults that the game should not
inherit.

## Declared attributes: wording fixes skin tone, placement alone does not

Three rounds, each regenerating the same four characters at their stored seeds, so only the
prompt differs.

**Round 1: placement only.** Skin and build moved into the subject sentence, and a closing
sentence restated skin, build and the distinguishing feature. Brown skin still rendered fair and
plump still rendered athletic. The beard came back as light stubble and the scar became visible,
so restating helps small features but not what the prior pulls against.

**Round 2: a wording sweep outside the game.** Direct API renders at three seeds per variant,
with prompts in the compiler's exact sentence shape and faces cropped and enlarged:

| Variant | Result, 3 seeds |
|---|---|
| "brown skin" | light peach on all three |
| "dark brown skin" / "a deep brown skin tone" | medium tan on two, light on one |
| "dark-skinned" beside the subject, plus "dark brown skin" | brown on all three |
| "a plump build" / "a chubby, heavyset build" / "a heavyset build with a soft round belly" | athletic on all nine, indistinguishable |
| age band + "45 years old" | no visible change |
| stronger 40+ band ("clearly older than thirty… slightly sagging cheeks") | some added lines on two seeds |

A number did nothing here either, even with a language-model text encoder. An ethnicity word
was deliberately not tried: the player declares a skin tone, not an ethnicity, and the style
experiments already showed such words changing more than they name.

**Round 3: the fix, in the game.** `FeatureOption` gained an optional `Prompt`: the words
emitted in place of the tag, while the tag stays what the player picks and what the character
stores. The compiler puts skin tone right after the subject, and build still follows the age
band (HANDOFF 1.9 is about build and height, not skin tone). The loader and the juvenile-coding
test check prompt wording as well as tags. The skin ladder, rendered at three seeds:

| Choice | Prompt wording | Rendered |
|---|---|---|
| pale skin | pale-skinned with pale skin | pale |
| fair skin | fair-skinned with fair skin | fair, barely darker than pale |
| tanned skin | tan-skinned with tanned golden-brown skin | light tan |
| brown skin | dark-skinned with dark brown skin | brown on all three |
| dark skin | very dark-skinned with deep dark brown skin | **no darker than brown** |

Regenerated in the game with the stronger 40+ band as well, the four characters still pass
consistency, with IoU vs neutral 90.3-97.1% and one figure in every sprite:

| Declared | First run | Now |
|---|---|---|
| brown skin (woman, 45) | fair | brown in all six frames |
| tanned skin (man, 22) | light golden | visibly tanned |
| 45 years old | early thirties | lines at the mouth and eyes, reads around 40 |
| small scar (man, 22) | barely visible | visible in most frames |
| trimmed beard (man, 30) | gone from sprites | light stubble, not a trimmed beard |
| plump build (man, 22) | athletic | still athletic |
| freckles (woman, 24) | barely visible | still barely visible |

Still open, in order of how much they matter:

1. **Dark skin is not distinct from brown skin.** Five choices render as four. That is the
   same class of bug as the one fixed, one step further down.
2. **Build does not move at all with wording.** The half-body framing may hide it, or the prior
   may simply win. The next thing to try is a framing or pose change, not another adjective.
3. **Facial hair and freckles stay faint on sprites.** They are small at sprite resolution, so
   the fix may be a closer framing for talking sprites rather than prompt wording.

## Negative prompts do nothing on Z-Image Turbo

Z-Image Turbo is distilled for 8 steps at cfg 1.0. At cfg 1.0 classifier-free guidance has no
unconditional branch, so the negative should never reach the model. Measured, not assumed:
one positive prompt (the 45-year-old woman), three seeds, and only the negative and cfg vary.
Mean absolute RGB difference, and share of pixels that moved by more than 16:

| Pair | Mean diff | Pixels moved |
|---|---|---|
| Same request twice (control) | 0.000% | 0.00% |
| cfg 1.0: no negative vs the full pack negative | 0.000% | 0.00% |
| cfg 1.0: no negative vs "glasses, eyeglasses, spectacles" | 0.000% | 0.00% |
| cfg 2.0: no negative vs "glasses, eyeglasses, spectacles" | 5.0-7.8% | 18-95% |
| cfg 1.0 vs cfg 2.0, no negative | 5.1-6.1% | 24-30% |

At cfg 1.0 the negative is inert: byte-identical on every seed, including a negative that
directly contradicts the positive.

Raising cfg does not make it a protection. At cfg 2.0 the negative changes the image, but
"glasses" did not remove the glasses the positive asked for: the render just went harsher,
flatter and more saturated. cfg 2.0 alone softened focus and lost the age lines, and every
render took 5.2 s instead of 2.8 s. That is the same lesson as the ceiling measurements on
Illustrious, but stronger: a negative does not beat a positive, and here it does not even try.

**What this means for safety on this pack.** The `alwaysNegative` minor-safety terms protect
nothing at render time, and they never did on Z-Image. What actually holds is structural, and
none of it depends on the model obeying a negative:

* `ContentPolicy.Resolve` clamps anyone under 18 to PG13, from the character's own age;
* restricted-positive filtering removes refused terms before they enter the prompt;
* migration 002 refuses above-PG13 art for a character under 18;
* the loader refuses any appearance choice, tag or prompt wording, that contains an
  always-negative term;
* age bands describe adults in the positive prompt.

**What changed.**

* **Packs declare `negativePrompts`.** It is `Applied` by default and `Ignored` on `zimage-anime`.
  An ignored pack compiles an empty negative in both dialects, because a negative the model never
  sees still changes the cache key and still reads, to anyone looking at a prompt, as a
  safeguard.
* **The Z-Image provider refuses a non-empty negative at cfg ≤ 1.0,** the same way it refuses a
  pose skeleton. A pack cannot quietly claim a protection this provider does not apply.
* **`alwaysNegative` stays required on every pack,** as the vocabulary guard, which is real.

## The cast is generated from contrast profiles, and nothing is stored yet

Phase-2 plan build step 2. The alternatives to a main LI come from `content/contrasts.json`
(Bolder, Opposite, Other life), `content/temper.json` (five two-ended axes) and
`content/wants.json` (wants with families and symmetric conflicts). `CastGenerator` builds one
member per profile, deterministically from a seed.

**The rules it holds, each unit-tested across 60 seeds.**

* **Look budget:** at least three look changes, one of them visible in silhouette.
* **Temper:** at least two axes flipped.
* **Want:** different from every other member's, and related to the main LI's as the profile
  says. Bolder takes the same family, Opposite a conflicting want and Other life a different
  family.
* **Subject** never changes.
* **Age:** an adult main LI's variants stay between 18 and ten years older than them. A main LI
  under 18 passes their exact age to every variant, so nothing generates a minor the player did
  not describe.
* **Player-only choices** are never picked.
* **No two members share a moved hair colour, hair style, aesthetic or age.**

The loader refuses content that could not meet the budget: an axis without exactly two ends, a
one-sided conflict, or a profile with too few or no silhouette dimensions.

**Two decisions came from measurements, not from the plan.**

* **Build is not a silhouette dimension.** No build wording moved the Z-Image render, so a
  variant relying on it would look like the main LI.
* **Hair colour is kept distinct between members.** On the first debug render, Bolder and
  Opposite both came out purple-haired, and the comparison blurred. Only style, aesthetic and
  age had been kept apart.

**Rendered through the real pipeline** on `/cast/{id}`, for the 24-year-old woman and the
30-year-old man. Each main LI and variant is a portrait at its own seed, in its own aesthetic's
outfit, with its temper's resting expression. By eye, each set is four clearly different people.
The woman: red long hair in artsy clothes; a purple bob in a denim jacket; a blue braid with dark
skin in sportswear; and at 29, a camel coat and red ponytail. The man: a suit and beard; blue
cropped hair with glasses in a track jacket; a blonde ponytail with dark skin in a linen shirt;
and at 20, a leather jacket and slicked-back hair.

**Limits, stated so nothing downstream assumes otherwise.**

* **Placeholders:** the main LI's temper, want and aesthetic are placeholders derived from their
  seed, until the new-game flow asks for them (build step 5).
* **Nothing is persisted:** the cast is rebuilt on request and served from the image cache.
  Storing it waits for migration 003.
* **The feature slot:** a variant's added feature replaces the main LI's distinguishing feature
  (the man's beard became glasses), because appearance has one feature slot.
* **Distinctness is not yet measured.** The plan's check needs someone who has not seen the
  design, and that judgement has not been made.

## Settings and places are content; a save's places are rows with their own seed

Phase-2 plan build step 3.

**Place types replace locations.** `content/place-types.json` holds 22 types, one per place in
the three settings. Each type has booru tags, a plain-English description and a vocabulary of
2-3 details, with both wordings. Time-of-day lighting is shared by kind (indoor, outdoor) and
may be overridden per type, so the file does not repeat 110 lighting lines. The loader refuses a
type missing any time of day in either vocabulary. Cafe and park keep their earlier wording as
overrides.

**Settings** are `content/settings/{big-city,small-town,summer-camp}.json`: named places with
details, a routine place, exactly three openings, dated events, occupations and tone. The loader
checks every reference by name:

* place types and details;
* opening and event places;
* event days within the calendar;
* opening and routine places known from the start;
* the id matching the file name.

Summer camp states in its tone that everyone is adult staff. The minimum age is still a game
setting and no setting touches the clamp.

**A save's places are rows.** Each row has a place type, a name, detail ids and its own seed.
The seed is the same at every time of day, so morning and night show the same room. It also
fixes a bug found while building this: the old background seed mixed in `string.GetHashCode`,
which .NET randomises per process, so every restart regenerated every background. A setting is
set once per save, like a style pack. A save created before settings is given the default
setting on first visit.

**Migration 003** adds:

* the save's setting and player columns;
* the `place`, `character_outfit`, `game_clock`, `flag` and `visit` tables;
* the cast columns on `character`.

It enforces the cast rules in the schema as well as in `CastGenerator`, because a stored row
outlives the process that wrote it:

* a variant belongs to a main LI in the same save;
* a variant shares the main LI's subject;
* an adult main LI's variants are adults;
* an under-18 main LI's variants have their exact age;
* a main LI with a stored cast cannot change age or subject.

The cast is now stored the first time it is built and read back afterwards, so it is fixed for
the save.

**One bug found by the tests:** variants were read back ordered by id. Version-7 Guids created
in the same millisecond carry random bits in that position, so Bolder and Other life swapped on
some runs. The query now orders by rowid, which is insertion order.

**Checked against the real database:** the migration ran on the existing save, which picked up
`big-city` and its four known places. The office background rendered with its city-view
detail, and the stored cast read back unchanged.

**Not in this step:** the clock, flags and visits have tables but no behaviour (build step 4), and
nothing yet chooses a setting or an opening (step 5).

## People in backgrounds: fix the place wording, not the "no people" sentence

The in-game Corner Cup background had two tiny pedestrians outside the window. A background is
cached for the life of a save, so a stray figure is permanent. Z-Image ignores negatives, so the
only levers are positive wording and content.

The fix was measured, not assumed. There were three sweeps on Z-Image at midday. Enlarged crops
confirmed every suspected figure.

**Sweep 1: 8 street-facing place types × 3 endings × 2 seeds.**

* **A** is today's sentence, "An empty scene with no people in it".
* **B** is a strong ending: "no one inside, no one outside the windows, no pedestrians and no
  figures in the distance".
* **C** is B plus an early "An empty background scene with no people".

| Place type | A | B | C |
|---|---|---|---|
| Cafe | 0/2 | 1/2 | 0/2 |
| Main street | 0/2 | 1/2 | 1/2 |
| Fairground | 0/2 | 0/2 | 1/2 |
| Office, diner, bar, park | 0/2 each | 0/2 each | 0/2 each |
| Night market | 2/2, vendors | 2/2 | 2/2 |

**Sweep 2: new seeds; A against D (no sentence), and two night-market descriptions.**

| Place type | A | D |
|---|---|---|
| Cafe | 0/3 | 1/3 |
| Main street | 0/3 | 0/3 |
| Fairground | 0/3 | 1/3 |
| Night market, "rows of food stalls" | 3/3 | 3/3, crowds |
| Night market, "after closing, shuttered, unattended" | 1/3 | |
| Night market, "set up but not yet open, empty, unattended stalls" | 0/3 | |

**Sweep 3: all 22 place types × 3 seeds** with A and the new night-market description. One person
in 66 renders: a distant walker on the park path (seed 101), confirmed by an enlarged crop. The
other 65 were empty, including mess hall, staff lounge and campfire circle.

**What this settles.**

* **Naming the thing primes it.** The spelled-out ending added figures where the short sentence
  had none, and removing the sentence added some too. The short sentence stays, and a comment in
  `NaturalPromptCompiler` says why.
* **A description that implies people gets people**, whatever the sentence says. Stalls imply
  vendors. The night-market description now says the stalls are unattended and not yet open,
  and a content test refuses place-type wording with people-implying words (crowd, vendor,
  customer, pedestrian, busy and others).
* **What remains:** about 2 figures in 90 renders with today's wording: one through a cafe window
  onto a street, and one distant walker on a park path. Both are small and far away. Wording
  cannot remove them reliably. The dependable fix would be a person check on the server that
  re-renders with the next seed, which needs a detection model. Not done, because it adds a model
  download to the GPU box.
* **The content guard caught a real case on its first run:** the family shop's "crowded shelves".
  Harmless in the renders, but it is the kind of word that primes, so it now reads "tightly
  packed shelves".
* **The existing Corner Cup background** for the current save keeps its pedestrians. Its seed
  and prompt are unchanged, so the cached image is served again.

Unrelated, and noted for later: some rendered signs carry garbled or Chinese-looking lettering,
on trail signs and bar neon.

## The clock, encounters and turns

Phase-2 plan build step 4, with no LLM.

**The clock** is a day and one of five slots, with Night rolling over to the next Morning. A
setting's calendar ends after its last day. A save's clock starts at day 1, Morning, on first
play.

**Encounters are content,** in `content/encounters/{setting}.json`, plus an optional
`common.json` shared by every setting. An encounter says where it can happen:

* a place id;
* a place held in a flag, for places decided in play such as the main LI's home place;
* or a count of earlier solo visits to that place.

It also says when: slots and a day range. It lists the flags it requires (`key`, `!key`,
`key=value`) and whether it is once-only. It states what it sets and which places it reveals,
and who is there (`main_li` or `variant:{route}`). Each dated setting event becomes an
encounter at priority 100, and its place becomes known on the event day.

**The engine decides; prose never does.** `EncounterEvaluator` picks the highest-priority match,
with ties going to the lowest id, so file order never changes a pick. A once-only encounter records
itself in a flag holding the day it fired. A slot where nothing matches gets placeholder ambient
text. All of it is pure and unit-tested.

**The loader refuses a broken reference by name:**

* an unknown place;
* a reveal of a place the setting lacks;
* a day range outside the calendar or backwards;
* a malformed flag expression or assignment;
* an unknown kind of company;
* empty text;
* a duplicate id.

It also refuses a file named after no setting, because a typo in a file name would otherwise
drop a setting's encounters silently.

**Two content tests keep the content playable.** Every place that starts unknown is revealed by
some encounter or event, since otherwise the player could never go there. Every setting has a
chance-route meeting, reached through solo visits.

**A turn is one transaction.** It advances the clock, records the visit with who was there, sets
flags and reveals places. The clock advances only from the slot the turn was planned at, so the
same turn planned twice (a double click, two tabs) is applied once and the second is refused
with nothing changed.

**`/play/{saveId}`** shows the day, the slot and today's events, with one button per known place.
A turn shows the place's background at the slot the visit happened in, the encounter or ambient
text, and any new place. Checked in the running app on the existing save: day 1 Morning at The
Corner Cup fired the bookshop flyer and revealed Paper Lantern Books, over a morning cafe
background with no one in it.

**Not in this step:**

* Openings, the main LI's home place and the four meeting beats (step 5).
* Relationship state and choices (step 6).
* Endings: the play page stops at the end of the calendar (step 8).
* Characters are named in encounters but not yet drawn in them.

## The four meeting beats are generated from each opening

Phase-2 plan build step 5.

**The beats are built, not authored nine times.** Every opening has the same shape (plan §4), so
`JsonEncounterCatalog` generates its encounters from the opening's own fields:

* **meet** at the meeting place on days 1-2;
* **recognise** at the home place, in the opening's slot, on days 2-4;
* **recognise late** at the opening's new `secondPlace` on days 5-7, if the first was missed;
* **first date** from day 5, once the player has the main LI's number and invites them along.

A new opening is data with no new encounters, and one test plays all four beats through for every
opening in every setting.

**A choice is state, not a page.** An encounter's choice leaves `pending.choice` holding the
encounter id. While it is open no turn can be taken, and it survives a reload. Answering it closes
the flag, records the answer and sets the answer's flags in one transaction, refused unless that
choice is the one open.

**An invite is a transient flag.** It shapes the turn's pick and is never stored, and the map only
offers it where some encounter would honour it.

**The cast is built on approval, not on first view.** A stored temper used to mean a stored cast.
Now the player's temper is stored at creation, so the cast lookup keys on the want, which only
storing the cast writes.

## Story state: numbers and facts belong to C#

Phase-2 plan build step 6, with no LLM.

**Profiles make the cast disagree.** Each member gets their own top desire, and averts the next
member's top desire, so a tag that raises one member always costs another. Weights, dealbreakers,
a hidden need and liked and disliked place types come from `content/values.json` and a seed.

**The rules are content.** `content/relationship.json` holds:

* the per-scene and per-day clamps;
* tag and want values;
* dealbreaker and promise costs;
* stage thresholds;
* per-temper-end scales.

`RelationshipEngine` applies them: fiery swings harder, guarded needs more to move a stage. Stages
advance one requirement at a time and never move back, also enforced by a trigger.

**Facts are append-only.** `FactLedger` decides whether a proposed fact is accepted, a duplicate,
a supersede or a rejection:

* an immutable single value cannot be contradicted;
* a mutable one needs an explaining event;
* claims can be false and never conflict;
* a claim shown on screen is confirmed.

A trigger keeps stored facts from being edited or superseded twice.

**The validator explains itself.** `SceneValidator` returns reasons written for a retry prompt:

* vocabulary;
* contradictions;
* who learns what in a scene they are not in;
* schedules, overridden by the encounter or an open meeting;
* knowledge a character does not have;
* stages a scene gets ahead of.

**In play so far:** choices carry tags, the contact choice is scored for the main LI, a first date
checks the place type, and relationship changes commit inside the turn or choice transaction.
Friend needs the want revealed, which arcs set in step 7, so a playthrough stops at acquaintance
for now.

## Variant routes: temper assigns them, and their beats are generated

Phase-2 plan build step 7, first half.

**Temper picks the route.** `content/routes.json` lists what each route suits:

* routine: open, easygoing;
* introduced: outgoing, playful;
* chance: fiery, guarded.

`RouteAssigner` tries every one-to-one assignment and keeps the one where the most suited temper
ends line up. Ties go to the first in cast and content order, so a cast always gets the same
routes.

**Routes and names are stored once, on first play.** Placeholder names come from the same file,
per subject, and never repeat the main LI's. A save made before routes existed gets them the first
time it is played, and a later call never renames or re-routes anyone.

**Each route's beats are generated**, as the opening's are:

* **routine** meets at the routine place after two solo visits;
* **introduced** meets on an evening out with the main LI once they are dating, a flag written
  when that stage is reached;
* **chance** stays authored per setting, because its place is the point.

Every route then gets a contact choice where they were met, remembered through a `{place}` flag
value, and a first date the player invites them to.

**Everyone in a scene is scored.** Choices and dates change the relationship of each love interest
the encounter is with, not only the main LI's. The map's invite is a picker over whoever some
encounter would honour now.

## Arcs: four generated beats per love interest, and one crisis night

Phase-2 plan build step 7, second half.

**An arc is four beats tied to the person's want**, generated for the main LI and every route, at
the place they are usually found (the main LI's home place, or where a variant was met):

1. **Reveal:** from day 6, once the player has their number.
2. **Obstacle:** from day 9, after a first date, with a choice to help them think it through or
   tell them to be realistic.
3. **Crisis:** at the setting's last event, with a choice to help, stay out, or make it worse.
4. **Resolution:** a choice that addresses their need, or misses it.

The beats set the flags the stages read: want revealed, crisis resolved, need addressed.

**The want is filled in at play time.** Text uses `{want}` and `{need}`, and choice tags use
`helps:{want}` and `hinders:{want}`. The world service swaps in the want of the person the scene is
about before anything is shown or scored, so the catalog stays per setting rather than per save.

**One crisis night, one person.** Plan §7 puts crises for conflicting wants on the same event so
the player cannot help both. Here every crisis lands on the setting's last event, and each needs
the player to bring that person along. The invite picker makes the dilemma visible, which a
same-slot priority tie would have hidden. The cost: two people whose wants do not conflict cannot
both be helped either.

## Endings: people leave at night, and a save ends once

Phase-2 plan build step 8.

**Leaving is a nightly check.** When a turn ends a day, `EndingRules` runs over everyone who has
been met and hasn't left, using thresholds from `content/endings.json`. It applies the rules in
this order:

1. a dealbreaker;
2. suspicion at or over a tolerance divided by the character's temper suspicion scale;
3. too many broken promises;
4. a week without a scene together while still at acquaintance.

The reason is stored as `{key}.left` in the turn's own transaction. From then on every encounter
with that person is filtered out, and they are never offered as an invite, so a closed route stays
closed.

**The ending check** is due on the last day. It is due earlier when exactly one route is open and
it has reached committed. It lists who left and why, offers every open route at dating or above,
and always offers leaving alone. If no route is on offer and someone left, alone is recorded as
`LeftAlone`: the characters' doing, not only the player's.

**Every combination is tested.** Each of four love interests in five situations (unmet, left,
acquaintance, dating, committed), 625 combinations in all, times every pick. Each pick yields
exactly one ending or is refused.

**The recap is stored once**, in `player_profile`, along with its partner check. It names who the
player ended with, who they passed over and who walked away, plus the look, temper, want and
desires of the partner or of the person they were closest to.

**Not yet:** a character asking for commitment mid-story, and stood-up dates as promises. Nothing
in play creates promises yet, so that rule only runs in tests until the LLM or authored scenes make
them.

## The scene writer: C# builds the packet, the model fills a schema, C# decides

Phase-2 plan build step 9, first part.

**A small client, not a framework.** The game needs one call: a chat completion that answers in
JSON. `OpenAiCompatibleClient` posts `chat/completions` with a `json_schema` response format, which
LM Studio, llama.cpp and vLLM all support, and holds the GPU lease for the call. The
`Microsoft.Extensions.AI` reference stays for later, but nothing depends on it.

**The packet is built from stored state only**, in plan §8's order:

1. where and when;
2. who is here, with their temper writing and relationship stage in words;
3. what the player knows;
4. what the people present know;
5. what must happen, which is the encounter's authored text;
6. rules, including the content ceiling.

Facts are trimmed oldest first to stay within about 3,000 tokens. Memories and outfits are not in
it yet.

**The schema narrows before validation does.** Expression, predicate and fact level are enums in
the schema, so a server that constrains decoding cannot produce them wrong. The writer still checks
every answer:

* text length;
* the expression slot;
* no `Core` facts from a scene;
* everything `SceneValidator` checks.

Rejections go back to the model as reasons, up to two retries, and then the authored text is used.
An unreachable endpoint also falls back, but its error is not sent to the model as a reason.

**Nothing waits on the model.** The turn commits first. The play page shows the placeholder and
background, then swaps in the written scene. Accepted facts are stored as known to everyone
present. The packet, answer, attempts and rejections are appended to `turn_log`, so a playthrough
can be replayed.

**Off by default.** `Llm:Enabled` is false, so a machine without a model plays exactly as before.

**The judge reads prose, the validator reads JSON.** Only a second call can catch a scene that
gives someone blue hair when their hair is black. `SceneJudge` gets the scene and the immutable,
non-claimed facts it may use, and returns contradictions that go back to the writer as retry
reasons. A judge that fails to answer passes the scene, so turning it on can only make scenes
better, never block one.

**Places are proposed as ids.** A scene may name a new place as a place type from the catalog (an
enum in the schema), a name, and up to three of that type's detail ids. A name matching an authored
place the player didn't know yet reveals that place rather than duplicating it. Anything else
becomes a known story place with its own seed. The name is display text only.

**The story bible splits as the plan says.** C# picks each person's job from the setting's
occupations, deterministically from the save and person, and already has their want. The model
adds likes and one secret, which are stored as core facts known only to that person, so a scene
can reveal them later. Appearance is written as immutable core facts that the player can see. If
no answer passes, the story runs on the C# picks alone.
