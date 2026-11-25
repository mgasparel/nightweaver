import test from 'node:test';
import assert from 'node:assert/strict';
import { ActivityBuffer, ROW_LIMIT, EXPANSION_LIMIT, rowId, mergeRows, selection, levelOf, safeDetail, observationDelay, cursorParts } from '../src/activity-model.ts';
const journal = '12345678-1234-1234-1234-123456789abc';
const runtime = '87654321-1234-1234-1234-123456789abc';
const row = (offset, extra = {}) => ({ schema: 1, position: { journal, runtime, offset, sequence: offset }, kind: 'InvocationStarted', phase: 'Delivery', strength: 'Confirmed', message: 'Invocation started.', source: 'Invocation', sourceReference: 'ref', reason: 'None', bead: 'ns-task.1', invocation: 'inv-1', observedAt: '2026-11-25T12:00:10Z', sourceTime: '2026-11-25T12:00:00Z', freshness: 'Fresh', ...extra });
const page = (rows, extra = {}) => ({ records: rows, details: rows.map(r => ({ id: rowId(r), availability: 'unavailable' })), state: 'Ready', journalHealth: 'Fresh', cursor: rowId(rows.at(-1) ?? row(0)), ...extra });
test('sustained live bursts keep rows, links and expansions finite', () => {
  const b = new ActivityBuffer();
  for (let i = 1; i <= 6000; i++) b.accept(row(i), { id: rowId(row(i)), availability: 'available' });
  assert.equal(b.rows.length, ROW_LIMIT);
  assert.equal(b.details.size, ROW_LIMIT);
  assert.equal(b.rows[0].position.offset, 5501);
  assert(b.gap);
  for (const r of b.rows) b.toggle(rowId(r));
  assert.equal(b.expanded.size, EXPANSION_LIMIT);
});
test('pause holds anchor rows and open evidence through bursts and duplicate replay', () => {
  const b = new ActivityBuffer(); b.page(page([row(1), row(2)])); b.toggle(rowId(row(1)));
  const retained = b.rows;
  for (let i = 3; i < 2000; i++) { b.accept(row(i)); b.accept(row(i)); }
  assert.equal(b.rows, retained); assert.equal(b.pending, 501);
  assert(b.expanded.has(rowId(row(1)))); assert.equal(b.details.size, 2);
  assert.equal(b.cursor, rowId(row(2)));
});
test('cursor catch-up retains order and deduplicates records after reconnect', () => {
  const b = new ActivityBuffer(); b.page(page([row(2), row(3)])); b.paused = true; b.accept(row(4));
  b.paused = false; b.page(page([row(3), row(4)])); b.accept(row(4)); b.accept(row(5));
  assert.deepEqual(b.rows.map(r => r.position.offset), [2, 3, 4, 5]);
  assert.equal(b.cursor, rowId(row(5)));
});
test('older pages keep the visible identities and do not advance live cursor', () => {
  const b = new ActivityBuffer(); b.page(page([row(100), row(101)])); b.toggle(rowId(row(100)));
  b.page(page([row(99), row(100)]), true);
  assert.deepEqual(b.rows.map(r => r.position.offset), [99, 100, 101]);
  assert.equal(b.cursor, rowId(row(101))); assert(b.expanded.has(rowId(row(100))));
});
test('journal replacement during pause retains rows and reports loss before return', () => {
  const b = new ActivityBuffer(); b.accept(row(1)); b.paused = true;
  const changed = row(1, { position: { journal: runtime, runtime, offset: 1, sequence: 1 } });
  b.status({ state: 'Expired, Gap', runtimeReplaced: true }); b.accept(changed);
  assert.equal(b.rows[0].position.journal, journal); assert(b.gap && b.replaced);
  b.paused = false; b.page(page([changed]));
  assert.equal(b.rows.length, 1); assert.equal(b.rows[0].position.journal, runtime);
});
test('filtered empty pages advance the scan cursor without removing retained facts', () => {
  const b = new ActivityBuffer(); b.page(page([row(1)])); b.page(page([], { cursor: rowId(row(100)) }));
  assert.equal(b.rows.length, 1); assert.equal(b.cursor, rowId(row(100)));
});
test('route filters accept only exact safe Bead IDs and known display levels', () => {
  assert.deepEqual(selection('?bead=ns-task.1&level=warning'), { bead: 'ns-task.1', level: 'warning' });
  assert.deepEqual(selection('?bead=%3Cscript%3E&level=admin'), { bead: '', level: '' });
  assert.equal(selection('?bead=' + 'x'.repeat(129)).bead, '');
});
test('work warning and error mapping matches the server contract', () => {
  assert.equal(levelOf(row(1)), 'work');
  assert.equal(levelOf(row(1, { kind: 'PauseObserved' })), 'warning');
  assert.equal(levelOf(row(1, { kind: 'HealthChanged', reason: 'RuntimeFailed' })), 'error');
  assert.equal(levelOf(row(1, { kind: 'CleanupFailed' })), 'error');
});
test('safe evidence links bind Project, Bead and exact invocation identity', () => {
  const r = row(1, { kind: 'MergeVerified', strength: 'Verified', merge: { pullRequest: 'https://github.com/org/repo/pull/2' } }), workspace = '/projects/qa/beads/ns-task.1?tab=history';
  const invocation = `/api/telemetry/stream?runtime_id=${runtime}&slot_id=delivery-1&invocation_id=inv-1`;
  assert.deepEqual(safeDetail({ workspace, invocation, pullRequest: 'https://github.com/org/repo/pull/2' }, 'qa', r), { workspace, invocation, pullRequest: 'https://github.com/org/repo/pull/2' });
  assert.equal(safeDetail({ invocation: invocation.replace(runtime, runtime.replaceAll('-', '')) }, 'qa', r).invocation, invocation.replace(runtime, runtime.replaceAll('-', '')));
  assert.equal(safeDetail({ pullRequest: r.merge.pullRequest }, 'qa', { ...r, strength: 'Unknown' }).pullRequest, undefined);
  const enterprise = 'https://code.example.test/org/repo/pull/3';
  assert.equal(safeDetail({ pullRequest: enterprise }, 'qa', { ...r, merge: { pullRequest: enterprise } }).pullRequest, enterprise);
  for (const bad of ['javascript:alert(1)', '//evil.test/pull/1', 'https://github.com.evil.test/o/r/pull/1', 'https://github.com/o/r/pull/1?secret=a']) assert.equal(safeDetail({ pullRequest: bad }, 'qa', r).pullRequest, undefined);
  assert.equal(safeDetail({ workspace: '/projects/foreign/beads/ns-task.1', invocation: invocation.replace('inv-1', 'inv-2') }, 'qa', r).workspace, undefined);
  assert.equal(safeDetail({ invocation: invocation.replace('inv-1', 'inv-2') }, 'qa', r).invocation, undefined);
});
test('delayed times do not reorder journal rows or imply fresh historical evidence', () => {
  assert.equal(observationDelay(row(1)), 'Observed 10s after source');
  assert.equal(observationDelay(row(1, { sourceTime: undefined })), 'Source time unavailable');
  assert.equal(observationDelay(row(1, { sourceTime: '2026-11-26T00:00:00Z' })), 'Clock order uncertain');
  assert.deepEqual(mergeRows([row(2)], [row(1, { sourceTime: '2026-11-26T00:00:00Z' })]).map(r => r.position.offset), [1, 2]);
});
test('cursor parsing preserves full decimal offset precision', () => {
  assert.equal(cursorParts('1.' + journal.replaceAll('-', '') + '.9223372036854775807').offset, 9223372036854775807n);
  assert.equal(cursorParts('1.foreign.2'), undefined);
});

