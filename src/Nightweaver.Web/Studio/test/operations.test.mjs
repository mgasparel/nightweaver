import { test } from 'node:test';
import assert from 'node:assert/strict';
import { QueryCoordinator, InteractionStore, queryKey, observationStatus } from '../src/operations.ts';

const identity = (key = 'attention') => ({ projectId: 'qa', key });
const envelope = (revision = '1', epoch = 'A', key = 'attention', extra = {}) => ({
  schema: 'nightweaver-observation-v1', ...identity(key), epoch, revision, health: 'fresh', data: [revision],
  lastAttemptAt: revision, observedAt: revision,
  policy: { expectedValidationIntervalMs: 60000, maxSnapshotAgeMs: 120000, maxPendingAgeMs: 5000 },
  validatedAgeMs: 0, pendingAgeMs: null, synchronization: { journal: 'current', pending: false }, sources: {}, error: null, targetIntervalMs: 1000, nextAttemptAt: null, ...extra,
});
const settle = async () => { for (let i = 0; i < 8; i++) await Promise.resolve(); };
test('cold-start outage expires across retries and recovers only on backend evidence', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance();
  assert.equal(f.snapshot().connectionState, 'connecting');
  for (let attempt = 0; attempt < 3; attempt++) {
    f.streams.at(-1).failed();
    await f.advance(10000);
  }
  assert.equal(f.snapshot().connectionState, 'disconnected');
  assert.equal(f.snapshot().observation, undefined);
  await f.advance(15000);
  assert.equal(f.snapshot().connectionState, 'disconnected', 'retry cannot restore connection evidence');
  f.hello(); await f.advance();
  assert.equal(f.snapshot().connectionState, 'recovering');
  assert.equal(f.snapshot().synchronized, false);
  f.reads.at(-1).resolve(envelope()); await f.advance();
  assert.equal(f.snapshot().connectionState, 'connected');
  assert.equal(f.snapshot().synchronized, true);
  f.coordinator.dispose();
});

