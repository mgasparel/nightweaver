import { test } from 'node:test';
import assert from 'node:assert/strict';
import { assertSample, summarizeTools, toolCommand, boundTools } from './shared-read-assertions.mjs';

test('guarded actor and commit options are not command families', () => {
  assert.equal(toolCommand({ tool: 'bd', args: ['--sandbox','--actor','local-console','--dolt-auto-commit','on','comments','add','qa-read-000'] }), 'comments');
});
test('equals-form global options keep the actual command', () => {
  assert.equal(toolCommand({ tool: 'bd', args: ['--dolt-auto-commit=on','config','set','events-journal','true'] }), 'config');
});
test('Git directory options do not become command families', () => {
  assert.equal(toolCommand({ tool: 'git', args: ['-C','H:/fixture/repo','rev-parse','--git-dir'] }), 'rev-parse');
});

const sample = () => ({ error: null, httpMs: 1, usefulMs: 3, detailMs: 4,
  proof: { expected: true, rendered: ['qa-live-1'], resources: [
    { epoch: 'epoch', revision: '1', sources: { inventory: {} }, health: 'fresh', dataPresent: true }] } });
test('accepts complete useful content with source evidence', () => assertSample(sample()));
test('accepts validated base facts with incomplete optional source state', () => {
  const value = sample(); value.proof.resources[0].health = 'stale';
  value.proof.resources[0].required = ['inventory']; value.proof.resources[0].sources.inventory.health = 'fresh';
  assertSample(value);
});
test('rejects an invalid required base source', () => {
  const value = sample(); value.proof.resources[0].health = 'stale';
  value.proof.resources[0].required = ['inventory']; value.proof.resources[0].sources.inventory.health = 'unavailable';
  assert.throws(() => assertSample(value));
});
test('does not invent an HTTP receipt for validated retained data', () => {
  const value = sample(); value.httpMs = null; value.retainedReceipt = true; assertSample(value);
});
for (const field of ['httpMs', 'usefulMs', 'detailMs']) {
  test(`rejects missing ${field}`, () => { const value = sample(); value[field] = null; assert.throws(() => assertSample(value)); });
}
for (const change of [value => { value.error = 'timeout'; }, value => { value.proof.rendered = []; },
  value => { value.proof.resources[0].dataPresent = false; }, value => { value.proof.resources[0].health = 'stale'; },
  value => { value.proof.expected = false; }]) {
  test('rejects failed, partial or empty evidence', () => { const value = sample(); change(value); assert.throws(() => assertSample(value)); });
}
test('counts journal and source commands and retains unavailable wait attribution', () => {
  const at = '2026-11-23T00:00:00.000Z';
  const result = summarizeTools([
    { tool: 'bd', args: ['--readonly', '--sandbox', 'events', 'list'], startedUtc: at, elapsedMs: 1000, outputBytes: 20, errorBytes: 0, exitCode: 0 },
    { tool: 'bd', args: ['--readonly', 'list'], startedUtc: at, elapsedMs: 500, outputBytes: 30, errorBytes: 1, exitCode: 1 }
  ], at, '2026-11-23T00:00:02.000Z');
  assert.equal(result.count, 2); assert.equal(result.families['bd:events'], 1);
  assert.equal(result.bytes, 51); assert.equal(result.errors, 1); assert.equal(result.peakConcurrency, 2);
  assert.equal(result.gateWaitMs, null);
});
test('reports unavailable load below UTC correlation resolution', () => {
  const value = summarizeTools([], '2026-11-23T00:00:00.000Z', '2026-11-23T00:00:00.000Z');
  assert.equal(value.count, null); assert.equal(value.commandsPerSecond, null);
});

test('does not claim complete load from commands without final records', () => {
  const start = '2026-11-23T00:00:01.000Z', end = '2026-11-23T00:00:02.000Z';
  const result = summarizeTools([
    { tool: 'bd', args: ['list'], startedUtc: start, elapsedMs: 100, outputBytes: 20, errorBytes: 0, exitCode: 0 }
  ], start, end, [
    { tool: 'bd', args: ['events', 'list'], startedUtc: '2026-11-23T00:00:00.000Z' },
    { tool: 'git', args: ['status'], startedUtc: start }
  ]);
  for (const key of ['count','commandsPerSecond','families','bytes','nativeInclusiveMs','errors','peakConcurrency']) assert.equal(result[key], null);
  assert.equal(result.startedCount, 2); assert.equal(result.startedFamilies['git:status'], 1);
  assert.equal(result.settledOnly.count, 1); assert.equal(result.unsettledRecords.length, 2);
});

test('independent settlement excludes a pre-window gap but never invents its bytes', () => {
  const gap = { tool: 'bd', args: ['list'], startedUtc: '2026-11-23T00:00:00Z',
    settledByUtc: '2026-11-23T00:00:00.500Z' };
  const start = '2026-11-23T00:00:01Z', end = '2026-11-23T00:00:02Z';
  assert.equal(summarizeTools([], start, end, [gap]).count, 0);
  assert.equal(boundTools([], [gap], start, end).possibleMissingFinalCount, 0);
});

test('missing final inside a window has bounded time and unknown bytes', () => {
  const start = '2026-11-23T00:00:01Z', end = '2026-11-23T00:00:02Z';
  const value = boundTools([], [{ tool: 'bd', args: ['list'], startedUtc: start,
    settledByUtc: '2026-11-23T00:00:01.500Z' }], start, end);
  assert.equal(value.startedCount, 1); assert.equal(value.overlapCountLower, 0);
  assert.equal(value.overlapCountUpper, 1); assert.equal(value.nativeInclusiveMsUpper, 500);
  assert.equal(value.nativeInclusiveMsLower, 0); assert.equal(value.bytesUpper, null);
  assert.equal(value.missingFinalsBounded, true);
});

test('missing or contradictory settlement cannot bound the window', () => {
  const start = '2026-11-23T00:00:01Z', end = '2026-11-23T00:00:02Z';
  for (const settledByUtc of [null, 'invalid', '2026-11-23T00:00:00Z']) {
    const row = { tool: 'bd', args: ['list'], startedUtc: start, settledByUtc };
    const value = boundTools([], [row], start, end);
    // A timestamp before the recorded start is not a valid absence proof.
    assert.equal(value.startedCount, 1);
    assert.equal(value.missingFinalsBounded, false);
    assert.equal(value.nativeInclusiveMsUpper, null);
  }
  assert.equal(boundTools([], [{ startedUtc: start }], start, end).nativeInclusiveMsUpper, null);
});
