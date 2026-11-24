import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { buildReport } from './shared-read-report.mjs';

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'shared-read-report-'));
  t.after(() => fs.rmSync(root, { recursive: true }));
  const at = '2026-11-23T00:00:00.000Z', end = '2026-11-23T00:00:01.000Z';
  const run = { surface: 'workspace', activity: 'active', repetition: 1, startExit: 0, probeExit: 0,
    stopExit: 0, startedUtc: at, finishedUtc: end };
  const result = { failure: null, runs: [run], activity: ['active'], surfaces: ['workspace'],
    coldRepetitions: 1, warmCycles: 1 };
  const sample = (surface, mode) => ({ surface, mode, cycle: 0, activity: 'active', error: null,
    startedUtc: at, completedUtc: end, httpMs: 1, usefulMs: 2, detailMs: 3,
    proof: { expected: true, rendered: ['fixture'], resources: [
      { epoch: 'epoch', revision: '1', sources: { inventory: {} }, health: 'fresh', dataPresent: true }] } });
  const browser = { samples: [sample('workspace', 'cold'),
    ...['overview','explorer','attention','workspace','digest'].map(surface => sample(surface, 'warm'))],
    changes: ['comment','guarded-note','dependency','blocker-close'].map(kind => ({ kind })),
    errors: [], activity: [{ id: 'agent', aliveBefore: true, aliveAfter: true }], active: true,
    missing: ['Gate wait attribution unavailable'] };
  const trial = path.join(root, 'active-workspace-1');
  fs.mkdirSync(path.join(trial, 'tools'), { recursive: true });
  const save = () => {
    fs.writeFileSync(path.join(root, 'result.json'), JSON.stringify(result));
    fs.writeFileSync(path.join(trial, 'browser.json'), JSON.stringify(browser));
  };
  save();
  return { root, trial, result, browser, save };
}

test('complete evidence can retain explicit unmeasured attribution', t => {
  const f = fixture(t); const report = buildReport(f.root);
  assert.equal(report.completed, true); assert.equal(report.samples.length, 6);
  assert(report.missing.includes('Gate wait attribution unavailable'));
});
for (const [name, change] of [
  ['missing trial', f => { f.result.coldRepetitions = 2; }],
  ['missing cold sample', f => { f.browser.samples.shift(); }],
  ['missing warm cycle', f => { f.result.warmCycles = 2; }],
  ['failed sample', f => { f.browser.samples[0].error = 'timeout'; }],
  ['failed stack stop', f => { f.result.runs[0].stopExit = 1; }],
  ['missing guarded write', f => { f.browser.changes.pop(); }],
  ['browser error', f => { f.browser.errors.push('render error'); }],
  ['native activity stopped', f => { f.browser.activity[0].aliveAfter = false; }]
]) test(`rejects ${name}`, t => {
  const f = fixture(t); change(f); f.save();
  const report = buildReport(f.root);
  assert.equal(report.completed, false); assert(report.failures.length > 0);
});
test('rejects missing browser evidence', t => {
  const f = fixture(t); fs.unlinkSync(path.join(f.trial, 'browser.json'));
  assert.equal(buildReport(f.root).completed, false);
});
test('rejects unsettled native commands', t => {
  const f = fixture(t); fs.writeFileSync(path.join(f.trial, 'tools', 'one.pending'), '{}');
  assert.equal(buildReport(f.root).completed, false);
});

for (const childPid of [2, 3]) test(`bounds a missing final only for the launched child (${childPid})`, t => {
  const f = fixture(t), tools = path.join(f.trial, 'tools');
  f.browser.clockIdentity = 'node/clock';
  f.browser.clockOriginUtc = f.result.runs[0].startedUtc;
  f.browser.external = [{ args: ['--readonly', 'ready'], settledMs: 900 }];
  f.browser.changes.at(-1).readyRenderedMs = 800;
  f.save();
  const save = (name, value) => fs.writeFileSync(path.join(tools, name), JSON.stringify(value));
  save('one.pending', { tool: 'bd', args: ['comments'], pid: 1, startedUtc: f.result.runs[0].startedUtc });
  save('one.launched', { pid: 2 });
  save('one.observed', { schema: 'nightweaver-tool-observation-v1', observedUtc: f.result.runs[0].finishedUtc,
    wrapper: { pid: 1, settledByUtc: '2026-11-23T00:00:00.500Z' },
    child: { pid: childPid, settledByUtc: '2026-11-23T00:00:00.600Z' } });
  const report = buildReport(f.root);
  assert.equal(report.completed, false);
  assert.equal(report.trials[0].scenario.bounds.missingFinalsBounded, childPid === 2);
  assert.equal(report.trials[0].scenario.bounds.bytesUpper, null);
  assert.equal(report.trials[0].scenario.load.count, null);
});
test('report replaces unrelated raw HTTP times and groups only matched receipts', t => {
  const f = fixture(t);
  const row = f.browser.samples[0];
  row.startMs = 100; row.detailMs = 50; row.httpMs = 5;
  row.proof.resources[0].key = 'workspace:fixture';
  f.browser.requests = [{ method: 'GET', atMs: 101,
    url: 'http://localhost/api/operations/v1/projects/qa/resources?key=attention' },
    { method: 'GET', atMs: 110,
      url: 'http://localhost/api/operations/v1/projects/qa/resources?key=workspace%3Afixture' }];
  f.browser.resources = [{ key: 'attention', atMs: 105 }, { key: 'workspace:fixture', atMs: 120 }];
  f.save();
  const report = buildReport(f.root);
  assert.equal(report.samples[0].httpMs, 20);
  assert.equal(report.groups.find(group => group.mode === 'cold').httpMs.median, 20);
  assert.equal(JSON.parse(fs.readFileSync(path.join(f.trial, 'browser.json'))).samples[0].httpMs, 5);
  f.browser.requests = []; f.save();
  const unavailable = buildReport(f.root);
  assert.equal(unavailable.samples[0].httpMs, null);
  assert.equal(unavailable.groups.find(group => group.mode === 'cold').httpMs, null);
  assert(unavailable.missing.some(reason => reason.startsWith('HTTP receipt unavailable:')));
});
