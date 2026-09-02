# Checkpoint evaluation

Illustrious was never chosen over alternatives. Counterfeit V2.5 failed on outfit, framing and
declared attributes; Illustrious was the first replacement tried and it worked. That makes it
sufficient, not best, and nothing here has compared it to anything.

This is the protocol that fixes that.

## What is measured, and why these things

Each case renders two images identical except for one named variable, and scores the mean
absolute pixel difference between them. The number answers *did changing this move the image,
and by how much*. It says nothing about whether the change was the right one — that is what
the contact sheet is for, and every case is looked at as well as scored.

| Case | Question | Reading |
|---|---|---|
| `age` | Does the declared age change the image at all? | higher better; near zero means inert |
| `negative` | Does the negative prompt do anything? | higher better; zero means that safety layer does not exist |
| `subject` | Are an adult woman and an adult man different people? | higher better |
| `outfit` | Does a declared outfit survive a change of expression? | **lower better** |
| `feature` | Does a declared distinguishing feature appear? | visual only |

`age` exists because a game may set its floor at 16, so an adults-only tier has to be able to
make an adult read as an adult. `negative` exists because the minor-safety terms in
`alwaysNegative` *are* a negative prompt, and on a guidance-distilled model they attach to
nothing. Both criteria come from the content-policy work, not from image quality.

## Running it

```bash
ADATE_COMFY=http://192.168.2.33:8188 node eval/run.mjs illustrious pony noobai flux2-klein
```

```bash
powershell -File eval/score.ps1 -Models illustrious,pony,noobai,flux2-klein
```

Candidates other than Illustrious need `models-eval.tsv` in `MANIFESTS` and the stack
recreated; the 24GB profile ships with it enabled. Sana is a separate manifest and a separate
flag and needs both: append `models-sana.tsv` to `MANIFESTS` and set `INSTALL_EXTRA_MODELS=1`.

Everything stateful in the stack is a bind mount, so `docker compose down` destroys containers
and nothing else. Weights already on disk are skipped, so recreating downloads only what is new.

Booru and natural-language phrasings of every case are held side by side in `eval/cases.json`,
and a model is sent the one matching its dialect. Sending booru tags to a natural-language
model would measure the dialect and not the model.

## Results

Measured on an RTX 5090, ComfyUI 0.34.2 / cu130, seed 4242, 832x1216.

| Model | age | agetags | ageweight | ageadult | negative | subject | outfit ↓ | s/image |
|---|---|---|---|---|---|---|---|---|
| Illustrious XL v2.0 | 1.97% | 9.79% | **11.91%** | 9.24% | 24.43% | 10.71% | 11.58% | 6.1 |
| Pony Diffusion V6 XL | 2.25% | — | — | — | 12.52% | 8.55% | 11.43% | 6.1 |
| NoobAI-XL v1.1 | 4.77% | 9.03% | 10.83% | **9.78%** | **28.63%** | **12.32%** | **6.54%** | 6.1 |
| FLUX.2 klein 4B | blocked | blocked | blocked | blocked | blocked | blocked | blocked | — |
| Sana 1.6B | not run | not run | not run | not run | not run | not run | not run | — |

**The `age` case was measuring the wrong thing.** See the next section: the number in the
prompt was never booru vocabulary, and asking in tags instead moves the score from 1.97% to
11.91% on the same checkpoint. The row is kept because it is the honest record of what
"`{N} years old`" achieves, which is nothing.

**No model passes `age` as originally phrased.** 4.77% is the best of three and is still, by
eye, the same young woman at 19 and at 65. Every candidate that ran failed it identically,
which was the first clue that the phrasing rather than the checkpoint was at fault.

## The age tag was our bug, not the model’s

`age` contrasts "`19 years old`" with "`65 years old`". Danbooru has no such tag. The three
cases added afterwards ask for the same contrast in vocabulary the models were trained on:

| Case | Phrasing | Illustrious | NoobAI |
|---|---|---|---|
| `age` | `19 years old` vs `65 years old` | 1.97% | 4.77% |
| `agetags` | `mature female` vs `old woman, wrinkles, elderly` | 9.79% | 9.03% |
| `ageweight` | the same, weighted 1.3-1.5 | **11.91%** | 10.83% |
| `ageadult` | `young adult` vs `middle-aged` | 9.24% | 9.78% |

