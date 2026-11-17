import assert from 'node:assert/strict';
import test from 'node:test';
import { assertSustained, isSettled } from './source-freshness-assertions.mjs';

const workspace = 'workspace:qa-example';
const current = at => ({ at, header: 'Live connection · Current', workspace: 'Current', navigationStale: false, alerts: [] });
function healthy() {
  const responses = [];
  for (let at = 0; at <= 130000; at += 2000) for (const key of ['attention', workspace]) {
    const observedAt = new Date(Math.floor(at / 60000) * 60000).toISOString();
    responses.push({ at, key, observedAt, health: 'fresh', validatedAgeMs: at % 60000, pendingAgeMs: null,
      sources: { inventory: { health: 'fresh', observedAt } },
      policy: { maxSnapshotAgeMs: 120000, maxPendingAgeMs: 5000 },
      synchronization: { journal: 'current', pending: false, delayed: false, error: null } });
  }
  return { wallSeconds: 130, responses, states: [current(0), current(130000)] };
}
function refreshing() {
  const evidence = healthy();
  evidence.states.splice(1, 0, { ...current(1000), header: 'Live connection · Refreshing', workspace: 'Refreshing' }, current(3000));
  for (const value of evidence.responses.filter(value => value.at === 2000)) {
    value.synchronization.pending = true; value.pendingAgeMs = 1000;
  }
  return evidence;
}
test('settled Current and corroborated Current → Refreshing → Current cross two real audit intervals', () => {
  assert.equal(assertSustained(healthy(), workspace).length, 3);
  assert.equal(assertSustained(refreshing(), workspace).length, 3);
});
test('settled baseline requires post-write validation of every source, clean pending and current journal', () => {
  const value = healthy().responses.at(-1);
  assert(isSettled(value, 120000));
  assert(!isSettled(value, 120001));
  assert(!isSettled({ ...value, sources: { inventory: { health: 'fresh', observedAt: new Date(0).toISOString() } } }, 120000));
  assert(!isSettled({ ...value, synchronization: { ...value.synchronization, pending: true }, pendingAgeMs: 1 }, 120000));
  assert(!isSettled({ ...value, synchronization: { ...value.synchronization, journal: 'catching-up' } }, 120000));
});
const failures = {
  stale: e => { e.states[0].workspace = 'stale'; },
  unavailable: e => { e.states[0].workspace = 'Unavailable'; },
  unknown: e => { e.states[0].workspace = 'Unknown'; },
  delayed: e => { e.states[0].header = 'Live connection · Updates delayed'; },
  disconnected: e => { e.states[0].header = 'Disconnected · Current'; },
  'source failure': e => { e.responses[0].sources.inventory.error = { code: 'source-timeout' }; },
  'envelope failure': e => { e.responses[0].health = 'stale'; },
  'read failure': e => { e.states[0].alerts = ['Observation unavailable']; },
  'journal failure': e => { e.responses[0].synchronization.error = { code: 'journal-timeout' }; },
  'expired age': e => { e.responses[0].validatedAgeMs = 120001; },
  'missing age': e => { e.responses[0].validatedAgeMs = null; },
  'missing audits': e => { for (const value of e.responses) value.observedAt = new Date(0).toISOString(); },
  'absent samples': e => { e.responses = []; },
  'absent Workspace': e => { e.responses = e.responses.filter(value => value.key === 'attention'); },
  'absent rendered samples': e => { e.states = []; },
  'short window': e => { e.wallSeconds = 129; },
  'short rendered window': e => { e.states.at(-1).at = 129000; },
  'navigation stale flash': e => { e.states[0].navigationStale = true; },
  'uncorroborated refresh': e => { e.states.splice(1, 0, { ...current(1000), workspace: 'Refreshing' }, current(3000)); },
  'stuck pending': e => { e.responses[0].synchronization.pending = true; e.responses[0].pendingAgeMs = 5001; },
};
for (const [name, mutate] of Object.entries(failures)) test(`rejects ${name}`, () => {
  const evidence = healthy(); mutate(evidence);
  assert.throws(() => assertSustained(evidence, workspace));
});
test('rejects Refreshing that exceeds allowance or never recovers even with fresh responses', () => {
  const slow = refreshing(); slow.states[2].at = 6001;
  assert.throws(() => assertSustained(slow, workspace), /pending allowance/);
  const stuck = refreshing(); stuck.states = stuck.states.slice(0, 2);
  stuck.states.push({ ...stuck.states[1], at: 130000 });
  assert.throws(() => assertSustained(stuck, workspace), /pending allowance/);
  const unfinished = refreshing(); unfinished.states[1].at = 129000;
  unfinished.responses.at(-1).synchronization.pending = true; unfinished.responses.at(-1).pendingAgeMs = 1;
  unfinished.states = [current(0), { ...unfinished.states[1], header: 'Live connection · Current' }, { ...unfinished.states[1], header: 'Live connection · Current', at: 130000 }];
  assert.throws(() => assertSustained(unfinished, workspace), /never recovered/);
});
