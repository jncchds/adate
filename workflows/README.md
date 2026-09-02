# Workflows

Three ComfyUI graphs, each stored as an **API-format** export plus a manifest that maps
logical input names to node paths. The C# side never hardcodes graph structure — rewiring a
workflow means re-exporting the graph and editing the manifest, not changing code.

> **API format, not the normal save.** ComfyUI's regular Save produces a UI graph with
> layout and link metadata, which is a different shape entirely. Enable
> *Settings → Dev mode options* and use **Save (API format)**.

## Files

For each workflow id `X`, this directory needs:

| File | Contents |
|---|---|
| `X.manifest.json` | node id map (below) |
| `<whatever the manifest names>` | the API-format graph |

Manifests are validated against their graph the first time a workflow is loaded, so a node id
that does not exist fails immediately rather than after a queue wait.

## Manifest shape

```json
{
  "id": "portrait",
  "workflowFile": "portrait.json",
  "inputs": {
    "positive": "6.inputs.text",
    "negative": "7.inputs.text",
    "seed":     "3.inputs.seed",
    "width":    "5.inputs.width",
    "height":   "5.inputs.height"
  },
  "outputNodes": ["9"]
}
```

`outputNodes` is not optional in practice. Leaving it empty means "accept images from any
node", and a graph that also saves a debug preview would then return two images — which the
provider rejects, because caching the wrong one under a content address is permanent.

## Required inputs per workflow

Names come from `WorkflowInputs`. An input the graph does not declare is silently skipped, so
a graph may expose more than the minimum but never less.

### `portrait` — candidate portraits

Plain SD1.5 text-to-image. No IP-Adapter: this graph is what *produces* the anchor.

| Input | Required | Notes |
|---|---|---|
| `positive` | ✅ | |
| `negative` | ✅ | |
| `seed` | ✅ | Four candidates are one prompt at four seeds |
| `width` / `height` | ✅ | 512×768 from the style pack |

Output: one `SaveImage`.

### `sprite` — expression sprites

The graph the spike is actually testing.

| Input | Required | Notes |
|---|---|---|
| `positive` | ✅ | |
| `negative` | ✅ | |
| `seed` | ✅ | Fixed per expression slot |
| `width` / `height` | ✅ | 640×960 from the style pack |
| `anchor` | ✅ | Filename for a `LoadImage` node |
| `anchorWeight` | ✅ | IP-Adapter weight; first rung of the fallback ladder |

Graph must include:

- **IP-Adapter Plus** (`ip-adapter-plus_sd15.safetensors`) with the CLIP vision encoder,
  taking the anchor image as reference. **Not FaceID or InstantID** — those rely on
  InsightFace embeddings trained on real faces and perform poorly on anime.
- **`LoadBackgroundRemovalModel`** (`birefnet.safetensors`) feeding **`RemoveBackground`**.
  Note the names: BiRefNet is native to ComfyUI but neither node has "BiRefNet" in its name.
- **`RemoveBackground` returns a `MASK`, not an RGBA image**, so the mask must go through
  **`InvertMask`** and then **`JoinImageWithAlpha`** before the save. The inversion is not
  optional: `JoinImageWithAlpha` treats the mask as the region to cut away, so without it
  you get a character-shaped hole in an opaque background. Verified on hardware.
- A `SaveImage`, which does preserve the alpha channel. Verified: output is PNG colour
  type 6 with roughly 2% partial-alpha edge feathering, and hair strands survive.

The `anchor` input is a *filename*, not a path. The game uploads the anchor to ComfyUI via
`POST /upload/image` first, because the game and ComfyUI do not share a filesystem — they
routinely run on different machines.

### `background` — location backgrounds

| Input | Required | Notes |
|---|---|---|
| `positive` | ✅ | |
| `negative` | ✅ | |
| `seed` | ✅ | Stable per location and time |
| `width` / `height` | ✅ | 768×512 from the style pack |

No IP-Adapter, no matting — backgrounds are opaque and full-frame.

## Checkpoint

All three load `Counterfeit-V2.5_fp16.safetensors`. The checkpoint is named in the style pack
(`stylepacks/counterfeit-anime.json`) and may be set directly in the graph for now; a
`checkpoint` input can be exposed later when a second pack exists.

## Status

All three graphs are written and have been run against real hardware — RTX 5090, ComfyUI
0.34.2, `cu130-slim`. Measured warm, with the checkpoint already resident:

| Workflow | Size | Time |
|---|---|---|
| `portrait` | 512×768 | 3.5–4.4 s |
| `background` | 768×512 | 4.0 s |
| `sprite` | 640×960 | ~4 s of sampling plus matting |

Cold start is about 100 s for the first image of a session, and about 87 s again the first
time the IP-Adapter and matting models load. Both are one-off model loads amortised across
the session, not per-image cost. Against HANDOFF §2 that puts four candidates near 16 s
warm, comfortably inside the 60 s budget.

## Re-verifying after an edit

The graph and its manifest are hashed into every image's content address, so editing either
one correctly invalidates art produced by the previous version — an identical request
against an edited graph misses cache and regenerates.

```bash
ADATE_Comfy__BaseAddress=http://<gpu-box>:8188 dotnet run --project src/Game.Cli -- generate portrait "1girl, solo, red hair, green eyes" --seed 42 --size 512x768
```

The CLI prints the content hash, the stored path, and the elapsed time — which is what
HANDOFF §2's timing criteria are measured against.