Six times the effect from changing the words alone, on the same checkpoint and the same
seed. By eye it is not subtle: `agetags-b` is a genuinely elderly woman with a lined face,
and the model greys her hair unprompted even though hair colour is held brown in both
variants.

`ageadult` matters more than the extremes. A young adult against a middle-aged one is the
narrow gap the content design actually depends on, and it scores 9.24% -- nearly as much as
20-versus-80. So this is usable, not a trick at the ends of the range.

**This reverses the earlier conclusion.** The checkpoints can render age. Our prompt could
not ask for it, because `BooruPromptCompiler` emitted a number where the training data has
tags. The fix was in the compiler, and each pack subject now carries an `ageBands` block.

It also means `age` was never a reason to prefer one checkpoint over another, and the
evaluation is decided on the other criteria.

## Read by eye

**NoobAI wins on the numbers and loses on its prior.** Best negative bite, best subject
separation, and much the best outfit stability at 6.54% -- the body barely moves between
expressions, which is exactly what the crossfade needs. But its unprompted output skews
sexualised: the plain "white shirt" case came back with an emphasised bust, and the portrait
case came back bare-shouldered with nothing in the prompt asking for either. At a PG13 default
that is work the negative list has to undo on every render.

**Pony is the wrong style.** It renders painterly semi-realism, not clean anime, and washed
out at that. It also has the weakest negatives of the three at 12.52% -- variant b still has a
detailed garden behind the subject -- and it ignored the declared blouse and skirt in the age
case entirely in favour of an ornate gown. Weak adherence is the failure Counterfeit was
dropped for.

**Illustrious is the middle and the safest.** Clean anime, negatives that bite hard, every
declared feature rendered, and the least sexualised prior of the three.

### Illustrious, read by eye

**`age` 1.97% — the number understates it.** 19 and 65 are not merely similar, they are the
same young woman. The age tag is inert, and the sheet shows the 65-year-old rendering at about
twenty. This is the finding that motivated the evaluation and it is the worst possible result
on the criterion that matters most for an adults-only tier.

**`negative` 24.43% — negatives bite hard.** The cafe is fully gone from variant b. Whatever
else Illustrious does, `alwaysNegative` is doing real work there.

**`subject` 10.71%** — unmistakably a woman and a man from the same appearance tags.

**`outfit` 11.58%** — blouse and skirt held across neutral to laughing; the difference is
expression and pose, not wardrobe.

**`feature`** — freckles, eyepatch, twin braids and blonde all rendered on the first try. This
is the axis Counterfeit failed outright.

One miss worth noting: the `age` prompts asked for a white blouse and got a white dress.

## The four age bands, swept

`eval/sweep.mjs` renders every band for every runnable checkpoint and both subjects,
holding seed, appearance and negative constant. There is no score: the question is "does
each band read as its label", which no pixel difference answers.

| Model | Female bands | Male bands |
|---|---|---|
| Illustrious XL v2.0 | four clearly distinct | four clearly distinct |
| NoobAI-XL v1.1 | teen and student nearly identical | teen and student nearly identical |
| Pony V6 XL | weak; all read early thirties | **none**; all four are the same man |

Illustrious is the only candidate that separates all four bands on both subjects, and Pony
fails outright on males. Combined with the earlier criteria and the licences, that settles
the checkpoint question.

### Two things the sweep changed

**Occupation tags cheat.** The first pass used `high school student`, `college student`,
`office lady`, `salaryman`. Separation was excellent -- and the occupations brought their
own clothes, so the declared white shirt came back as a school uniform in one band and a
shirt and tie in another. The game controls outfit separately, so an age band that dresses
the character is a band that fights it. The shipped bands are occupation-free, and pay for
it: the adult bands separate noticeably less at 25-40 against 40+.

**The male teen band came back androgynous.** `mature male` is what carries maleness on
these checkpoints, and the teen band deliberately has none of it. `(masculine:1.2)`
replaces it without adding age.