test('retained runtime observations expire on their own allowance while tracker data stays recent', async () => {
  const f = fixture();
  for (const key of ['factory', 'attention']) f.coordinator.subscribe(identity(key), () => {});
  await f.advance(); f.hello(); await f.advance();
  for (const read of f.reads) read.resolve(envelope('1', 'A', read.identity.key,
    read.identity.key === 'factory' ? { policy: null, targetIntervalMs: 2000 } : {}));
  await settle();
  assert.equal(f.coordinator.getSnapshot(identity('factory')).stale, false);
  f.coordinator.suspend();
  await f.advance(5000);
  assert.equal(f.coordinator.getSnapshot(identity('factory')).stale, true);
  assert.equal(f.coordinator.getSnapshot(identity('attention')).stale, false);
  f.coordinator.dispose();
});
test('Product and readiness commands reconcile cleanup along with active tracker resources', async () => {
  const f = fixture();
  for (const key of ['attention', 'workspace:qa.1', 'base:workspace:qa.1', 'section:qa.1:history', 'cleanup:1:all:', 'factory']) f.coordinator.subscribe(identity(key), () => {});
  await f.advance(); f.hello(); await f.advance();
  await f.coordinator.invalidateAfterCommand('tracker');
  assert.deepEqual(f.posts[0].keys.sort(), ['attention', 'workspace:qa.1', 'base:workspace:qa.1', 'section:qa.1:history', 'cleanup:1:all:'].sort());
  await f.coordinator.invalidateAfterCommand('cleanup');
  assert.deepEqual(f.posts[1].keys.sort(), ['cleanup:1:all:', 'factory'].sort());
  f.coordinator.dispose();
});
function fixture() {
  let now = 0; let serial = 0;
  const jobs = new Map(); const streams = []; const reads = []; const posts = []; const wakes = [];
  const clock = { now: () => now, random: () => 1,
    later(callback, ms) { const id = ++serial; jobs.set(id, { at: now + ms, callback }); return () => jobs.delete(id); } };
  const transport = {
    connect(project, keys, event, failed) {
      assert.equal(streams.filter(stream => !stream.closed).length, 0, 'only one live stream');
      const stream = { project, keys, event, failed, closed: false }; streams.push(stream);
      return () => { stream.closed = true; };
    },
    read(identity, signal) { return new Promise((resolve, reject) => reads.push({ identity, signal, resolve, reject })); },
    async refresh(project, keys) { posts.push({ project, keys }); },
    async wake(project, keys) { wakes.push({ project, keys }); },
  };
  const coordinator = new QueryCoordinator(transport, clock);
  const advance = async (ms = 0) => {
    const end = now + ms;
    for (let loops = 0; ; loops++) {
      assert.ok(loops < 1000, 'bounded timer work');
      const job = [...jobs.entries()].filter(([, job]) => job.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
      if (!job) break;
      now = job[1].at; jobs.delete(job[0]); job[1].callback(); await settle();
    }
    now = end; await settle();
  };
  const send = (name, fields = {}, index = streams.length - 1) => streams[index].event(name, {
    schema: 'nightweaver-operations-v1', projectId: 'qa', epoch: 'A', ...fields,
  });
  const hello = (fields = {}) => send('hello', { versions: Object.fromEntries(streams.at(-1).keys.map(key => [key, '1'])), ...fields });
  return { coordinator, transport, streams, reads, posts, wakes, jobs, advance, send, hello, snapshot: (key) => coordinator.getSnapshot(identity(key)) };
}

test('bootstrap waits for hello and coalesces a notification during the initial deferred read', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance();
  assert.equal(f.reads.length, 0); f.hello(); await f.advance();
  assert.equal(f.reads.length, 1);
  for (let version = 2; version <= 100; version++) f.send('invalidation', { versions: { attention: String(version) } });
  f.reads[0].resolve(envelope('1')); await f.advance();
  assert.equal(f.snapshot().observation, undefined); assert.equal(f.reads.length, 2);
  f.reads[1].resolve(envelope('100')); await f.advance();
  assert.equal(f.snapshot().observation.revision, '100'); assert.equal(f.snapshot().stale, false);
  f.coordinator.dispose(); assert.equal(f.jobs.size, 0);
});

test('pre-hello invalidations are bounded to active keys and survive handshake sampling', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance();
  f.send('invalidation', { versions: { attention: '5', unknown: '999' } });
  f.send('invalidation', { versions: { attention: '3' } });
  await f.advance(); assert.equal(f.reads.length, 0);
  f.hello(); await f.advance(); f.reads[0].resolve(envelope('1')); await f.advance();
  assert.equal(f.snapshot().observation, undefined);
  await f.advance(2000); f.reads[1].resolve(envelope('5')); await f.advance();
  assert.equal(f.snapshot().observation.revision, '5'); f.coordinator.dispose();
});

test('healthy status stays current through successive invalidations and deferred replacement reads', async () => {
  const f = fixture(); const states = [];
  f.coordinator.subscribe(identity(), () => states.push(f.snapshot().stale));
  await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope()); await f.advance(); states.length = 0;
  for (let version = 2; version <= 5; version++) {
    f.send('invalidation', { versions: { attention: String(version) } }); await f.advance();
    await f.advance(500);
    f.reads.at(-1).resolve(envelope(String(version))); await f.advance();
    await f.advance(500);
  }
  assert.ok(states.length > 0);
  assert.ok(states.every(stale => !stale), 'Routine live updates must not flash a healthy observation to stale');
  f.coordinator.dispose();
});

test('notifications cannot postpone delayed status when replacement reads keep lagging', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {});
  await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope()); await f.advance();
  for (let version = 2; version <= 11; version++) {
    f.send('invalidation', { versions: { attention: String(version) } }); await f.advance();
    f.reads.at(-1).resolve(envelope()); await f.advance();
    assert.equal(f.snapshot().stale, false);
    await f.advance(1000);
  }
  assert.equal(f.snapshot().stale, false);
  assert.equal(f.snapshot().dataState, 'delayed');
  assert.equal(f.snapshot().observation.revision, '1', 'Lagging responses cannot replace validated data');
  f.reads.at(-1).resolve(envelope('11')); await f.advance();
  assert.equal(f.snapshot().stale, false);
  f.coordinator.dispose(); assert.equal(f.jobs.size, 0);
});

