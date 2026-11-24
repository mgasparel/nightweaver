import { test } from 'node:test';
import assert from 'node:assert/strict';
import { surfaceKeys } from './shared-read-keys.mjs';

test('Overview requires both independent tracker bases and runtime', () => {
  const latest = key => ['base:attention', 'base:digest:auto'].includes(key) ? { dataPresent: true } : undefined;
  assert.deepEqual(surfaceKeys('overview', latest, 'qa-read-000'), ['base:attention', 'base:digest:auto', 'factory']);
});
test('pending bases cannot stand in for baseline complete-resource evidence', () => {
  assert.deepEqual(surfaceKeys('overview', () => ({ dataPresent: false }), 'qa-read-000'), ['attention', 'digest:auto', 'factory']);
});
test('Workspace base evidence keeps the exact selected identity', () => {
  assert.deepEqual(surfaceKeys('workspace', () => ({ dataPresent: true }), 'qa-read-000'), ['base:workspace:qa-read-000']);
});
