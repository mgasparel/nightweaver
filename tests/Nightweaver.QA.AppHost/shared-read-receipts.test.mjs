import { test } from 'node:test';
import assert from 'node:assert/strict';
import { analyzeReceipt, legacyReceipts, resourceKey, sampleReceipt } from './shared-read-receipts.mjs';

const url = key => `http://localhost/api/operations/v1/projects/qa/resources?key=${encodeURIComponent(key)}`;
const sample = (mode = 'cold') => ({ mode, startMs: 100, detailMs: 50, httpMs: 1,
  proof: { resources: [{ key: 'workspace:qa-read-000', atMs: 90 }] } });
const receipt = (key, requestAtMs, atMs) => ({ key, requestAtMs, atMs, status: 200 });
test('unrelated earlier receipt cannot set surface HTTP time', () => {
  const rows = [receipt('attention', 101, 105)];
  assert.equal(sampleReceipt(sample(), rows, 110).httpMs, null);
  rows.push(receipt('workspace:qa-read-000', 110, 120));
  const result = sampleReceipt(sample(), rows, 150);
  assert.equal(result.httpMs, 20); assert.equal(result.httpReceipt.key, 'workspace:qa-read-000');
});
test('warm retained read stays null when only unrelated responses arrive', () => {
  const result = sampleReceipt(sample('warm'), [receipt('attention', 101, 105)], 150);
  assert.equal(result.httpMs, null); assert.equal(result.retainedReceipt, true);
  assert.equal(result.httpUnavailableReason, null);
});
test('same-key request must start and respond within the sample interval', () => {
  for (const row of [receipt('workspace:qa-read-000', 99, 120),
    receipt('workspace:qa-read-000', 110, 151), receipt('workspace:qa-read-000', null, 120)]) {
    const result = sampleReceipt(sample(), [row], 150);
    assert.equal(result.httpMs, null); assert.equal(result.retainedReceipt, false);
    assert(result.httpUnavailableReason);
  }
});
test('a relevant unmatched response does not prove retained receipt', () => {
  const result = sampleReceipt(sample('warm'), [receipt('workspace:qa-read-000', 99, 120)], 150);
  assert.equal(result.retainedReceipt, false); assert.equal(result.httpMs, null);
});
test('base and full-response contracts use their validated resource keys', () => {
  const value = sample(); value.proof.resources[0].key = 'base:workspace:qa-read-000';
  assert.equal(sampleReceipt(value, [receipt('workspace:qa-read-000', 101, 110),
    receipt('base:workspace:qa-read-000', 102, 125)], 150).httpMs, 25);
});
test('legacy analysis corrects a receipt from an unrelated resource without changing raw evidence', () => {
  const browser = { requests: [{ url: url('attention'), method: 'GET', atMs: 101 },
    { url: url('workspace:qa-read-000'), method: 'GET', atMs: 110 }],
    resources: [{ key: 'attention', atMs: 105 }, { key: 'workspace:qa-read-000', atMs: 120 }] };
  const raw = sample(); const before = JSON.stringify({ raw, browser });
  assert.equal(analyzeReceipt(raw, browser).httpMs, 20);
  assert.equal(JSON.stringify({ raw, browser }), before);
});
test('legacy analysis refuses ambiguous concurrent same-key requests', () => {
  const browser = { requests: [101, 102].map(atMs => ({ url: url('workspace:qa-read-000'), method: 'GET', atMs })),
    resources: [{ key: 'workspace:qa-read-000', atMs: 120 }] };
  assert.equal(legacyReceipts(browser)[0].requestAtMs, null);
  assert.equal(analyzeReceipt(sample(), browser).httpMs, null);
});
test('missing historical request evidence is unavailable', () => {
  const result = analyzeReceipt(sample(), {});
  assert.equal(result.httpMs, null); assert(result.httpUnavailableReason);
});
test('resource URL matcher excludes another project and endpoint', () => {
  assert.equal(resourceKey(url('explorer:all::1')), 'explorer:all::1');
  assert.equal(resourceKey(url('attention').replace('/qa/', '/other/')), null);
  assert.equal(resourceKey(url('attention').replace('/resources?', '/resources-extra?')), null);
});