test('replacement responses preserve authoritative source health and cancel the catch-up deadline', async () => {
  for (const health of ['fresh', 'stale', 'unavailable']) {
    const f = fixture(); f.coordinator.subscribe(identity(), () => {});
    await f.advance(); f.hello(); await f.advance();
    f.reads[0].resolve(envelope()); await f.advance();
    f.send('invalidation', { versions: { attention: '2' } }); await f.advance();
    f.reads.at(-1).resolve(envelope('2', 'A', 'attention', { health })); await f.advance();
    assert.equal(f.snapshot().observation.health, health);
    assert.equal(f.jobs.size, 1, 'Only the polling timer remains');
    f.coordinator.dispose(); assert.equal(f.jobs.size, 0);
  }
});

test('pending catch-up deadlines end with the subscription or connection lifetime', async () => {
  for (const action of ['unsubscribe', 'suspend', 'dispose']) {
    const f = fixture(); const off = f.coordinator.subscribe(identity(), () => {});
    await f.advance(); f.hello(); await f.advance();
    f.reads[0].resolve(envelope()); await f.advance();
    f.send('invalidation', { versions: { attention: '2' } }); await f.advance();
    assert.equal(f.jobs.size, 3, 'Poll, read deadline and fixed catch-up deadline');
    if (action === 'unsubscribe') off();
    if (action === 'suspend') f.coordinator.suspend();
    if (action === 'dispose') f.coordinator.dispose();
    await f.advance();
    assert.equal(f.jobs.size, action === 'dispose' ? 0 : 1, action);
    f.coordinator.dispose(); assert.equal(f.jobs.size, 0);
  }
});

test('multiple subscribers, repeated mounts and rapid selections share one stream and discard old lifetimes', async () => {
  const f = fixture(); const first = f.coordinator.subscribe(identity(), () => {});
  const second = f.coordinator.subscribe(identity(), () => {}); await f.advance();
  f.hello(); await f.advance(); first(); await f.advance(); assert.equal(f.streams.length, 1);
  second(); const third = f.coordinator.subscribe(identity('workspace:qa-2'), () => {}); third();
  f.coordinator.subscribe(identity('workspace:qa-3'), () => {}); await f.advance();
  assert.equal(f.streams.length, 2); assert.deepEqual(f.streams[1].keys, ['workspace:qa-3']);
  f.hello(); await f.advance(); f.reads[0].resolve(envelope('500')); await f.advance();
  assert.equal(f.snapshot().observation, undefined);
  f.reads[1].resolve(envelope('2', 'A', 'workspace:qa-3')); await f.advance();
  assert.equal(f.snapshot('workspace:qa-3').observation.revision, '2');
  f.coordinator.dispose(); assert.ok(f.streams.every(stream => stream.closed));
});

test('reconnect gap resync retains same-epoch watermark and rejects late old responses', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope('9007199254740994')); await f.advance();
  await f.advance(2000); assert.equal(f.reads.length, 2);
  f.streams[0].failed(); assert.equal(f.snapshot().connectionState, 'recovering');
  await f.advance(1000); f.hello(); await f.advance();
  f.reads[1].resolve(envelope('9007199254740999')); f.reads[2].resolve(envelope('2')); await f.advance();
  assert.equal(f.snapshot().observation.revision, '9007199254740994'); assert.equal(f.snapshot().stale, false);
  f.send('invalidation', { versions: { attention: '9007199254740995' } }); await f.advance();
  f.reads.at(-1).resolve(envelope('9007199254740995')); await f.advance();
  assert.equal(f.snapshot().observation.revision, '9007199254740995'); f.coordinator.dispose();
});

