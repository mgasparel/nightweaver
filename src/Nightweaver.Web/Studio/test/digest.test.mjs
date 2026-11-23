import assert from 'node:assert/strict';
import { test } from 'node:test';
import { completedOutcomes, digestWithSections, DigestCheckpoints, digestWindow, visibleThreads } from '../src/digest-model.ts';

test('base activity remains readable while matching merge sections complete independently', () => {
  const now = Date.parse('2026-11-23T18:00:00Z');
  const updatedAt = new Date(now - 1000).toISOString();
  const entry = id => ({bead: {id, title: id, status: 'closed', type: 'task', updatedAt}, activityKind: 'closed', activityAt: updatedAt});
  const base = {generatedAt: updatedAt, metrics: {completedTotal: 2}, threads: [], standaloneCompleted: [entry('one'), entry('two')], standaloneOpen: []};
  const section = {beadId: 'one', updatedAt, state: 'available', pending: false, validatedAgeMs: 1000,
    source: {health: 'fresh', observedAt: updatedAt}, value: {state: 'verified', pullRequest: 'https://github.com/a/b/pull/1', cleanupWarning: 'locked report'}};
  const pending = digestWithSections(base, undefined, 0);
  assert.equal(pending.standaloneCompleted[0].outcomeLabel, 'Closed');
  assert.equal(pending.standaloneCompleted[0].mergeEvidence.state, 'loading');
  const progress = digestWithSections(base, {sections: {one: section}}, 0);
  assert.equal(progress.standaloneCompleted[0].outcomeLabel, 'Merged');
  assert.equal(progress.standaloneCompleted[0].mergeEvidence.cleanupWarning, 'locked report');
  assert.equal(progress.standaloneCompleted[1].mergeEvidence.state, 'unavailable');
  assert.equal(digestWithSections(base, {sections: {one: section}}, 120000).standaloneCompleted[0].mergeEvidence.state, 'stale');
  assert.strictEqual(progress.metrics, base.metrics); assert.equal(progress.generatedAt, base.generatedAt);
  for (const patch of [{beadId: 'foreign'}, {updatedAt: '2026-11-23T17:00:00Z'}, {state: 'failed'}, {state: 'stale'}, {pending: true},
    {validatedAgeMs: 120001}, {value: {state: 'invalid'}}]) {
    assert.equal(digestWithSections(base, {sections: {one: {...section, ...patch}}}, 0).standaloneCompleted[0].outcomeLabel, 'Closed');
  }
  for (const state of ['missing', 'invalid', 'unavailable']) {
    const result = digestWithSections(base, {sections: {one: {...section, state: 'unavailable', value: {state}}}}, 0);
    assert.equal(result.standaloneCompleted[0].mergeEvidence.state, state);
  }
  const removed = {...base, standaloneCompleted: [entry('two')]};
  assert.deepEqual(digestWithSections(removed, {sections: {one: section}}, 0).standaloneCompleted.map(item => item.id), ['two']);
});

