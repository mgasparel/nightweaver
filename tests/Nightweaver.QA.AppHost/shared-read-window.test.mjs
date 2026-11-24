import { test } from 'node:test';
import assert from 'node:assert/strict';
import { scenarioWindow, windowLoad, settlementBoundary } from './shared-read-window.mjs';
const at = offset => new Date(Date.parse('2026-11-23T00:00:00Z') + offset).toISOString();
const browser = () => ({ clockIdentity: 'node/clock', clockOriginUtc: at(0),
  samples: [{ mode: 'cold', startedUtc: at(100), completedUtc: at(500) }],
  changes: [{ kind: 'blocker-close', readyRenderedMs: 900 }],
  external: [{ args: ['--readonly','ready','--json'], settledMs: 1000.25 }] });

test('window includes final native verification and rounds outward', () => {
  const value = scenarioWindow(browser());
  assert.equal(value.startedUtc, at(100)); assert.equal(value.cutoffUtc, at(1002));
  assert.equal(value.clockIdentity, 'node/clock'); assert.equal(value.cutoffOffsetMs, 1000.25);
});
test('a later shutdown start cannot contaminate the measured window', () => {
  const row = { tool:'git',args:['rev-parse'],startedUtc:at(1100) };
  const result = windowLoad(browser(), [], [row]);
  assert.equal(result.load.count, 0); assert.equal(result.laterStarts, 1);
  assert.equal(result.bounds.missingFinalsBounded, true);
});
test('an unknown start inside the measured window remains unknown', () => {
  const result = windowLoad(browser(), [], [{tool:'bd',args:['list'],startedUtc:at(200)}]);
  assert.equal(result.load.count, null); assert.equal(result.bounds.nativeInclusiveMsUpper, null);
  assert.equal(result.bounds.missingFinalsBounded, false);
});
test('a timed-out command with independent settlement has bounded time and unknown bytes', () => {
  const result = windowLoad(browser(), [], [{tool:'bd',args:['comments'],startedUtc:at(200),settledByUtc:at(700)}]);
  assert.equal(result.bounds.nativeInclusiveMsUpper, 500);
  assert.equal(result.bounds.bytesUpper, null); assert.equal(result.bounds.missingFinalsBounded, true);
});
test('missing clock and final native evidence never produce an empty successful load', () => {
  for (const change of [v=>delete v.clockOriginUtc, v=>delete v.clockIdentity,
    v=>v.external.pop(), v=>v.external[0].args=['blocked','--json'],
    v=>v.external[0].settledMs=800, v=>v.samples=[]]) {
    const value=browser(); change(value);
    const result=windowLoad(value, [], []);
    assert.equal(result.window.available, false); assert.equal(result.load, null); assert.equal(result.bounds, null);
  }
});

test('settlement requires matching wrapper and delegated process identities', () => {
  const pending = { pid: 1, startedUtc: at(100) }, launched = { pid: 2 };
  const observed = { schema: 'nightweaver-tool-observation-v1', observedUtc: at(900),
    wrapper: { pid: 1, settledByUtc: at(500) }, child: { pid: 2, settledByUtc: at(600) } };
  assert.equal(settlementBoundary(pending, launched, observed), at(601));
  for (const change of [v=>v.wrapper.pid=3, v=>v.child.pid=3,
    v=>delete v.child.settledByUtc, v=>v.wrapper.settledByUtc=at(50),
    v=>v.child.settledByUtc=at(950)]) {
    const value = structuredClone(observed); change(value);
    assert.equal(settlementBoundary(pending, launched, value), null);
  }
  assert.equal(settlementBoundary(pending, null, observed), null);
});
