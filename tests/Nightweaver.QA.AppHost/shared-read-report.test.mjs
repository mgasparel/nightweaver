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
