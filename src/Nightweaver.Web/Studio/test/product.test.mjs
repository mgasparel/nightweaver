import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ProductCommands, emptyProduct } from '../src/product-commands.ts';

const draft = { ...emptyProduct(), title: 'Literal <script>', description: 'User input', acceptanceCriteria: 'Keep it' };
test('intake sends only Product fields and verifies the receipt without readiness', async () => {
  const calls = [], invalidations = [];
  const owner = new ProductCommands(async (url, init) => {
    calls.push({ url, init });
    return Response.json({ id: 'qa.1', type: draft.type, priority: draft.priority, status: 'open' }, { status: 201 });
  }, kind => invalidations.push(kind));
  await owner.create('intake', draft);
  assert.deepEqual(JSON.parse(calls[0].init.body), draft);
  assert.equal(calls[0].init.headers['X-Nightweaver-Request'], 'console-v1');
  assert.equal(calls[0].url, '/api/beads');
  assert.equal(owner.get('intake').created, 'qa.1');
  await owner.create('intake', draft);
  assert.equal(calls.length, 1, 'confirmed creation requires a new explicit draft');
  assert.deepEqual(invalidations, ['tracker']);
});
test('failed validation retains field errors and never changes the supplied draft', async () => {
  const owner = new ProductCommands(async () => Response.json({ errors: { title: ['Invalid title'] } }, { status: 400 }), () => {});
  await owner.create('intake', draft);
  assert.deepEqual(owner.get('intake').errors, { title: ['Invalid title'] });
  assert.equal(draft.title, 'Literal <script>');
  assert.equal(owner.get('intake').review, false);
});
for (const status of [409, 503]) test(`HTTP ${status} requires evidence review before another explicit command`, async () => {
  let count = 0;
  const owner = new ProductCommands(async () => { count++; return Response.json({ detail: 'fixture' }, { status }); }, () => {});
  await owner.ready('qa', 'qa.1', 'A'.repeat(64));
  await owner.ready('qa', 'qa.1', 'B'.repeat(64));
  assert.equal(count, 1); assert(owner.get('qa').review);
  owner.acknowledge('qa');
  await owner.ready('qa', 'qa.1', 'B'.repeat(64));
  assert.equal(count, 2);
});
test('pending and ambiguous network outcomes cannot be replayed across consumers', async () => {
  let reject, count = 0;
  const reconciled = [];
  const owner = new ProductCommands(() => { count++; return new Promise((_, fail) => reject = fail); }, kind => reconciled.push(kind));
  const request = owner.create('intake', draft);
  await owner.create('intake', draft); assert.equal(count, 1);
  reject(Error('disconnect')); await request;
  await owner.create('intake', draft); assert.equal(count, 1);
  assert(owner.get('intake').review); assert.deepEqual(reconciled, ['tracker']);
});
test('readiness preserves exact displayed identity and cleanup retry uses stable owner/revision request ID', async () => {
  const bodies = [];
  const owner = new ProductCommands(async (url, init) => {
    const body = JSON.parse(init.body); bodies.push(body);
    if (url.includes('ready-for-planning')) return Response.json({ beadId: 'qa.1', action: 'ready-for-planning', stateChanged: true });
    return Response.json({ request: { ...body, beadId: 'qa.1', outcome: 'queued' } }, { status: 202 });
  }, () => {});
  await owner.ready('ready', 'qa.1', 'F'.repeat(64));
  assert.deepEqual(bodies[0], { expected: 'F'.repeat(64) });
  await owner.cleanup('cleanup', 'qa.1', 'A'.repeat(32), 'B'.repeat(64));
  await owner.cleanup('cleanup', 'qa.1', 'A'.repeat(32), 'B'.repeat(64));
  assert.deepEqual(bodies[1], bodies[2]);
  await owner.cleanup('cleanup', 'qa.1', 'C'.repeat(32), 'B'.repeat(64));
  assert.notEqual(bodies[2].requestId, bodies[3].requestId);
});
test('foreign or incomplete command receipts stay uncertain', async () => {
  for (const value of [{}, { id: 'qa.1', type: 'task', priority: 2, status: 'open' }]) {
    const owner = new ProductCommands(async () => Response.json(value, { status: 201 }), () => {});
    await owner.create('intake', draft); assert(owner.get('intake').review);
  }
});
test('expired cleanup acknowledgement allows a new explicit request identity', async () => {
  const ids = [];
  const owner = new ProductCommands(async (_, init) => {
    const body = JSON.parse(init.body); ids.push(body.requestId);
    return Response.json({ request: { ...body, beadId: 'qa.1', outcome: 'expired' } }, { status: 202 });
  }, () => {});
  await owner.cleanup('cleanup', 'qa.1', 'A'.repeat(32), 'B'.repeat(64));
  await owner.cleanup('cleanup', 'qa.1', 'A'.repeat(32), 'B'.repeat(64));
  assert.notEqual(ids[0], ids[1]);
});
test('deadline covers stalled response decoding and discards late success', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let release;
  const owner = new ProductCommands(async () => ({ ok: true, status: 201, json: () => new Promise(resolve => release = resolve) }), () => {});
  const request = owner.create('intake', draft);
  await Promise.resolve(); await Promise.resolve();
  t.mock.timers.tick(10000); await request;
  assert(owner.get('intake').review);
  release({ id: 'qa.1', type: 'feature', priority: 2, status: 'open' });
  await Promise.resolve(); assert.equal(owner.get('intake').created, undefined);
});
