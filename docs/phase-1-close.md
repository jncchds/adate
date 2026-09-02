# Phase 1 close

What Spike 0 set out to prove, what it actually proved, and what the next phase inherits.

Written at commit `9177de4`. 113 tests green. The stack runs on an RTX 5090 at
`192.168.2.33`; the game runs locally against it.

---

## Where it landed

The full loop works end to end: declared attributes, four candidate portraits, an approved
anchor, six expression sprites matted to alpha, a generated background, and a composite scene
the player can change expression, location and time on. Roughly 7 s per sprite warm.

The last verification run used a 45-year-old female character on a database created under
migration 001, so it exercised the 001-to-002 upgrade on real rows as well as the flow.

## What the handoff got wrong, and what replaced it

Four of the assumptions Phase 1 was built on turned out to be false when measured. Each is
recorded in full in `docs/spike0-findings.md` and `docs/decisions.md`; this is the index.

| HANDOFF assumed | Measured |
|---|---|
| IP-Adapter Plus carries identity | It destroys Illustrious's colour and shading. Identity comes from tags plus a fixed seed. The anchor is a seed, not an image. |
| §1.9's age anchor keeps characters adult | `{N} years old` is not booru vocabulary and moved 1.97% of the image between 19 and 65. Age is tags now, in per-subject bands. |
| The content ceiling is a negative prompt | A positive asking for the opposite wins: 4.8-6.4% against the same prompt with the negatives removed. The ceiling gates the intent instead. |
| One subject, implicitly female | `1girl` and `mature female` were hardcoded, so a male love interest could not be expressed at all. Subject is a pack-supplied anchor. |

The pattern is worth naming: **every one of those was found by measuring something the design
had assumed, and three of the four were my own conclusions being overturned by a later
measurement.** The evaluation harness in `eval/` exists so the next phase can keep doing that
cheaply.

## The content design

Two things are configurable per game: the minimum character age (16 or 18) and whether adult
content is enabled. Neither can lift the under-18 clamp, which is computed from the
character's own age, takes no parameter, and no configuration value reaches.

Enforced in three places on purpose:

* `ContentPolicy.Resolve` is the only thing that produces a `ContentDecision`.
* `ApprovedIntent.Approve` requires a decision, and `IPromptCompiler.CompilePositive` requires
  an approved intent, so no route from a character to an image skips the gate.
* Migration 002 refuses to store a sprite above PG13 for a character under 18, and refuses to
  lower an age below 18 once such art exists — because a cache row outlives the process that
  wrote it.

The line is coverage, not revealingness: PG13 permits anything covered, Suggestive adds
underwear-as-outerwear and sexual posing, Explicit is uncovered or a sex act.

## The checkpoint question

Settled on Illustrious XL v2.0, and this time on evidence rather than on it being the first
thing that worked. `docs/model-evaluation.md` has the numbers. It is the only candidate that
separates all four age bands on both subjects, its negatives bite, it renders declared
features, it has the least sexualised prior of the three that ran, and it is the only anime
candidate whose licence permits commercial use.

Two candidates remain unmeasured: FLUX.2 klein 4B is blocked on system RAM, and Sana was not
enabled.

---

## What the next phase inherits

### Open, and load-bearing

**The crossfade acceptance criterion is not met.** The final run scored 66.9% silhouette
overlap on `angry` against 83-90% on the rest, and the `angry` frame came back in a different
outfit entirely — a purple top and cyan skirt where every other frame has the declared white
blouse and blue skirt. The pose skeleton was uploaded for all six, so the pipeline is intact;
this is the checkpoint reacting to one expression tag at one seed.

That points at something the design does not currently account for: **outfit stability is
seed-dependent, and the four-candidate pick has a hidden second job.** Some anchor seeds hold
an outfit across all six expressions and some do not, and nothing tests for it. A candidate
picker that rendered two expressions per seed and scored their overlap would catch it before
the player commits to a character.

**Nothing consumes the fade-to-black path.** `ContentDecision` carries `Depict` and
`Narration` and nothing reads them yet, because the story side that would produce intents does
not exist. `Game.Llm` is an empty project; `CharacterStudio` authors its own intents for the
Spike 0 flow.

**25-40 against 40+ is the thinnest age boundary.** A game wanting a visibly older love
interest should say so in the appearance attributes rather than relying on the band.

### Open, and smaller

* FLUX.2 klein 4B needs the WSL2 memory ceiling raised, or the fp4 text encoder. The container
  sees 8.28 GB of system RAM and FLUX stages 15.8 GB through it. That limit is unexamined and
  sits under every other measurement on this box.
* Peak VRAM under `--highvram` has never been measured. `nvidia-smi` on the box is the way.
* The male half of the coverage rule is unverified — the test that was meant to check a
  shirtless man had `1girl` in the subject tags.
* `eval/graphs/sana.json` is written from documentation and has never run.

### Deliberately not done

* No LoRA training. The HANDOFF fallback ladder's step 4 was never needed.
* No save/load beyond one save. `save_id` is on every table from day one, which was the point.
* `Game.Llm` untouched. Spike 0 was an image proof.
