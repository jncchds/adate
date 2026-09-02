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

Candidates other than Illustrious need `models-eval.tsv` added to `MANIFESTS` and the stack
restarted. Sana additionally needs `INSTALL_EXTRA_MODELS=1`.

Booru and natural-language phrasings of every case are held side by side in `eval/cases.json`,
and a model is sent the one matching its dialect. Sending booru tags to a natural-language
model would measure the dialect and not the model.

## Results

Measured on an RTX 5090, ComfyUI 0.34.2 / cu130, seed 4242, 832x1216.

| Model | age | negative | subject | outfit ↓ | s/image |
|---|---|---|---|---|---|
| Illustrious XL v2.0 | **1.97%** | 24.43% | 10.71% | 11.58% | 6.1 |
| Pony Diffusion V6 XL | — | — | — | — | — |
| NoobAI-XL v1.1 | — | — | — | — | — |
| FLUX.2 klein 4B | — | — | — | — | — |
| Sana 1.6B | — | — | — | — | — |

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

## Known unknowns

- **Sana has no native ComfyUI support.** Verified against `/object_info` on the box: no Sana
  nodes exist. It needs `city96/ComfyUI_ExtraModels` running inside the ComfyUI process, which
  is a supply-chain decision rather than a download. Its target paths in `models-eval.tsv` are
  a best guess at what that pack expects and may need adjusting on first run.
- **FLUX.2 klein 4B is not 4B of VRAM.** 7.75 GB diffusion + 8.04 GB Qwen3-4B text encoder +
  0.34 GB VAE = 16.1 GB, or 11.9 GB with the fp4 encoder. The 12 GB profile cannot host it
  alongside an LLM.
- **The `outfit` case conflates wardrobe drift with expression change**, since both move
  pixels. It is comparable across models, which is what it is for, but a low score is not by
  itself proof the outfit held.
- **Nothing here tests anime style directly.** It is judged from the sheets, which is a
  judgement and not a measurement.