test('new epoch marks retained display unsynchronized, resets watermarks and rejects superseded epoch HTTP', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope('900')); await f.advance(); await f.advance(2000);
  f.send('heartbeat', { epoch: 'B' }); f.hello({ epoch: 'B' }); await f.advance();
  assert.equal(f.snapshot().synchronized, false); assert.equal(f.snapshot().dataState, 'refreshing'); assert.equal(f.snapshot().observation.epoch, 'A');
  f.reads[1].resolve(envelope('999')); f.reads[2].resolve(envelope('1', 'B')); await f.advance();
  assert.equal(f.snapshot().observation.epoch, 'B'); assert.equal(f.snapshot().observation.revision, '1');
  f.coordinator.dispose();
});

test('HTTP epoch replacement triggers a new handshake instead of publishing across epochs', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope('1', 'B')); await f.advance();
  assert.equal(f.streams.length, 2); assert.equal(f.snapshot().observation, undefined);
  f.hello({ epoch: 'B' }); await f.advance(); f.reads[1].resolve(envelope('1', 'B')); await f.advance();
  assert.equal(f.snapshot().observation.epoch, 'B'); f.coordinator.dispose();
});

test('overflow and equal-version resync refetch freshness, transient HTTP errors recover without commands', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope()); await f.advance();
  f.send('resync', { reason: 'overflow' }); await f.advance(); f.reads[1].reject(Error('temporary')); await f.advance();
  assert.equal(f.snapshot().stale, false); assert.match(f.snapshot().error, /temporary/);
  await f.advance(2000); f.reads[2].resolve(envelope('1', 'A', 'attention', { observedAt: 'later' })); await f.advance();
  assert.equal(f.snapshot().observation.observedAt, 'later'); assert.equal(f.snapshot().error, undefined);
  assert.equal(f.posts.length, 0); f.coordinator.dispose();
});

test('stalled reads expire under healthy heartbeats and recovery rejects late completions', async () => {
  for (const trigger of ['poll', 'invalidation', 'refresh', 'wake']) {
    const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
    f.reads[0].resolve(envelope()); await f.advance(); await f.advance(2000);
    const stalled = f.reads[1];
    for (let i = 0; i < 4; i++) { f.send('heartbeat'); await f.advance(2000); }
    assert.equal(stalled.signal.aborted, false);
    if (trigger === 'invalidation') f.send('invalidation', { versions: { attention: '2' } });
    if (trigger === 'refresh') await f.coordinator.refresh([identity()]);
    if (trigger === 'wake') { f.coordinator.setVisible(false); f.coordinator.setVisible(true); f.hello(); }
    f.send('heartbeat'); await f.advance(2000);
    assert.equal(stalled.signal.aborted, true, trigger);
    assert.equal(f.snapshot().connectionState, trigger === 'wake' ? 'recovering' : 'connected');
    assert.equal(f.snapshot().stale, false);
    if (trigger !== 'wake') assert.match(f.snapshot().error, /timed out/i);
    assert.equal(f.snapshot().observation.revision, '1'); assert.equal(f.reads.length, 3);
    f.reads[2].resolve(envelope('2')); await f.advance();
    assert.equal(f.snapshot().stale, false); assert.equal(f.snapshot().error, undefined);
    stalled.resolve(envelope('999')); await f.advance();
    assert.equal(f.snapshot().observation.revision, '2');
    assert.equal(f.posts.length, trigger === 'refresh' ? 1 : 0);
    assert.equal(f.streams.length, trigger === 'wake' ? 2 : 1); f.coordinator.dispose(); assert.equal(f.jobs.size, 0);
  }
});

