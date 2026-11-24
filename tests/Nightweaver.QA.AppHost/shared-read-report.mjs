import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { assertSample, summarizeTools, boundTools } from './shared-read-assertions.mjs';
import { analyzeReceipt } from './shared-read-receipts.mjs';
import { windowLoad, settlementBoundary } from './shared-read-window.mjs';

export function buildReport(root) {
const result = JSON.parse(fs.readFileSync(path.join(root, 'result.json'), 'utf8'));
const samples = [], missing = new Set(), trials = [], failures = [];
for (const activity of result.activity) for (const surface of result.surfaces) {
  for (let repetition = 1; repetition <= result.coldRepetitions; repetition++) {
    if (!result.runs.some(run => run.activity === activity && run.surface === surface && run.repetition === repetition)) {
      failures.push(`Missing trial: ${activity}-${surface}-${repetition}`);
    }
  }
}
for (const run of result.runs) {
  const trial = path.join(root, `${run.activity}-${run.surface}-${run.repetition}`);
  const file = path.join(trial, 'browser.json');
  if (!fs.existsSync(file)) { failures.push(`No browser evidence: ${path.basename(trial)}`); continue; }
  const value = JSON.parse(fs.readFileSync(file, 'utf8'));
  if (run.startExit !== 0 || run.probeExit !== 0 || run.stopExit !== 0) failures.push(`Failed probe or stack stop: ${path.basename(trial)}`);
  if (value.errors.length) failures.push(`Browser errors: ${path.basename(trial)}`);
  for (const item of value.activity) {
    if (!item.aliveBefore || item.aliveAfter !== value.active) failures.push(`Invalid native activity: ${path.basename(trial)}/${item.id}`);
  }
  const expected = [{ surface: run.surface, mode: 'cold', cycle: 0 }];
  for (let cycle = 0; cycle < result.warmCycles; cycle++) {
    for (const surface of ['overview','explorer','attention','workspace','digest']) expected.push({ surface, mode: 'warm', cycle });
  }
  for (const row of expected) {
    if (!value.samples.some(sample => sample.surface === row.surface && sample.mode === row.mode
      && sample.cycle === row.cycle && sample.activity === run.activity)) {
      failures.push(`Missing sample: ${path.basename(trial)}/${row.mode}/${row.surface}/${row.cycle}`);
    }
  }
  for (const kind of ['comment', 'guarded-note', 'dependency', 'blocker-close']) {
    if (!value.changes.some(change => change.kind === kind)) failures.push(`Missing ${kind}: ${path.basename(trial)}`);
  }
  const records = fs.readdirSync(path.join(trial, 'tools')).filter(name => name.endsWith('.json'))
    .map(name => JSON.parse(fs.readFileSync(path.join(trial, 'tools', name), 'utf8')));
  const unsettled = fs.readdirSync(path.join(trial, 'tools')).filter(name => name.endsWith('.pending')
    && !fs.existsSync(path.join(trial, 'tools', name.replace(/\.pending$/, '.json'))));
  const pending = unsettled.map(name => {
    const row = JSON.parse(fs.readFileSync(path.join(trial, 'tools', name), 'utf8'));
    const observed = path.join(trial, 'tools', name.replace(/\.pending$/, '.observed'));
    if (fs.existsSync(observed)) {
      const value = JSON.parse(fs.readFileSync(observed, 'utf8'));
      // Both identities must have native-handle settlement. A wrapper exit
      // alone cannot establish settlement of the delegated native command.
      const launched = path.join(trial, 'tools', name.replace(/\.pending$/, '.launched'));
      row.settledByUtc = settlementBoundary(row,
        fs.existsSync(launched) ? JSON.parse(fs.readFileSync(launched, 'utf8')) : null, value);
      row.observation = value;
    }
    return row;
  });
  if (unsettled.length) failures.push(`Native command settlement unavailable: ${path.basename(trial)} (${unsettled.length} commands)`);
  for (const gap of value.missing) missing.add(gap);
  trials.push({ directory: path.basename(trial), manifest: value.manifest, changes: value.changes,
    activity: value.activity, lifecycle: run, errorCount: value.errors.length, unsettled, externalCommands: value.external,
    scenario: windowLoad(value, records, pending),
    fullToolLoad: summarizeTools(records, run.startedUtc ?? result.measuredUtc, run.finishedUtc ?? result.finishedUtc, pending),
    fullToolBounds: boundTools(records, pending, run.startedUtc ?? result.measuredUtc, run.finishedUtc ?? result.finishedUtc) });
  for (const rawSample of value.samples) {
    const sample = analyzeReceipt(rawSample, value);
    if (sample.httpUnavailableReason) missing.add(`HTTP receipt unavailable: ${path.basename(trial)}/${sample.mode}/${sample.surface}/${sample.cycle}: ${sample.httpUnavailableReason}`);
    let valid = true, reason = null;
    try { assertSample(sample); } catch (error) { valid = false; reason = String(error); failures.push(`Invalid sample: ${path.basename(trial)}/${sample.mode}/${sample.surface}: ${reason}`); }
    samples.push({ trial: path.basename(trial), ...sample, valid, reason,
      trackerLoad: summarizeTools(records, sample.startedUtc, sample.completedUtc ?? sample.cutoffUtc, pending),
      trackerLoadBounds: boundTools(records, pending, sample.startedUtc, sample.completedUtc ?? sample.cutoffUtc) });
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
return { schema: 'nightweaver-shared-read-report-v1',
  receiptAnalysis: { schema: 'nightweaver-shared-read-receipt-analysis-v1',
    matched: samples.filter(row => row.httpMs !== null).length,
    retained: samples.filter(row => row.retainedReceipt).length,
    unavailable: samples.filter(row => row.httpUnavailableReason).length },
  revision: result.revision, tree: result.tree, dirty: result.dirty, latencyTarget: null,
  completed: result.failure === null && result.runs.length > 0 && failures.length === 0,
  clock: 'Node monotonic measurement; UTC correlates native shim intervals, not cross-process phase subtraction.',
  groups, missing: [...failures, ...missing], failures, samples, trials };
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  const root = path.resolve(process.argv[2]);
  const report = buildReport(root);
  const destination = process.argv[3] ? path.resolve(process.argv[3]) : path.join(root, 'report.json');
  fs.writeFileSync(destination, JSON.stringify(report, null, 2));
  console.log(JSON.stringify({ completed: report.completed, groups: report.groups, missing: report.missing }, null, 2));
  if (!report.completed) process.exitCode = 1;
}
