import fs from 'node:fs';
import path from 'node:path';
import { assertSample, summarizeTools } from './shared-read-assertions.mjs';

const root = path.resolve(process.argv[2]);
const result = JSON.parse(fs.readFileSync(path.join(root, 'result.json'), 'utf8'));
const samples = [], missing = new Set(), trials = [];
for (const run of result.runs) {
  const trial = path.join(root, `${run.activity}-${run.surface}-${run.repetition}`);
  const file = path.join(trial, 'browser.json');
  if (!fs.existsSync(file)) { missing.add(`No browser evidence: ${path.basename(trial)}`); continue; }
  const value = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (run.probeExit !== 0 || run.stopExit !== 0) missing.add(`Failed probe or stack stop: ${path.basename(trial)}`);
  for (const kind of ['comment', 'guarded-note', 'dependency', 'blocker-close']) {
    if (!value.changes.some(change => change.kind === kind)) missing.add(`Missing ${kind}: ${path.basename(trial)}`);
  }
  const records = fs.readdirSync(path.join(trial, 'tools')).filter(name => name.endsWith('.json'))
    .map(name => JSON.parse(fs.readFileSync(path.join(trial, 'tools', name), 'utf8')));
  const unsettled = fs.readdirSync(path.join(trial, 'tools')).filter(name => name.endsWith('.pending')
    && !fs.existsSync(path.join(trial, 'tools', name.replace(/\.pending$/, '.json'))));
  if (unsettled.length) missing.add(`Native command settlement unavailable: ${path.basename(trial)} (${unsettled.length} commands)`);
  for (const gap of value.missing) missing.add(gap);
  trials.push({ directory: path.basename(trial), manifest: value.manifest, changes: value.changes,
    activity: value.activity, lifecycle: run, errorCount: value.errors.length, unsettled, externalCommands: value.external,
    fullToolLoad: summarizeTools(records, run.startedUtc ?? result.measuredUtc, run.finishedUtc ?? result.finishedUtc) });
  for (const sample of value.samples) {
    let valid = true, reason = null;
    try { assertSample(sample); } catch (error) { valid = false; reason = String(error); }
    samples.push({ trial: path.basename(trial), ...sample, valid, reason,
      trackerLoad: summarizeTools(records, sample.startedUtc, sample.completedUtc ?? sample.cutoffUtc) });
  }
}
const groups = [];
for (const activity of result.activity) for (const mode of ['cold','warm']) for (const surface of result.surfaces) {
  const rows = samples.filter(row => row.activity === activity && row.mode === mode && row.surface === surface);
  const valid = rows.filter(row => row.valid);
  const stats = field => {
    const values = valid.map(row => row[field]).filter(Number.isFinite).sort((a,b) => a-b);
    if (!values.length) return null;
    const middle = Math.floor(values.length / 2);
    return { count: values.length, median: values.length % 2 ? values[middle] : (values[middle-1]+values[middle])/2,
      min: values[0], max: values.at(-1) };
  };
  if (!rows.length || valid.length !== rows.length) missing.add(`Incomplete ${activity}/${mode}/${surface}`);
  groups.push({ activity, mode, surface, actual: rows.length, complete: valid.length,
    httpMs: stats('httpMs'), usefulMs: stats('usefulMs'), detailMs: stats('detailMs') });
}
fs.writeFileSync(path.join(root, 'report.json'), JSON.stringify({ schema: 'nightweaver-shared-read-report-v1',
  revision: result.revision, tree: result.tree, dirty: result.dirty, latencyTarget: null,
  completed: result.failure === null && result.runs.every(run => run.probeExit === 0 && run.stopExit === 0),
  clock: 'Node monotonic measurement; UTC correlates native shim intervals, not cross-process phase subtraction.',
  groups, missing: [...missing], samples, trials }, null, 2));
console.log(JSON.stringify({ groups, missing: [...missing] }, null, 2));
