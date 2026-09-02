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

import { comfy, here, join, patch, run, fetchImage, ensureResident, loadJson, readFileSync, writeFileSync, mkdirSync } from './comfy.mjs';

const models = loadJson('models.json');
const battery = loadJson('cases.json');

const keys = process.argv.slice(2);
if (keys.length === 0) {
  console.error('usage: node eval/run.mjs <model-key> [...]');
  console.error('known: ' + Object.keys(models).filter(k => !k.startsWith('_')).join(', '));
  process.exit(2);
}

for (const key of keys) {
  const model = models[key];
  if (!model) throw new Error(`unknown model '${key}'`);

  await ensureResident(key);

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
