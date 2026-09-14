# Z-Image stack

The image backend the game uses when `Imaging:Provider` is `ZImage`. It runs on the GPU box
next to ComfyUI, as its own compose project.

    compose/zimage/
    ├── docker-compose.yml
    ├── Dockerfile.z-image    DiffSynth-Studio pinned via DIFFSYNTH_REF
    └── z_image_api.py        bind-mounted, so edits need a restart, not a rebuild

This directory is the source of truth. The box runs a copy at `D:\docker\stacks\zimage`
(managed with Dockge), with model caches and output under `D:\docker\zimage\`.

## Deploy

Copy the three files to `D:\docker\stacks\zimage`, then from that directory:

    docker compose up -d --force-recreate z-image   # z_image_api.py changed
    docker compose up -d --build                    # Dockerfile changed

Recreate rather than `restart` after replacing `z_image_api.py`. It is a single-file bind
mount, which pins the file it was created against: replace the file (a copy, an upload, most
editors' save) and the running container sees it as missing (`-?????????`), so a plain restart
fails to import it. Recreating binds the new file. Measured on Docker Desktop.

The first start downloads ~20GB of weights. Startup should end with:

      tokenizer      Qwen2TokenizerFast
      text_encoder   ZImageTextEncoder
      dit            ZImageDiT
      vae_decoder    FluxVAEDecoder
    Ready.

Any `NoneType` there means that component's `origin_file_pattern` matched nothing. Compose
sets `Z_IMAGE_PATTERN_STYLE=glob`; flip it to `prefix` if a DiffSynth bump changes the layout.

## Gotcha

`ZImagePipeline.from_pretrained()` defaults `model_configs` to `[]`, which loads no weights.
Startup looks fine, then the first generation fails with `'NoneType' object has no attribute
'apply_chat_template'`. There is no `vram_config`; the knob is `vram_limit: float`.

## Check it

    Invoke-RestMethod "http://192.168.2.33:8000/health"

    $r = Invoke-RestMethod "http://192.168.2.33:8000/generate" -Method POST `
         -ContentType "application/json" `
         -Body '{"prompt":"a woman with long purple hair, anime style","seed":42}'
    Invoke-WebRequest "http://192.168.2.33:8000/images/$($r.image_id)" -OutFile test.png

## Settings

Turbo is distilled for 8 steps at `cfg_scale` 1.0. The game sends both explicitly and hashes
them into the cache key (`ZImageOptions`). The base `Tongyi-MAI/Z-Image` wants ~30 steps and
cfg 3.0-5.0; change `Z_IMAGE_MODEL_ID` and the game's options together.

## Not used by the game

`/characters` keeps descriptions in process memory, so they are lost on every restart. It
only prepends the description to the prompt. The game owns character records and compiles
prompts itself (docs/phase-2-plan.md §1).
