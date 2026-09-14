"""
Z-Image API server (DiffSynth-Studio).

Verified signature:
    ZImagePipeline.from_pretrained(
        torch_dtype, device, model_configs, tokenizer_config, vram_limit, enable_npu_patch
    )

model_configs defaults to [] -- omit it and NO weights load: the pipeline's
tokenizer/text_encoder/dit all stay None, startup looks fine, and the first
generate call dies with
    'NoneType' object has no attribute 'apply_chat_template'

Note there is no vram_config parameter. The knob is vram_limit: float.

FILE PATTERNS
-------------
The tokenizer_config default in the signature uses a bare directory prefix
('tokenizer/'), not a glob -- so the loader likely matches on prefix. Two forms
are provided below; if a component loads as NoneType, flip PATTERN_STYLE.

MATTING
-------
`"background": "remove"` cuts the subject out with BiRefNet and returns RGBA, so the game
can composite sprites over separately generated backgrounds without matting anything
itself. Measured on the RTX 5090: 0.16-0.36s per image warm, 1.7GB VRAM peak.
"""

from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse
from pydantic import BaseModel
from typing import Literal, Optional, List
from pathlib import Path
from datetime import datetime
import os
import re
import uuid
import torch
from PIL import Image
from torchvision import transforms

from diffsynth.pipelines.z_image import ZImagePipeline
from diffsynth.core.loader.config import ModelConfig

MODEL_ID = os.environ.get("Z_IMAGE_MODEL_ID", "Tongyi-MAI/Z-Image-Turbo")
OUTPUT_DIR = Path(os.environ.get("Z_IMAGE_OUTPUT_DIR", "/app/output"))

# "prefix"  -> transformer/          (matches the tokenizer_config default's style)
# "glob"    -> transformer/*.safetensors
PATTERN_STYLE = os.environ.get("Z_IMAGE_PATTERN_STYLE", "prefix")

# Turbo is distilled for 8 steps at cfg 1.0.
# Base Tongyi-MAI/Z-Image wants ~30 steps and cfg 3.0-5.0.
DEFAULT_STEPS = int(os.environ.get("Z_IMAGE_STEPS", "8"))
DEFAULT_CFG = float(os.environ.get("Z_IMAGE_CFG", "1.0"))

MATTING_MODEL_ID = os.environ.get("Z_IMAGE_MATTING_MODEL_ID", "ZhengPeng7/BiRefNet")
# Pinned: the model repo ships its own Python (trust_remote_code), so an unpinned revision
# would execute whatever was pushed there last.
MATTING_REVISION = os.environ.get(
    "Z_IMAGE_MATTING_REVISION", "e2bf8e4460fc8fa32bba5ea4d94b3233d367b0e4"
)
MATTING_SIZE = 1024

DEVICE = "cuda" if torch.cuda.is_available() else "cpu"

# Output ids are uuids; anything that could name a path outside OUTPUT_DIR is refused.
IMAGE_ID = re.compile(r"[A-Za-z0-9_-]{1,64}")
MATTING_DTYPE = torch.float16 if DEVICE == "cuda" else torch.float32

app = FastAPI(title="Z-Image API", version="4.2.0")

pipeline = None
matting = None
character_contexts: dict = {}

matting_transform = transforms.Compose([
    transforms.Resize((MATTING_SIZE, MATTING_SIZE)),
    transforms.ToTensor(),
    transforms.Normalize([0.485, 0.456, 0.406], [0.229, 0.224, 0.225]),
])


def build_model_configs() -> List[ModelConfig]:
    suffix = "" if PATTERN_STYLE == "prefix" else "*.safetensors"
    return [
        ModelConfig(model_id=MODEL_ID, origin_file_pattern=f"transformer/{suffix}"),
        ModelConfig(model_id=MODEL_ID, origin_file_pattern=f"text_encoder/{suffix}"),
        ModelConfig(model_id=MODEL_ID, origin_file_pattern=f"vae/{suffix}"),
    ]


def load_matting():
    from transformers import AutoModelForImageSegmentation

    model = AutoModelForImageSegmentation.from_pretrained(
        MATTING_MODEL_ID, revision=MATTING_REVISION, trust_remote_code=True
    )
    return model.to(DEVICE, dtype=MATTING_DTYPE).eval()