test('Overview outcomes include closed lineage roots and children once, newest first, without open work', () => {
  const item = (id, status, day) => ({id, status, activityAt: `2026-11-${day}T01:00:00Z`});
  const child = item('child', 'closed', 19);
  const data = {standaloneCompleted: [child, item('standalone', 'closed', 18)], threads: [
    {root: item('open-root', 'open', 20), items: [child, item('open-child', 'in_progress', 20)]},
    {root: item('closed-root', 'closed', 20), items: []},
    {root: {...item('historical-parent', 'closed', 17), activityKind: 'context'}, items: []}
  ]};
  assert.deepEqual(completedOutcomes(data).map(row => row.id), ['closed-root', 'child', 'standalone']);
  assert.deepEqual(completedOutcomes({standaloneCompleted: [], threads: []}), []);
});
const snapshot = { generatedAt: '2026-11-14T01:02:03Z', threads: Array.from({length: 8}, (_, i) => ({root: {id: `root-${i}`}})) };
test('checkpoint captures displayed cutoff across concurrent replacement and duplicate submission', async () => {
  let release; let body; let reconciled = 0; let count = 0;
  const pending = new Promise(resolve => release = resolve);
  const commands = new DigestCheckpoints(async (_url, request) => { count++; body = JSON.parse(request.body); return pending; }, () => reconciled++);
  const displayed = structuredClone(snapshot);
  const save = commands.save('qa', displayed);
  displayed.generatedAt = '2026-11-14T01:05:00Z';
  await commands.save('qa', displayed);
  assert.equal(count, 1); assert.equal(body.caughtUpAt, snapshot.generatedAt);
  release(new Response(JSON.stringify({lastCaughtUpAt: snapshot.generatedAt})));
  await save;
  assert.equal(commands.get('qa').saved, snapshot.generatedAt); assert.equal(reconciled, 1);
  assert.match(commands.get('qa').message, /Later activity remains unread/);
});
test('invalid receipt and save errors preserve a recoverable explicit command with no replay', async () => {
  for (const response of [new Response('{}'), new Response('{"lastCaughtUpAt":"invalid"}'), new Response('{}', {status: 503})]) {
    let count = 0;
    const commands = new DigestCheckpoints(async () => { count++; return response; }, () => {});
    await commands.save('qa', snapshot);
    assert.equal(commands.get('qa').pending, false); assert.equal(commands.get('qa').saved, undefined);
    assert.match(commands.get('qa').message, /could not be verified/); assert.equal(count, 1);
    assert.equal(commands.get('other').message, '');
  }
});
test('long lineage remains bounded until expanded/focused identities move beyond preview', () => {
  assert.equal(visibleThreads(snapshot, false, new Set()).length, 5);
  assert.equal(visibleThreads(snapshot, false, new Set(['root-4'])).length, 5);
  const inserted = { ...snapshot, threads: [{root: {id: 'new'}}, ...snapshot.threads] };
  assert.equal(visibleThreads(inserted, false, new Set(['root-4'])).length, 9);
  assert.equal(visibleThreads(inserted, false, new Set(), 'root-4').length, 9);
  assert.equal(visibleThreads(inserted, false, new Set()).length, 5);
  assert.equal(visibleThreads(snapshot, true, new Set()).length, 8);
});
test('all supported filters normalize without acknowledging activity', () => {
  for (const value of ['auto', 'since_caught_up', '24h', '7d', '30d', 'all']) assert.equal(digestWindow(value), value);
  assert.equal(digestWindow('foreign'), 'auto'); assert.equal(digestWindow(null), 'auto');
});

test('transport deadline releases duplicate guard and rejects a late old receipt', async t => {
  t.mock.timers.enable({apis: ['setTimeout']});
  let release; let count = 0;
  const commands = new DigestCheckpoints(async () => { count++; return new Promise(resolve => release = resolve); }, () => {});
  const first = commands.save('qa', snapshot);
  t.mock.timers.tick(10000); await first;
  assert.equal(commands.get('qa').pending, false); assert.match(commands.get('qa').message, /could not be verified/);
  const newer = {...snapshot, generatedAt: '2026-11-14T01:04:00Z'};
  const oldRelease = release;
  const next = commands.save('qa', newer);
  oldRelease(new Response(JSON.stringify({lastCaughtUpAt: snapshot.generatedAt})));
  await Promise.resolve(); await Promise.resolve();
  assert.equal(commands.get('qa').pending, true);
  release(new Response(JSON.stringify({lastCaughtUpAt: newer.generatedAt})));
  await next;
  assert.equal(commands.get('qa').saved, newer.generatedAt); assert.equal(count, 2);
});
test('receipt body decoding has the same deadline as transport', async t => {
  t.mock.timers.enable({apis: ['setTimeout']});
  const commands = new DigestCheckpoints(async () => ({ok: true, json: () => new Promise(() => {})}), () => {});
  const pending = commands.save('qa', snapshot);
  await Promise.resolve(); await Promise.resolve();
  t.mock.timers.tick(10000); await pending;
  assert.equal(commands.get('qa').pending, false); assert.equal(commands.get('qa').saved, undefined);
});
