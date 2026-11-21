import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AttentionCommands } from '../src/attention-commands.ts';
import { actionRequest, decisionSections, safeBeadId, safeEvidenceUrl } from '../src/bead-model.ts';
const item = { beadId: 'qa.1', stage: 'stage:review', labels: ['other', 'needs-human', 'product-decision-required'], repairCycles: 3 };
const receipt = action => new Response(JSON.stringify({ beadId: item.beadId, action, resolved: action !== 'note_only', commentRecorded: true, stateChanged: action !== 'note_only' }));

test('decision context preserves attributable multiple questions/recommendations without interpreting prose as choices', () => {
  const section = (label, text) => ({ label, text, details: true, commentId: null });
  const sections = [section('Question', 'First question'), section('Recommended answer', 'First direction'),
    section('Tradeoffs', 'Source-provided consequence'), section('Question', 'Second question'),
    section('Recommended answer', 'Second direction'), section('Invocation', 'Technical context')];
  const decision = { kind: 'product-decision-required', question: 'First question', message: { state: 'supported', sections } };
  assert.deepEqual(decisionSections(decision), sections.slice(1, 5));
  for (const state of ['ordinary', 'fallback']) assert.deepEqual(decisionSections({ ...decision, message: { ...decision.message, state } }), []);
  assert.deepEqual(decisionSections({ ...decision, kind: 'needs-human' }), []);
  assert.deepEqual(decisionSections({ ...decision, message: null }), []);
});
test('Workspace admits canonical dotted identities and rejects unsafe/unbounded IDs before subscription', () => {
  for (const id of ['ns-mbd.4', 'ns-mbd.4.2', 'QA_task-1']) assert(safeBeadId(id));
  for (const id of ['../foreign', 'qa-<script>', '', 'x'.repeat(129), 'qa/1', 'qa.', 'CON', 'nul.data', 'LPT1']) assert(!safeBeadId(id));
});
test('guarded wire payloads preserve all pause labels and exact Bead; Abort remains the existing empty contract', () => {
  assert.deepEqual(actionRequest(item, 'retry', '').body, { expected: { stage: 'stage:review', pauseLabels: ['needs-human', 'product-decision-required'], repairCycles: 3 } });
  assert.deepEqual(actionRequest(item, 'note_only', 'draft').body, { instruction: 'draft', action: 'note_only' });
  assert.deepEqual(actionRequest(item, 'abort', '').body, {});
  assert.equal(actionRequest(item, 'resume', 'draft').url, '/api/beads/qa.1/decide');
  assert.equal(actionRequest({ ...item, stage: null, repairCycles: null }, 'resume', 'draft').body.expected.repairCycles, 0);
});
test('pending command prevents duplicate submits across consumers; late response remains bound to original identity', async () => {
  let release; let calls = 0; let reconciled = 0;
  const owner = new AttentionCommands(() => { calls++; return new Promise(resolve => release = resolve); }, () => reconciled++);
  const pending = owner.submit('qa', item, 'retry', '');
  await owner.submit('qa', item, 'retry', '');
  assert.equal(calls, 1); assert(owner.get('qa', item.beadId).pending);
  assert.equal(owner.get('qa', 'other').message, '');
  release(receipt('retry')); await pending;
  assert.match(owner.get('qa', item.beadId).message, /confirmed/);
  assert.equal(owner.get('qa', 'other').message, ''); assert.equal(reconciled, 1);
});
test('unknown network outcome reconciles once and requires explicit review; no replay', async () => {
  let calls = 0; let reconciled = 0;
  const owner = new AttentionCommands(async () => { calls++; throw Error('network'); }, () => reconciled++);
  await owner.submit('qa', item, 'resume', 'draft');
  assert(owner.get('qa', item.beadId).uncertain);
  await owner.submit('qa', item, 'resume', 'draft'); assert.equal(calls, 1); assert.equal(reconciled, 1);
  owner.acknowledge('qa', item.beadId); assert(!owner.get('qa', item.beadId).uncertain);
});
for (const status of [404, 409, 503]) test(`HTTP ${status} preserves identity and reports failure for explicit recovery`, async () => {
  const owner = new AttentionCommands(async () => new Response(JSON.stringify({ detail: 'fixture rejected' }), { status }), () => {});
  await owner.submit('qa', item, 'retry', '');
  assert.match(owner.get('qa', item.beadId).message, status === 404 ? /unavailable/ : status === 409 ? /changed/ : /outcome unknown/);
  assert.equal(owner.get('qa', item.beadId).uncertain, status === 503);
});
test('partial-progress failure displays recorded comment and uncertain state without replay', async () => {
  const owner = new AttentionCommands(async () => new Response(JSON.stringify({ detail: 'failed', commentRecorded: true, stateChanged: null }), { status: 409 }), () => {});
  await owner.submit('qa', item, 'resume', 'draft');
  assert(owner.get('qa', item.beadId).uncertain); assert.match(owner.get('qa', item.beadId).message, /Comment: recorded. Workflow: unverified/);
});
test('an invalid or foreign success receipt is uncertain', async () => {
  const owner = new AttentionCommands(async () => new Response(JSON.stringify({ beadId: 'other', action: 'resume' })), () => {});
  await owner.submit('qa', item, 'resume', 'draft'); assert(owner.get('qa', item.beadId).uncertain);
});
test('deadline ends the pending UI even if adapter ignores abort; late receipt cannot overwrite review or replacement', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let release;
  const owner = new AttentionCommands(() => new Promise(resolve => release = resolve), () => {});
  const pending = owner.submit('qa', item, 'retry', '');
  t.mock.timers.tick(10000); await pending;
  assert(owner.get('qa', item.beadId).uncertain);
  owner.acknowledge('qa', item.beadId);
  release(receipt('retry')); await Promise.resolve(); await Promise.resolve();
  assert.equal(owner.get('qa', item.beadId).message, '');
});
test('receipt decoding also has a deadline; a stalled response body is uncertain', async t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const owner = new AttentionCommands(async () => ({ ok: true, json: () => new Promise(() => {}) }), () => {});
  const pending = owner.submit('qa', item, 'resume', 'draft');
  await Promise.resolve(); await Promise.resolve();
  t.mock.timers.tick(10000); await pending;
  assert(owner.get('qa', item.beadId).uncertain);
});

test('evidence accepts explicit HTTP(S) and rejects credentials, whitespace and unsafe references', () => {
  for (const value of ['https://example.com/pull/53', 'HTTP://example.com/evidence?q=1#checks']) assert(safeEvidenceUrl(value), value);
  for (const value of ['javascript:alert(1)', 'file:///H:/private', '//example.com/pr', '/api/private', 'https://user:secret@example.com/pr', 'https://user@example.com', ' https://example.com', 'https://example.com/a b', 'https://example.com/\nsecret', 'https://']) assert(!safeEvidenceUrl(value), value);
});