### What ships

| Band | Female | Male |
|---|---|---|
| 16-18 | `teenage, (mature female:0.4)` | `teenage, (masculine:1.2)` |
| 18-25 | `(mature female:1.1), young adult` | `(mature male:1.0), young adult` |
| 25-40 | `(mature female:1.3), adult` | `(mature male:1.3), adult` |
| 40+ | `(mature female:1.4), middle-aged, aged` | `(mature male:1.4), middle-aged, aged` |

The teen band damps the maturity anchor to 0.4 rather than removing it. Removing it
entirely is what the `alwaysNegative` terms exist to guard against, and damping reads
younger without reaching for juvenile vocabulary. Characters in this band are held at PG13
by `ContentPolicy` regardless of anything here.

The maturity anchor moved out of `subjects[].positive` and into the bands. Carrying it in
both would put it in the prompt twice at two different weights, and the heavier one would
silently win.

## Licences, which are not a footnote

| Model | Licence | Commercial use |
|---|---|---|
| Illustrious XL v2.0 | creativeml-openrail-m | permitted, with the OpenRAIL use restrictions |
| Pony Diffusion V6 XL | modified fair-ai-public-license-1.0-sd | **prohibited** in any monetised application |
| NoobAI-XL v1.1 | fair-ai-public-license-1.0-sd | **prohibited** |
| FLUX.2 klein 4B | apache-2.0 | permitted, unrestricted |
| FLUX.2 klein 9B / dev | non-commercial | **prohibited** |
| Sana 1.6B | apache-2.0 (Gemma licence on its text encoder) | permitted |

If this ever ships for money, half the field is already out. Worth settling early rather than
after tuning a pack around a model that cannot be used.

The OpenRAIL restrictions on Illustrious are also worth reading rather than skimming: they
prohibit exactly the class of output the content clamp exists to prevent, which means the
licence and the design agree for once.

## FLUX.2 klein 4B is blocked by system RAM, not VRAM

The run never completed. ComfyUI stayed inside the model load for over twenty minutes with
24.4 GB of VRAM still free, and did not answer an interrupt, because the interrupt flag is
only checked between nodes and it never left the loader.

`/system_stats` says why: **the container sees 8.28 GB of system RAM**, with 5.97 GB free.
FLUX.2 klein stages 7.75 GB of diffusion weights and an 8.04 GB text encoder through that. The
card was never the constraint.

Two ways forward, and they are not equivalent:

* Raise the memory ceiling the container gets. On WSL2 that is `memory=` in `.wslconfig`,
  which defaults to half of host RAM. This is the real fix and it also removes an unexamined
  limit from every other measurement on this box.
* Use `qwen_3_4b_fp4_flux2.safetensors` (3.85 GB) instead of the bf16 encoder, bringing the
  total to 11.9 GB. Cheaper, but it measures a quantised encoder rather than the model, which
  is a different question from the one being asked.

Until one of those happens the FLUX row is unmeasured, not failed. Nothing here says anything
about its output.

## Known unknowns

- **Sana has no native ComfyUI support.** Verified against `/object_info` on the box: no Sana
  nodes exist. It needs `city96/ComfyUI_ExtraModels` running inside the ComfyUI process, which
  is a supply-chain decision rather than a download. Its target paths in `models-eval.tsv` are
  a best guess at what that pack expects and may need adjusting on first run. They live in
  `models-sana.tsv`, separate from the rest, so turning Sana off actually skips the 9.7 GB.
- **FLUX.2 klein 4B is not 4B of VRAM.** 7.75 GB diffusion + 8.04 GB Qwen3-4B text encoder +
  0.34 GB VAE = 16.1 GB, or 11.9 GB with the fp4 encoder. The 12 GB profile cannot host it
  alongside an LLM.
- **The `outfit` case conflates wardrobe drift with expression change**, since both move
  pixels. It is comparable across models, which is what it is for, but a low score is not by
  itself proof the outfit held.
- **Nothing here tests anime style directly.** It is judged from the sheets, which is a
  judgement and not a measurement.