test('read deadlines are canceled on settlement, unsubscribe, suspension and disposal', async () => {
  for (const action of ['resolve', 'reject', 'unsubscribe', 'suspend', 'dispose']) {
    const f = fixture(); const off = f.coordinator.subscribe(identity(), () => {});
    await f.advance(); f.hello(); await f.advance();
    const read = f.reads[0]; assert.equal(f.jobs.size, 2, 'poll timer and read deadline');
    if (action === 'resolve') read.resolve(envelope());
    if (action === 'reject') read.reject(Error('temporary'));
    if (action === 'unsubscribe') off();
    if (action === 'suspend') f.coordinator.suspend();
    if (action === 'dispose') f.coordinator.dispose();
    await f.advance();
    assert.equal(f.jobs.size, action === 'dispose' ? 0 : 1, action);
    f.coordinator.dispose(); assert.equal(f.jobs.size, 0);
    read.resolve(envelope('999')); await f.advance(15000); assert.equal(f.jobs.size, 0);
  }
});

test('hidden tabs release the stream, retain data and reconcile on wake', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope('1', 'A', 'attention', { health: 'stale' })); await f.advance();
  f.coordinator.setVisible(false); await f.advance(30000);
  assert.equal(f.reads.length, 1); assert.equal(f.streams[0].closed, true);
  assert.equal(f.snapshot().observation.health, 'stale');
  f.coordinator.setVisible(true); f.hello(); await f.advance();
  assert.equal(f.streams.length, 2); assert.equal(f.reads.length, 2);
  assert.deepEqual(f.wakes, [{ project: 'qa', keys: ['attention'] }]);
  assert.equal(f.snapshot().synchronized, false);
  f.reads[1].resolve(envelope()); await f.advance();
  assert.equal(f.snapshot().synchronized, true); assert.equal(f.snapshot().stale, false);
  f.coordinator.dispose(); assert.equal(f.jobs.size, 0);
});

test('manual recovery coalesces refresh POST, clears only after later attempt, commands invalidate observations only', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope()); await f.advance();
  await Promise.all([f.coordinator.refresh([identity()]), f.coordinator.refresh([identity()])]); await f.advance();
  assert.equal(f.posts.length, 1); assert.equal(f.snapshot().refreshing, true);
  f.reads[1].resolve(envelope()); await f.advance(); assert.equal(f.snapshot().refreshing, true);
  await f.advance(2000); f.reads[2].resolve(envelope('1', 'A', 'attention', { lastAttemptAt: 'later' })); await f.advance();
  assert.equal(f.snapshot().refreshing, false);
  await f.coordinator.invalidateAfterCommand('tracker'); assert.deepEqual(f.posts[1].keys, ['attention']);
  f.coordinator.dispose();
});

test('stalled refresh settles with uncertain outcome and only explicit attempts retry it', async () => {
  const f = fixture(); const attempts = [];
  f.transport.refresh = (project, keys, signal) => new Promise((resolve, reject) => attempts.push({ resolve, reject, signal }));
  f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope()); await f.advance();
  let failure;
  const first = f.coordinator.refresh([identity()]).catch(error => { failure = error; });
  for (let i = 0; i < 5; i++) {
    f.send('heartbeat'); await f.advance(2000);
    f.reads.at(-1).resolve(envelope()); await f.advance();
  }
  assert.match(String(failure), /timed out.*unknown/i);
  await first;
  assert.equal(attempts[0].signal.aborted, true);
  assert.equal(f.snapshot().refreshing, false);
  assert.match(f.snapshot().error, /timed out.*unknown/i);
  f.coordinator.wake(); await f.advance(); assert.equal(attempts.length, 1, 'no automatic POST replay');
  const second = f.coordinator.refresh([identity()]);
  attempts[0].resolve(); await f.advance();
  assert.equal(f.snapshot().refreshing, true); assert.equal(f.snapshot().error, undefined);
  const duplicate = f.coordinator.refresh([identity()]);
  assert.equal(attempts.length, 2, 'late settlement cannot remove replacement deduplication');
  attempts[1].resolve(); await Promise.all([second, duplicate]);
  f.coordinator.dispose(); assert.equal(f.jobs.size, 0);
});

