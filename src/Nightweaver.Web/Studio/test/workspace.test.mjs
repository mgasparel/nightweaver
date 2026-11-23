import { test } from 'node:test';
import assert from 'node:assert/strict';
import { sectionView, currentAttention, baseIsCurrent, baseIsMissing } from '../src/workspace-model.ts';

const workflow = { stage: 'stage:review', pauseLabels: ['needs-human'], repairCycles: 2 };
const base = { id: 'qa.1', updatedAt: '2026-11-23T10:00:00Z', title: 'Current title', status: 'open',
  labels: ['needs-human'], stage: workflow.stage, repairCycles: 2,
  actions: { expectedWorkflow: workflow, canRetry: false, canAbort: true, canDecide: false } };
const snapshot = (value, changes = {}) => ({ synchronized: true, stale: false, observation: { health: 'fresh',
  data: { schema: 'nightweaver-section-v1', beadId: base.id, name: 'history', state: 'available',
    baseIdentity: { inventoryRevision: '2', updatedAt: base.updatedAt, expectedWorkflow: workflow }, value }, ...changes } });

test('held sections stay loading and never become confirmed empty', () => {
  assert.equal(sectionView({}, base, 'history').state, 'loading');
  assert.equal(sectionView({}, base, 'history').value, null);
  const empty = snapshot({ comments: [], attention: null }); empty.observation.data.state = 'empty';
  assert.equal(sectionView(empty, base, 'history').state, 'empty');
});

test('failed refresh retains readable history with historical identity and no action context', () => {
  const value = { comments: [{ id: '7' }], attention: { beadId: base.id } };
  const failed = snapshot(value, { health: 'stale', error: { code: 'source-failed' } });
  const view = sectionView(failed, base, 'history');
  assert.equal(view.state, 'stale'); assert.equal(view.value, value);
  assert.equal(currentAttention(base, view), null);
  assert.equal(sectionView({ observation: { error: { code: 'source-failed' } } }, base, 'history').state, 'failed');
  const noIdentity = snapshot(null); noIdentity.observation.data.baseIdentity = null; noIdentity.observation.data.state = 'failed';
  assert.equal(sectionView(noIdentity, base, 'history').state, 'failed');
});

test('confirmed absence requires fresh inventory proof rather than aggregate fresh health', () => {
  const missing = { synchronized: true, stale: false, observation: { health: 'unavailable',
    error: { code: 'resource-missing' }, sources: { inventory: { health: 'fresh' } },
    synchronization: { pending: false }, data: { schema: 'nightweaver-base-v1', completeness: 'complete',
      bead: null, inventoryRevision: '3', absence: { id: base.id, inventoryRevision: '3' } } } };
  assert.equal(baseIsMissing(missing, base.id), true);
  assert.equal(baseIsMissing(missing, 'qa.2'), false);
  assert.equal(baseIsMissing({ ...missing, stale: true }, base.id), false);
  missing.observation.synchronization.pending = true;
  assert.equal(baseIsMissing(missing, base.id), false);
  missing.observation.synchronization.pending = false;
  missing.observation.sources.inventory.health = 'stale';
  assert.equal(baseIsMissing(missing, base.id), false);
});

test('late detail cannot replace newer workflow, identity or timestamp', () => {
  const sample = snapshot({ attention: { beadId: base.id } });
  for (const changed of [{ ...base, updatedAt: '2026-11-23T11:00:00Z' },
    { ...base, actions: { ...base.actions, expectedWorkflow: { ...workflow, stage: 'stage:merge' } } }]) {
    const view = sectionView(sample, changed, 'history');
    assert.equal(view.state, 'stale'); assert.equal(currentAttention(changed, view), null);
  }
  assert.equal(sectionView(sample, { ...base, id: 'qa.2' }, 'history').value, null);
  assert.equal(sectionView(sample, base, 'text').value, null);
});

test('current base controls action availability despite retained history', () => {
  const view = sectionView(snapshot({ attention: { beadId: base.id, canRetry: true } }), base, 'history');
  assert.equal(currentAttention(base, view).canRetry, false);
  assert.equal(currentAttention(base, view).title, 'Current title');
  assert.equal(currentAttention({ ...base, status: 'closed' }, view), null);
  assert.equal(currentAttention({ ...base, actions: { ...base.actions, expectedWorkflow: { ...workflow, pauseLabels: [] } } }, view), null);
  assert.equal(baseIsCurrent({ synchronized: false, observation: { health: 'fresh' } }), false);
});
