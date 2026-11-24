import { test } from 'node:test';
import assert from 'node:assert/strict';
import { sectionReport } from './shared-read-section-report.mjs';
const sample = () => ({ surface: 'workspace', startMs: 100 });
const row = (state, atMs = 120) => ({ key: 'section:qa-read-000:history', atMs, health: 'fresh',
  data: { state, baseIdentity: { inventoryRevision: '1' } } });
test('base success cannot supply section completion', () => {
  const report = sectionReport(sample(), { resources: [{ key: 'base:workspace:qa-read-000', atMs: 110,
    health: 'fresh', data: { bead: { id: 'qa-read-000' } } }] }, 'qa-read-000', 200);
  assert(report.every(value => value.completedMs === null));
});
test('a successful confirmed history receipt has its own completion time', () => {
  const report = sectionReport(sample(), { resources: [row('available')] }, 'qa-read-000', 200);
  assert.equal(report.find(value => value.key.endsWith(':history')).completedMs, 20);
});
test('failed and unconfirmed receipts cannot supply completion', () => {
  for (const value of [row('failed'), { ...row('available'), health: 'stale' },
    { ...row('available'), data: { state: 'available' } }, row('available', 201)]) {
    assert(sectionReport(sample(), { resources: [value] }, 'qa-read-000', 200).every(value => value.completedMs === null));
  }
});
test('retained state has no invented new completion time', () => {
  const value = sample(); value.sections = { 'section:qa-read-000:history': row('available', 90) };
  const history = sectionReport(value, { resources: [] }, 'qa-read-000', 200).find(value => value.key.endsWith(':history'));
  assert.equal(history.completedMs, null); assert.equal(history.retainedSnapshot.atMs, 90);
});
test('merge membership cannot certify all merge entries', () => {
  const value = sectionReport({ surface: 'digest', startMs: 100 }, { resources: [
    { key: 'digest-merges:24h', atMs: 120, health: 'fresh', data: { sections: {
      'qa-read-034': { state: 'loading', source: { health: 'unavailable' } },
      'qa-read-035': { state: 'failed', source: { health: 'stale' } }
    } } }
  ] }, 'qa-read-000', 200)[0];
  assert.equal(value.completedMs, null);
  assert.equal(value.mergeStates['qa-read-034'].state, 'loading');
  assert.equal(value.mergeStates['qa-read-035'].state, 'failed');
});