for (const transport of ['history', 'SSE']) for (const paused of [false, true]) {
  test(`empty ${transport} replacement keeps later matching rows; paused=${paused}`, () => {
    const b = new ActivityBuffer(); b.page(page([row(1), row(2)]));
    b.toggle(rowId(row(2))); b.paused = paused;
    const changed = offset => row(offset, { position: { journal: runtime, runtime, offset, sequence: offset } });
    const empty = page([], { cursor: rowId(changed(10)) });
    if (transport === 'history') b.page(empty);
    else b.advance(empty.cursor);
    if (paused) {
      assert.equal(b.rows[0].position.journal, journal);
      assert(b.expanded.has(rowId(row(2))));
    } else assert.equal(b.rows.length, 0);
    b.accept(changed(11));
    if (paused) {
      assert.equal(b.rows[0].position.journal, journal);
      b.paused = false; b.page(empty); b.accept(changed(11));
    }
    assert.deepEqual(b.rows.map(rowId), [rowId(changed(11))]);
    assert(b.gap);
  });
}

test('older overflow preserves all displayed rows, evidence and continuation', () => {
  const b = new ActivityBuffer();
  b.page(page(Array.from({ length: 450 }, (_, i) => row(i + 101))));
  b.toggle(rowId(row(550)));
  const continuation = b.page(page(Array.from({ length: 100 }, (_, i) => row(i + 1))), true);
  assert.equal(b.rows.length, 500);
  assert.deepEqual(b.rows.map(r => r.position.offset), Array.from({ length: 500 }, (_, i) => i + 51));
  assert(b.expanded.has(rowId(row(550))));
  assert(b.details.has(rowId(row(550))));
  assert.equal(b.cursor, rowId(row(550)));
  assert.equal(continuation, 50);
  assert.equal(b.page(page([row(50)]), true), 50);
});
