// Minimal ComfyUI client for the evaluation scripts. Shared by run.mjs (the paired battery)
// and sweep.mjs (the age band grid), so both behave identically against a box that is slow,
// memory-constrained and occasionally unresponsive.

import { readFileSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export const here = dirname(fileURLToPath(import.meta.url));
export const comfy = process.env.ADATE_COMFY ?? 'http://127.0.0.1:8188';

/** Sets graph.<node>.inputs.<field> from a "3.inputs.text" style path, as a manifest does. */
export function patch(graph, path, value) {
  const [node, , field] = path.split('.');
  if (!graph[node]) throw new Error(`graph has no node '${node}' for path '${path}'`);
  graph[node].inputs[field] = value;
}

/**
 * Retries transport failures, not HTTP errors. Loading a cold multi-gigabyte model blocks
 * ComfyUI's event loop for minutes, so connections time out while a healthy server is busy.
 * A 400 means the graph is wrong and retrying it is pointless.
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
      throw Object.assign(new Error(`POST ${path} -> ${res.status}: ${text.slice(0, 600)}`), { fatal: true });
    }
    return JSON.parse(text);
  });
}

/**
 * Submits and waits. Polls /history rather than opening a websocket: the socket is an
 * optimisation for progress reporting and there is no progress to report here, while
 * /history is the authoritative record either way.
 */
export async function run(graph, timeoutMs = 1_800_000) {
  const { prompt_id: id } = await post('/prompt', { prompt: graph });
  const deadline = Date.now() + timeoutMs;

  while (Date.now() < deadline) {
    // A failed poll during a cold load is a reason to poll again, not to abandon a
    // generation that is still running server-side.
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

export async function fetchImage({ filename, subfolder, type }) {
  const q = new URLSearchParams({ filename, subfolder: subfolder ?? '', type: type ?? 'output' });
  return resilient('/view', async () => {
    const res = await fetch(`${comfy}/view?${q}`);
    if (!res.ok) throw new Error(`/view -> ${res.status}`);
    return Buffer.from(await res.arrayBuffer());
  });
}

const residentFile = join(here, 'out', '.resident');
const residentModel = () => (existsSync(residentFile) ? readFileSync(residentFile, 'utf8').trim() : null);

/**
 * Evicts resident models, but only when the box is holding a different one than this run
 * needs. Both simpler rules are wrong: always freeing throws away a warm model and pays
 * minutes to reload it, and never freeing OOMs the second candidate because --highvram keeps
 * the first resident. Which model is loaded outlives the process, so it is tracked on disk.
 */
export async function ensureResident(key) {
  if (residentModel() === key) return;

  await resilient('POST /free', async () => {
    const res = await fetch(`${comfy}/free`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ unload_models: true, free_memory: true }),
    });
    if (!res.ok) throw new Error(`POST /free -> ${res.status}`);
  });

  mkdirSync(join(here, 'out'), { recursive: true });
  writeFileSync(residentFile, key);
}

export const loadJson = name => JSON.parse(readFileSync(join(here, name), 'utf8'));
export { readFileSync, writeFileSync, mkdirSync, join };
