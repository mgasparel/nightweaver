import assert from 'node:assert/strict';
import { test } from 'node:test';
import { DigestCheckpoints, digestWindow, visibleThreads } from '../src/digest-model.ts';
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
