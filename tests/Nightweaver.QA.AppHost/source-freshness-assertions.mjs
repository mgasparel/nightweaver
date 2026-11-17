import assert from 'node:assert/strict';

export function assertHealthyResponse(value) {
  assert.equal(value.health, 'fresh', `${value.key}: envelope health`);
  assert(!value.error && !value.synchronization?.error, `${value.key}: source/read error`);
  assert(Number.isFinite(value.validatedAgeMs) && value.validatedAgeMs >= 0
    && value.validatedAgeMs <= 120000, `${value.key}: accepted source age`);
  assert.equal(value.policy?.maxSnapshotAgeMs, 120000);
  assert.equal(value.policy?.maxPendingAgeMs, 5000);
  assert.equal(value.synchronization?.journal, 'current', `${value.key}: journal catch-up`);
  assert.equal(value.synchronization?.delayed, false);
  assert(Object.keys(value.sources ?? {}).length, `${value.key}: source evidence missing`);
  assert(Object.values(value.sources).every(source => source.health === 'fresh' && !source.error && source.observedAt),
    `${value.key}: authoritative source failure`);
  if (value.synchronization.pending) {
    assert(Number.isFinite(value.pendingAgeMs) && value.pendingAgeMs >= 0
      && value.pendingAgeMs <= value.policy.maxPendingAgeMs, `${value.key}: pending allowance`);
  } else assert.equal(value.pendingAgeMs, null);
}

export function isSettled(value, after) {
  try { assertHealthyResponse(value); } catch { return false; }
  return !value.synchronization.pending && Date.parse(value.observedAt) >= after
    && Object.values(value.sources).every(source => Date.parse(source.observedAt) >= after);
}

export function assertSustained({ wallSeconds, responses, states }, workspaceKey) {
  assert(wallSeconds >= 130, 'Cross two real audits and the 120-second age boundary');
  const keys = ['attention', workspaceKey];
  const tracker = responses.filter(value => keys.includes(value.key));
  assert(tracker.length >= 100, 'Observe sustained HTTP receipt ageing');
  tracker.forEach(assertHealthyResponse);
  for (const key of keys) assert(tracker.some(value => value.key === key), `Missing ${key} samples`);
  const attentionValidations = [...new Set(tracker.filter(value => value.key === 'attention').map(value => value.observedAt))];
  assert(attentionValidations.length >= 3, 'Bootstrap and two real authoritative audits must validate Attention');
  assert(states.length >= 2, 'Missing rendered samples');
  assert(states.at(-1).at - states[0].at >= 130000, 'Rendered samples must span the observation window');
  for (const state of states) {
    assert.equal(state.navigationStale, false, 'Attention navigation must not flash stale');
    assert.deepEqual(state.alerts, [], 'Visible source/read failure');
    assert(['Live connection · Current', 'Live connection · Refreshing'].includes(state.header), `Unexpected connection/data state: ${state.header}`);
    assert(['Current', 'Refreshing'].includes(state.workspace), `Unexpected Workspace state: ${state.workspace}`);
  }
  for (const [key, select] of [['attention', state => state.header.split(' · ')[1]], [workspaceKey, state => state.workspace]]) {
    let started;
    for (const state of states) {
      if (select(state) === 'Refreshing' && started === undefined) {
        started = state.at;
        // Resource receipts occur every two seconds. Require actual pending
        // metadata around the transition, never just an absence of stale badges.
        assert(tracker.some(value => value.key === key && Math.abs(value.at - started) <= 2100
          && value.synchronization.pending), `${key}: uncorroborated Refreshing`);
      }
      if (started !== undefined) {
        assert(state.at - started <= 5000, `${key}: Refreshing exceeded pending allowance`);
        if (select(state) === 'Current') started = undefined;
      }
    }
    assert.equal(started, undefined, `${key}: Refreshing never recovered`);
  }
  return attentionValidations;
}