test('refresh deadlines and transport resources are canceled on settlement, suspension and disposal', async () => {
  for (const action of ['resolve', 'reject', 'suspend', 'dispose']) {
    const f = fixture(); let attempt;
    f.transport.refresh = (project, keys, signal) => new Promise((resolve, reject) => { attempt = { resolve, reject, signal }; });
    const operation = f.coordinator.refresh([identity()]);
    const result = operation.catch(error => error);
    assert.equal(f.jobs.size, 2);
    if (action === 'resolve') attempt.resolve();
    if (action === 'reject') attempt.reject(Error('rejected'));
    if (action === 'dispose') f.coordinator.dispose();
    if (action === 'suspend') f.coordinator.suspend();
    await f.advance();
    assert.equal(f.jobs.size, action === 'dispose' ? 0 : 1, action);
    if (action === 'dispose' || action === 'suspend') {
      assert.equal(attempt.signal.aborted, true);
      assert.match(String(await result), /(?:disposed|suspended).*unknown/i);
      if (action === 'dispose') await assert.rejects(f.coordinator.refresh([identity()]), /disposed/i);
    } else await result;
    f.coordinator.dispose(); attempt.reject(Error('late')); await f.advance(15000);
    assert.equal(f.jobs.size, 0);
  }
});

test('reconnect and wake do not replay refresh and late rejection cannot fail its replacement', async () => {
  const f = fixture(); const attempts = [];
  f.transport.refresh = (project, keys, signal) => new Promise((resolve, reject) => attempts.push({ resolve, reject, signal }));
  f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  const expired = f.coordinator.refresh([identity()]).catch(error => error);
  f.streams[0].failed(); await f.advance(1000); f.hello(); f.coordinator.wake();
  await f.advance(9000); assert.match(String(await expired), /timed out/);
  assert.equal(attempts.length, 1);
  const replacement = f.coordinator.refresh([identity()]);
  attempts[0].reject(Error('late failure')); await f.advance();
  assert.equal(f.snapshot().error, undefined); assert.equal(f.snapshot().refreshing, true);
  const duplicate = f.coordinator.refresh([identity()]); assert.equal(attempts.length, 2);
  attempts[1].resolve(); await Promise.all([replacement, duplicate]);
  f.coordinator.dispose(); assert.equal(f.jobs.size, 0);
});

test('same-epoch server eviction readmission accepts higher unavailable revision before changed or unchanged data', async () => {
  for (const changed of [false, true]) {
    const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
    f.reads[0].resolve(envelope('8')); await f.advance(); f.streams[0].failed(); await f.advance(1000);
    f.hello({ versions: { attention: '10' } }); await f.advance();
    f.reads[1].resolve(envelope('10', 'A', 'attention', { health: 'unavailable', data: null })); await f.advance();
    assert.equal(f.snapshot().observation.data, null); assert.equal(f.snapshot().observation.health, 'unavailable');
    f.send('invalidation', { versions: { attention: '11' } }); await f.advance();
    f.reads[2].resolve(envelope('11', 'A', 'attention', { data: changed ? ['changed'] : ['8'] })); await f.advance();
    assert.equal(f.snapshot().observation.revision, '11'); f.coordinator.dispose();
  }
});

test('bounded caches reject pinned growth, expire inactive entries and keep drafts separate', async () => {
  const f = fixture();
  for (let i = 0; i < 70; i++) { const off = f.coordinator.subscribe(identity(`workspace:qa-${i}`), () => {}); off(); }
  await f.advance(30000); assert.equal(f.snapshot('workspace:qa-69').observation, undefined);
  const off = [];
  for (let i = 0; i < 32; i++) off.push(f.coordinator.subscribe(identity(`workspace:pin-${i}`), () => {}));
  assert.throws(() => f.coordinator.subscribe(identity('factory'), () => {}), /Too many/);
  f.coordinator.dispose();
  const drafts = new InteractionStore(); for (let i = 0; i < 20; i++) drafts.set('qa', String(i), 'unsent');
  assert.throws(() => drafts.set('qa', '21', 'another'), /Draft storage limit/);
  assert.throws(() => drafts.set('qa', '0', 'x'.repeat(1024 * 1024)), /Draft storage limit/);
  assert.equal(drafts.get('qa', '0'), 'unsent'); drafts.discard('qa', '0'); drafts.set('qa', '21', 'another');
  assert.equal(drafts.get('foreign', '21'), '');
});