def remove_background(image: Image.Image) -> Image.Image:
    rgb = image.convert("RGB")
    x = matting_transform(rgb).unsqueeze(0).to(DEVICE, dtype=MATTING_DTYPE)
    with torch.no_grad():
        pred = matting(x)[-1].sigmoid().float().cpu()
    mask = transforms.functional.to_pil_image(pred[0].squeeze()).resize(rgb.size, Image.BILINEAR)
    rgb.putalpha(mask)
    return rgb


class CharacterDefinition(BaseModel):
    character_id: str
    name: str
    description: str
    metadata: Optional[dict] = None


class ImageGenerationRequest(BaseModel):
    prompt: str
    character_id: Optional[str] = None
    negative_prompt: str = ""
    seed: Optional[int] = None
    num_inference_steps: int = DEFAULT_STEPS
    cfg_scale: float = DEFAULT_CFG
    height: int = 768
    width: int = 768
    background: Literal["keep", "remove"] = "keep"
    # img2img: an earlier output to start from, and how far to move away from it
    # (1.0 ignores the input entirely).
    input_image_id: Optional[str] = None
    denoising_strength: float = 1.0


class ImageGenerationResponse(BaseModel):
    image_id: str
    timestamp: str
    character_id: Optional[str]
    prompt: str
    image_path: str
    seed: int
    background: str


@app.on_event("startup")
async def startup_event():
    global pipeline, matting

    if torch.cuda.is_available():
        vram = torch.cuda.get_device_properties(0).total_memory / (1024 ** 3)
        print(f"GPU: {torch.cuda.get_device_name(0)}  VRAM: {vram:.1f}GB", flush=True)
    else:
        print("WARNING: no CUDA device visible -- generation will be very slow", flush=True)

    configs = build_model_configs()
    print(f"Loading {MODEL_ID}  (pattern style: {PATTERN_STYLE})", flush=True)
    for c in configs:
        print(f"    {c.origin_file_pattern}", flush=True)
    print("First run downloads ~20GB; subsequent starts read from cache.", flush=True)

    pipeline = ZImagePipeline.from_pretrained(
        torch_dtype=torch.bfloat16,
        device=DEVICE,
        model_configs=configs,
        # tokenizer_config omitted: its default already points at MODEL_ID's tokenizer/
    )

    components = {
        "tokenizer": getattr(pipeline, "tokenizer", None),
        "text_encoder": getattr(pipeline, "text_encoder", None),
        "dit": getattr(pipeline, "dit", None),
        "vae_decoder": getattr(pipeline, "vae_decoder", None),
    }
    for name, obj in components.items():
        print(f"  {name:<14} {type(obj).__name__}", flush=True)

    missing = [n for n in ("tokenizer", "text_encoder", "dit") if components[n] is None]
    if missing:
        raise RuntimeError(
            f"Components failed to load: {', '.join(missing)}. "
            f"The origin_file_pattern values didn't match anything with "
            f"PATTERN_STYLE={PATTERN_STYLE!r}. Try the other style by setting "
            f"Z_IMAGE_PATTERN_STYLE={'glob' if PATTERN_STYLE == 'prefix' else 'prefix'} "
            f"in compose, or dump the real repo layout (see README)."
        )

    # Loaded at startup rather than on first use, so a broken matting model fails the
    # container instead of the first sprite. The warm-up pays the ~6s first-inference cost.
    print(f"Loading matting {MATTING_MODEL_ID}@{MATTING_REVISION[:12]}", flush=True)
    matting = load_matting()
    remove_background(Image.new("RGB", (64, 64)))
    print("  matting        ready", flush=True)

    OUTPUT_DIR.mkdir(parents=True, exist_ok=True)
    print("Ready.", flush=True)


@app.get("/health")
async def health_check():
    ready = (
        pipeline is not None
        and getattr(pipeline, "tokenizer", None) is not None
        and matting is not None
    )
    return {
        "status": "ok" if ready else "loading",
        "pipeline_ready": ready,
        "gpu_available": torch.cuda.is_available(),
        "model": MODEL_ID,
        "matting_model": f"{MATTING_MODEL_ID}@{MATTING_REVISION}",
    }


@app.post("/characters", response_model=CharacterDefinition)
async def create_character(character: CharacterDefinition):
    character_contexts[character.character_id] = character.dict()
    print(f"character '{character.character_id}' registered", flush=True)
    return character


@app.get("/characters")
async def list_characters():
    return list(character_contexts.values())


