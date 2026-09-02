// Checkpoint evaluation runner. Drives ComfyUI directly rather than going through
// Game.Imaging, deliberately: this measures models, not our pipeline, and the candidates
// need structurally different graphs that the game's workflow system has no reason to know
// about (FLUX loads a diffusion model, a text encoder and a VAE separately).
//
//   node eval/run.mjs <model-key> [more keys...]
//   ADATE_COMFY=http://192.168.2.33:8188 node eval/run.mjs illustrious
//
// Writes PNGs to eval/out/<model>/<case>-<variant>.png and a manifest to
// eval/out/<model>/results.json. Scoring is eval/score.ps1, run afterwards.

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const comfy = process.env.ADATE_COMFY ?? 'http://127.0.0.1:8188';

const models = JSON.parse(readFileSync(join(here, 'models.json'), 'utf8'));
const battery = JSON.parse(readFileSync(join(here, 'cases.json'), 'utf8'));

const keys = process.argv.slice(2);
if (keys.length === 0) {
  console.error('usage: node eval/run.mjs <model-key> [...]');
  console.error('known: ' + Object.keys(models).filter(k => !k.startsWith('_')).join(', '));
  process.exit(2);
}

/** Sets graph.<node>.inputs.<field> from a "3.inputs.text" style path, as a manifest does. */
function patch(graph, path, value) {
  const [node, , field] = path.split('.');
  if (!graph[node]) throw new Error(`graph has no node '${node}' for path '${path}'`);
  graph[node].inputs[field] = value;
}

/**
 * Retries transport failures, not HTTP errors. Loading a cold 16 GB model blocks ComfyUI's
 * event loop for minutes, so connections are refused or time out while a perfectly healthy
 * server is busy. A 400 from ComfyUI means the graph is wrong and retrying it is pointless.
 */
async function resilient(label, fn, attempts = 12) {
  for (let i = 1; ; i++) {
    try {
      return await fn();
    } catch (err) {
      if (err.fatal || i >= attempts) throw err;
      await new Promise(r => setTimeout(r, 5000));
    }
  }
}

async function post(path, body) {
  return resilient(`POST ${path}`, async () => {
    const res = await fetch(`${comfy}${path}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify(body),
    });
    const text = await res.text();
    if (!res.ok) {
      // Not retryable: the server answered and rejected the request.
      throw Object.assign(new Error(`POST ${path} -> ${res.status}: ${text.slice(0, 600)}`), { fatal: true });
    }
    return JSON.parse(text);
  });
}

/**
 * Submits and waits. Polls /history rather than opening a websocket: the socket is an
 * optimisation for progress reporting and this has no progress to report, while /history
 * is the authoritative record either way.
 */
// 30 minutes. A warm SDXL render is 6s, but FLUX.2 klein reads 16 GB off a spinning disk on
// first use and the box stops answering HTTP while it does. 600s was not enough and the run
// was abandoned while ComfyUI was still working on it.
async function run(graph, timeoutMs = 1_800_000) {
  const { prompt_id: id } = await post('/prompt', { prompt: graph });
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    // The box stops answering HTTP while it reads a cold model off the disk, and a
    // 16 GB one takes long enough to blow Node's default header timeout. The prompt is
    // still running server-side, so a failed poll is a reason to poll again, not to
    // abandon a generation that will finish.
    let entry;
    try {
      const res = await fetch(`${comfy}/history/${id}`);
      entry = (await res.json())[id];
    } catch {
      await new Promise(r => setTimeout(r, 5000));
      continue;
    }

    if (entry) {
      const status = entry.status ?? {};
      if (status.status_str === 'error') {
        const err = (status.messages ?? []).find(m => m[0] === 'execution_error');
        throw new Error(`ComfyUI execution error: ${JSON.stringify(err?.[1] ?? status).slice(0, 800)}`);
      }
      for (const out of Object.values(entry.outputs ?? {})) {
        if (out.images?.length) return out.images[0];
      }
      throw new Error(`prompt ${id} finished with no images`);
    }
    await new Promise(r => setTimeout(r, 1500));
  }
  throw new Error(`prompt ${id} did not complete within ${timeoutMs}ms`);
}

async function fetchImage({ filename, subfolder, type }) {
  const q = new URLSearchParams({ filename, subfolder: subfolder ?? '', type: type ?? 'output' });
  return resilient('/view', async () => {
    const res = await fetch(`${comfy}/view?${q}`);
    if (!res.ok) throw new Error(`/view -> ${res.status}`);
    return Buffer.from(await res.arrayBuffer());
  });
}

/**
 * Evicts resident models. The box runs with --highvram, which keeps everything loaded
 * between runs -- correct for the game, where one checkpoint is used all session, and wrong
 * here, where each candidate is a fresh multi-gigabyte checkpoint. Without this the third
 * model in a run OOMs while nvidia-smi still reports free VRAM, because ComfyUI's estimator
 * refuses the allocation rather than evicting on its own.
 */
async function freeModels() {
  await resilient('POST /free', async () => {
    const res = await fetch(`${comfy}/free`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ unload_models: true, free_memory: true }),
    });
    if (!res.ok) throw new Error(`POST /free -> ${res.status}`);
  });
}

for (const key of keys) {
  const model = models[key];
  if (!model) throw new Error(`unknown model '${key}'`);

  await freeModels();

  const dialect = model.dialect;
  const shared = battery._shared[dialect];
  if (!shared) throw new Error(`no shared prompts for dialect '${dialect}'`);

  const template = JSON.parse(readFileSync(join(here, 'graphs', model.graph), 'utf8'));
  const outDir = join(here, 'out', key);
  mkdirSync(outDir, { recursive: true });

  const results = { model: key, ...model, comfy, cases: [] };
  console.log(`\n=== ${model.displayName}  [${model.licence}]`);

  for (const testCase of battery.cases) {
    for (const [variantName, variant] of Object.entries(testCase.variants)) {
      const graph = structuredClone(template);

      for (const [name, value] of Object.entries(model.defaults ?? {})) {
        patch(graph, model.patch[name], value);
      }

      const body = variant[dialect];
      if (!body) throw new Error(`case ${testCase.id}/${variantName} has no ${dialect} phrasing`);

      const positive = [model.positivePrefix, shared.prefix, body].filter(Boolean).join(', ');

      // A case may override the negative -- that is the whole point of the negative case.
      const caseNegative = variant.negative ?? shared.negative;
      const negative = [model.negativePrefix, caseNegative].filter(Boolean).join(', ');

      patch(graph, model.patch.positive, positive);
      patch(graph, model.patch.negative, negative);
      patch(graph, model.patch.seed, battery._shared.seed);

      const label = `${testCase.id}-${variantName}`;
      const started = Date.now();
      const ref = await run(graph);
      const seconds = (Date.now() - started) / 1000;

      const file = join(outDir, `${label}.png`);
      writeFileSync(file, await fetchImage(ref));

      console.log(`  ${label.padEnd(12)} ${seconds.toFixed(1)}s`);
      results.cases.push({ case: testCase.id, variant: variantName, file, seconds, positive, negative });
    }
  }

  writeFileSync(join(outDir, 'results.json'), JSON.stringify(results, null, 2));
  console.log(`  -> ${join(outDir, 'results.json')}`);
}