test('normalized resource keys preserve identity and Unicode interior whitespace', () => {
  assert.equal(queryKey('explorer', 'ready', '  a  b  '), 'explorer:ready:YSAgYg');
  assert.equal(queryKey('cleanup', 'all', '', 2), 'cleanup:2:all:');
  assert.equal(queryKey('explorer', 'all', 'é'), 'explorer:all:w6k');
});

test('equal-revision reconnect accepts refreshed metadata, stale stream callbacks cannot publish', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope('8')); await f.advance(); f.streams[0].failed(); await f.advance(1000);
  f.hello({ versions: { attention: '8' } }); await f.advance();
  f.send('invalidation', { versions: { attention: '999' } }, 0);
  f.reads[1].resolve(envelope('8', 'A', 'attention', { observedAt: 'later' })); await f.advance();
  assert.equal(f.snapshot().observation.observedAt, 'later'); assert.equal(f.snapshot().stale, false);
  await f.advance(2000); f.reads[2].resolve(envelope('7')); await f.advance();
  assert.equal(f.snapshot().observation.revision, '8'); assert.equal(f.snapshot().stale, false);
  f.coordinator.dispose();
});

test('cache TTL removes display and watermark; old HTTP lifetime cannot update a recreated identity', async () => {
  const f = fixture(); const off = f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope('80')); await f.advance(); await f.advance(2000); off(); await f.advance(30000);
  assert.equal(f.snapshot().observation, undefined);
  f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[1].resolve(envelope('90')); f.reads[2].resolve(envelope('1')); await f.advance();
  assert.equal(f.snapshot().observation.revision, '1'); f.coordinator.dispose();
});

test('pinned byte limit retains prior display with a visible error', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope()); await f.advance(); await f.advance(2000);
  f.reads[1].resolve(envelope('2', 'A', 'attention', { data: 'x'.repeat(16 * 1024 * 1024) })); await f.advance();
  assert.equal(f.snapshot().observation.revision, '1'); assert.match(f.snapshot().error, /storage limit/);
  assert.equal(f.snapshot().stale, false); f.coordinator.dispose();
});

test('refresh before initial observation stays pending until a real attempt', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello();
  await f.coordinator.refresh([identity()]); await f.advance();
  f.reads[0].resolve(envelope('1', 'A', 'attention', { lastAttemptAt: null, health: 'unavailable', data: null })); await f.advance();
  assert.equal(f.snapshot().refreshing, true); await f.advance(2000);
  f.reads[1].resolve(envelope()); await f.advance(); assert.equal(f.snapshot().refreshing, false); f.coordinator.dispose();
});

test('LRU eviction removes only inactive responses and a refresh rejection remains recoverable', async () => {
  const f = fixture(); let off;
  for (let i = 0; i < 65; i++) {
    off?.(); const key = `workspace:qa-${i}`;
    off = f.coordinator.subscribe(identity(key), () => {}); await f.advance(); f.hello(); await f.advance();
    f.reads.at(-1).resolve(envelope('1', 'A', key)); await f.advance();
  }
  assert.equal(f.snapshot('workspace:qa-0').observation, undefined);
  assert.equal(f.snapshot('workspace:qa-64').observation.revision, '1');
  const refresh = f.transport.refresh;
  f.transport.refresh = async () => { throw Error('capacity 429'); };
  await assert.rejects(f.coordinator.refresh([identity('workspace:qa-64')]), /capacity/);
  assert.equal(f.snapshot('workspace:qa-64').refreshing, false);
  assert.match(f.snapshot('workspace:qa-64').error, /capacity/);
  f.transport.refresh = refresh;
  await f.coordinator.refresh([identity('workspace:qa-64')]);
  assert.equal(f.snapshot('workspace:qa-64').error, undefined); f.coordinator.dispose();
});