@app.get("/characters/{character_id}")
async def get_character(character_id: str):
    if character_id not in character_contexts:
        raise HTTPException(status_code=404, detail=f"Character '{character_id}' not found")
    return character_contexts[character_id]


@app.delete("/characters/{character_id}")
async def delete_character(character_id: str):
    if character_contexts.pop(character_id, None) is None:
        raise HTTPException(status_code=404, detail=f"Character '{character_id}' not found")
    return {"deleted": character_id}


@app.post("/generate", response_model=ImageGenerationResponse)
async def generate_image(request: ImageGenerationRequest):
    if pipeline is None:
        raise HTTPException(status_code=503, detail="Pipeline not initialized")
    if request.background == "remove" and matting is None:
        raise HTTPException(status_code=503, detail="Matting model not initialized")

    prompt = request.prompt
    if request.character_id:
        char = character_contexts.get(request.character_id)
        if char is None:
            raise HTTPException(
                status_code=404,
                detail=f"Character '{request.character_id}' not defined. POST /characters first.",
            )
        prompt = f"{char['description']}, {request.prompt}"

    seed = request.seed if request.seed is not None else int(torch.randint(0, 2**31, (1,)).item())

    input_image = None
    if request.input_image_id is not None:
        if not IMAGE_ID.fullmatch(request.input_image_id):
            raise HTTPException(status_code=400, detail="input_image_id must be a bare image id")
        if not 0.0 < request.denoising_strength <= 1.0:
            raise HTTPException(status_code=400, detail="denoising_strength must be in (0, 1]")
        source = OUTPUT_DIR / f"{request.input_image_id}.png"
        if not source.exists():
            raise HTTPException(status_code=404, detail=f"Input image '{request.input_image_id}' not found")
        input_image = Image.open(source)
        if input_image.mode == "RGBA":
            # A matted sprite has no background to restyle; give it a flat neutral one so the
            # model does not invent scenery in the transparent area, then matte the result again.
            flat = Image.new("RGB", input_image.size, (200, 200, 200))
            flat.paste(input_image, mask=input_image.split()[3])
            input_image = flat
        input_image = input_image.convert("RGB").resize((request.width, request.height), Image.LANCZOS)

    print(
        f"generate: seed={seed} steps={request.num_inference_steps} "
        f"cfg={request.cfg_scale} {request.width}x{request.height} "
        f"bg={request.background} "
        f"img2img={request.input_image_id + '@' + str(request.denoising_strength) if input_image else 'no'} "
        f":: {prompt[:70]}",
        flush=True,
    )

    try:
        image = pipeline(
            prompt=prompt,
            negative_prompt=request.negative_prompt,
            seed=seed,
            height=request.height,
            width=request.width,
            num_inference_steps=request.num_inference_steps,
            cfg_scale=request.cfg_scale,
            rand_device=DEVICE,
            input_image=input_image,
            denoising_strength=request.denoising_strength if input_image else 1.0,
        )

        # DiffSynth returns a PIL image directly (not a .images list like diffusers).
        if hasattr(image, "images"):
            image = image.images[0]

        if request.background == "remove":
            image = remove_background(image)
    except Exception as e:
        import traceback
        traceback.print_exc()
        raise HTTPException(status_code=500, detail=f"Generation failed: {e}")

    image_id = str(uuid.uuid4())
    image.save(str(OUTPUT_DIR / f"{image_id}.png"))

    return ImageGenerationResponse(
        image_id=image_id,
        timestamp=datetime.now().isoformat(),
        character_id=request.character_id,
        prompt=request.prompt,
        image_path=f"/images/{image_id}",
        seed=seed,
        background=request.background,
    )


@app.get("/images/{image_id}")
async def get_image(image_id: str):
    path = OUTPUT_DIR / f"{image_id}.png"
    if not path.exists():
        raise HTTPException(status_code=404, detail=f"Image '{image_id}' not found")
    return FileResponse(path, media_type="image/png")


@app.post("/batch-generate", response_model=List[ImageGenerationResponse])
async def batch_generate(requests: List[ImageGenerationRequest]):
    results = []
    for i, req in enumerate(requests, 1):
        print(f"[{i}/{len(requests)}]", flush=True)
        results.append(await generate_image(req))
    return results


@app.get("/")
async def root():
    return {
        "name": "Z-Image API",
        "version": "4.2.0",
        "model": MODEL_ID,
        "ready": pipeline is not None,
        "docs": "/docs",
    }
