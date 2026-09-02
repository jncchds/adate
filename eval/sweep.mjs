// Age band sweep. Renders every band in eval/agebands.json for every named model and both
// subjects, holding seed, appearance and negative constant so the band vocabulary is the only
// variable. Output is judged by eye -- there is no pairwise score here, because the question
// is "does each band read as its label", which no pixel difference answers.
//
//   ADATE_COMFY=http://192.168.2.33:8188 node eval/sweep.mjs illustrious pony noobai
//   EVAL_BARE=1 node eval/sweep.mjs illustrious      # occupation-free band tags
//
// Writes eval/out/sweep/<model>-<subject>-<band>.png.

import { comfy, here, join, patch, run, fetchImage, ensureResident, loadJson, readFileSync, writeFileSync, mkdirSync } from './comfy.mjs';

const models = loadJson('models.json');
const sweep = loadJson('agebands.json');

const keys = process.argv.slice(2);
if (keys.length === 0) {
  console.error('usage: node eval/sweep.mjs <model-key> [...]');
  process.exit(2);
}

const bare = process.env.EVAL_BARE === '1';
const outDir = join(here, 'out', bare ? 'sweep-bare' : 'sweep');
mkdirSync(outDir, { recursive: true });

for (const key of keys) {
  const model = models[key];
  if (!model) throw new Error(`unknown model '${key}'`);

  await ensureResident(key);

  const dialect = model.dialect;
  const shared = loadJson('cases.json')._shared[dialect];
  const template = JSON.parse(readFileSync(join(here, 'graphs', model.graph), 'utf8'));

  console.log(`\n=== ${model.displayName}`);

  for (const band of sweep.bands) {
    for (const subject of ['female', 'male']) {
      const graph = structuredClone(template);
      for (const [name, value] of Object.entries(model.defaults ?? {})) {
        patch(graph, model.patch[name], value);
      }

      // The natural-language models get a prose rendering of the same band. Sending booru
      // tags to them would measure the dialect rather than the band.
      // EVAL_BARE selects the occupation-free phrasing. Occupations carry their own
      // outfits -- a school uniform, a tie -- which override the outfit the game controls
      // separately, so only the bare form is a candidate for a shipped ageBands block.
      const bandTags = bare ? band.bare[subject] : band[subject];

      const body = dialect === 'booru'
        ? `${bandTags}, ${sweep._appearance.booru}`
        : `${bandTags} ${sweep._appearance.natural}`;

      const positive = [model.positivePrefix, shared.prefix, body].filter(Boolean).join(', ');
      const negative = [model.negativePrefix, sweep._negative[dialect]].filter(Boolean).join(', ');

      patch(graph, model.patch.positive, positive);
      patch(graph, model.patch.negative, negative);
      patch(graph, model.patch.seed, sweep._seed);

      const label = `${key}-${subject}-${band.id}`;
      const started = Date.now();
      const ref = await run(graph);
      writeFileSync(join(outDir, `${label}.png`), await fetchImage(ref));
      console.log(`  ${label.padEnd(34)} ${((Date.now() - started) / 1000).toFixed(1)}s`);
    }
  }
}

console.log(`\n-> ${outDir}`);