test('tracker age uses receipt elapsed time, retained data expires while disconnected and recovers on validation', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope('1', 'A', 'attention', { observedAt: '2099-01-01', validatedAgeMs: 119000 })); await f.advance();
  assert.equal(f.snapshot().stale, false);
  f.coordinator.setVisible(false); await f.advance(2000);
  assert.equal(f.snapshot().dataState, 'stale'); assert.equal(f.snapshot().observation.data[0], '1');
  f.coordinator.setVisible(true); f.hello(); await f.advance();
  assert.equal(f.snapshot().synchronized, false);
  f.reads.at(-1).resolve(envelope('1', 'A', 'attention', { observedAt: '1900-01-01', validatedAgeMs: 0 })); await f.advance();
  assert.equal(f.snapshot().dataState, 'current'); assert.equal(f.snapshot().synchronized, true);
  f.coordinator.dispose();
});

test('pending response receipts cannot postpone five-second warning or imply accepted-body validation', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope('1', 'A', 'attention', { validatedAgeMs: 100000, pendingAgeMs: 4000,
    synchronization: { journal: 'current', pending: true } })); await f.advance();
  assert.equal(f.snapshot().dataState, 'refreshing');
  await f.advance(2000); assert.equal(f.snapshot().dataState, 'delayed');
  f.reads.at(-1).resolve(envelope('1', 'A', 'attention', { validatedAgeMs: 102000, pendingAgeMs: 6000,
    synchronization: { journal: 'current', pending: true } })); await f.advance();
  assert.equal(f.snapshot().dataState, 'delayed');
  f.coordinator.dispose();
});

test('valid HTTP proves backend reachability during channel recovery; hello alone does not complete recovery', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope()); await f.advance(); f.streams[0].failed();
  assert.equal(f.snapshot().connectionState, 'recovering'); assert.equal(f.snapshot().stale, false);
  await f.advance(1000); f.hello(); await f.advance();
  f.reads.at(-1).reject(Error('read failed')); await f.advance();
  assert.equal(f.snapshot().connectionState, 'recovering'); assert.equal(f.snapshot().synchronized, false);
  await f.advance(2000); f.reads.at(-1).resolve(envelope()); await f.advance();
  assert.equal(f.snapshot().connectionState, 'connected'); assert.equal(f.posts.length, 0);
  f.coordinator.dispose();
});

test('journal backlog and permanent reader failure are visible independently of a recent accepted snapshot', async () => {
  const f = fixture(); f.coordinator.subscribe(identity(), () => {}); await f.advance(); f.hello(); await f.advance();
  f.reads[0].resolve(envelope('1', 'A', 'attention', { synchronization: { journal: 'catching-up', pending: false } })); await f.advance();
  assert.match(observationStatus(f.snapshot()), /Catching up/); assert.equal(f.snapshot().stale, false);
  await f.advance(2000);
  f.reads[1].resolve(envelope('2', 'A', 'attention', { synchronization: { journal: 'degraded', pending: false,
    error: { code: 'journal-settlement', message: 'Inspect Web host diagnostics before restarting it.', retryable: false } } })); await f.advance();
  assert.match(observationStatus(f.snapshot()), /Inspect Web host/); assert.equal(f.snapshot().connectionState, 'connected');
  f.coordinator.dispose();
});

test('backlog never masks source failure, pending delay or accepted-body expiry', () => {
  const base = { connectionState: 'connected', refreshing: false, stale: false,
    observation: envelope('1', 'A', 'attention', { synchronization: { journal: 'catching-up', pending: true } }) };
  assert.match(observationStatus({ ...base, dataState: 'delayed' }), /Updates delayed.*Catching up/);
  assert.match(observationStatus({ ...base, stale: true, dataState: 'stale' }), /Snapshot stale.*Catching up/);
  assert.match(observationStatus({ ...base, observation: { ...base.observation, error: { message: 'Tracker read failed' } } }), /Tracker read failed.*Catching up/);
});
