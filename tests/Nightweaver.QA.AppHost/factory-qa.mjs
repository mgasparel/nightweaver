import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL from aspire describe.');
const output = process.env.NIGHTWEAVER_QA_OUTPUT ?? fileURLToPath(new URL('../../TestResults/factory/', import.meta.url));
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error' && !m.text().includes('503')) errors.push(m.text()); });
  await page.goto(url, { waitUntil: 'networkidle' });
  const original = await page.evaluate(async () => (await fetch('/api/factory/state')).json());
  assert.ok(original.snapshot?.runtime_id);
  const busy = structuredClone(original.snapshot);
  const slot = (id, bead, status = 'running') => ({ slot_id: id, bead_id: bead, status,
    stage: 'stage:implement', role: 'Implementer', repair_cycles: 1, max_repair_cycles: 3,
    branch: `nightweaver/${bead}`, worktree_path: `H:\\Nightweaver\\worktrees\\${bead}`,
    invocation: { bead_id: bead, invocation_id: `invocation-${id}`, cli: 'codex', configured_model: 'configured-only',
      status: status === 'running' ? 'running' : 'failed', started_at: '2026-11-13T03:00:00Z', deadline_at: '2026-11-13T04:00:00Z' },
    diff: { status: 'available', added: 23, deleted: 7, binary: 2, untracked: 4 } });
  const idle = id => ({ slot_id: id, status: 'idle' });
  busy.delivery = { capacity: 3, slots: [slot('delivery-1', 'ns-build'), slot('delivery-2', 'ns-review'), idle('delivery-3')] };
  busy.delivery.slots[1].role = 'Reviewer';
  busy.delivery.slots[1].stage = 'stage:review';
  busy.merger = { capacity: 1, slots: [slot('merger-1', 'ns-merge')] };
  busy.merger.slots[0].role = 'Merger';
  busy.merger.slots[0].stage = 'stage:merge';
  let data = { availability: 'live', snapshot: busy };
  let fail = false;
  await page.route('**/api/factory/state', route => fail
    ? route.fulfill({ status: 503, json: {} }) : route.fulfill({ json: data }));
  const refresh = async () => {
    if (data.snapshot) data.snapshot.revision++;
    await page.evaluate(async () => { await refreshFactory(); });
  };
  const shot = async name => {
    await page.locator('#factory').scrollIntoViewIfNeeded();
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth));
    await page.locator("#factory").screenshot({ path: `${output}/${name}.png` });
    await page.screenshot({ path: `${output}/${name}-viewport.png` });
  };
  let streamIdentity;
  await page.route('**/api/telemetry/stream**', async route => {
    streamIdentity = new URL(route.request().url()).searchParams;
    await route.fulfill({ contentType: 'text/event-stream', body: 'event: log\ndata: {"cursor":1,"text":"Retained output for ' + streamIdentity.get('invocation_id') + '"}\n\nevent: end\ndata: {"status":"completed"}\n\n' });
  });
  const inspect = async (card, label, expected) => {
    await card.getByRole('button', { name: label, exact: true }).click();
    await page.getByText('✓ Stream ended: completed', { exact: true }).waitFor();
    for (const [key, value] of Object.entries(expected)) assert.equal(streamIdentity.get(key), value);
    assert.equal(await page.locator('#invocation-output').innerText(), 'Retained output for ' + expected.invocation_id);
    await page.keyboard.press('Escape');
  };
  await refresh();
  assert.match(await page.locator('#delivery-pool-status').innerText(), /2 of 3 slots active · 1 idle/);
  assert.equal(await page.locator('.slot-diagnostics[open]').count(), 0);
  const first = page.locator('[data-slot-id="delivery-1"]');
  assert.equal(await first.locator('[data-bead-workspace-link]').getAttribute('href'), '#bead/ns-build');
  assert.match(await first.innerText(), /Implementer · stage:implement/);
  assert.match(await first.innerText(), /Started:.*Deadline:/s);
  assert.match(await first.innerText(), /Repairs: 1 \/ 3/);
  assert.match(await first.innerText(), /\+23 \/ −7/);
  assert.ok(!(await first.innerText()).includes('configured-only'));
  const idleRow = page.locator('.factory-slot-idle');
  assert.match(await idleRow.innerText(), /Available for admission/);
  assert.equal(await idleRow.locator('details, button').count(), 0);
  assert.ok(!(await idleRow.innerText()).includes('Unknown'));
  await inspect(first, 'Open live invocation', { runtime_id: busy.runtime_id, slot_id: 'delivery-1', invocation_id: 'invocation-delivery-1' });
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    await shot(`busy-${width}`);
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await first.locator('summary').click();
  assert.match(await first.locator('dl').innerText(), /Actual model\s+Unavailable/);
  assert.match(await first.locator('dl').innerText(), /Binary: 2 · Untracked: 4/);
  await first.locator('summary').focus();
  busy.delivery.slots[0].diff.added = 24;
  // Allow an actual changed two-second poll to update the DOM, preserving disclosure and focus.
  busy.revision++;
  await page.waitForFunction(() => document.querySelector('[data-slot-id="delivery-1"] .slot-diff').textContent.includes('+24'));
  assert.equal(await first.locator('details').getAttribute('open'), '');
  assert.equal(await page.evaluate(() => document.activeElement.matches('[data-slot-id="delivery-1"] summary')), true);
  await first.locator('button').focus();
  await refresh();
  assert.equal(await page.evaluate(() => document.activeElement.dataset.openTelemetrySlot), 'delivery-1');
  // Never transfer focus to a replacement invocation's action.
  busy.delivery.slots[0].invocation.invocation_id = 'replacement';
  await refresh();
  assert.equal(await page.evaluate(() => document.activeElement.id), 'factory-title');
  await first.locator('summary').focus();
  busy.runtime_id = 'replacement-runtime';
  await refresh();
  assert.equal(await page.locator('.slot-diagnostics[open]').count(), 0);
  assert.equal(await page.evaluate(() => document.activeElement.id), 'factory-title');
  for (const [status, label] of [['failed', '✕ Failed'], ['launch_failed', '✕ Launch Failed'],
    ['launch_denied', '✕ Launch Denied'], ['preparation_failed', '✕ Preparation Failed']]) {
    busy.delivery.slots[0].invocation.status = status;
    await refresh();
    assert.ok((await first.locator('.slot-state').innerText()).includes(label));
  }
  busy.delivery.slots[0] = slot('delivery-1', 'ns-prepare', 'preparing');
  busy.delivery.slots[0].invocation = null;
  busy.delivery.slots[1] = slot('delivery-2', 'ns-failure', 'settling');
  busy.delivery.slots[1].invocation.status = 'timed_out';
  busy.merger.slots[0] = slot('merger-1', 'ns-paused', 'paused');
  await refresh();
  assert.match(await first.innerText(), /◐ Preparing/);
  assert.match(await first.innerText(), /Started: Unavailable · Deadline: Unavailable/);
  assert.equal(await first.locator('button').count(), 0);
  assert.match(await page.locator('[data-slot-id="delivery-2"]').innerText(), /✕ Timed Out/);
  assert.equal(await page.getByRole('button', { name: 'Open retained invocation', exact: true }).count(), 2);
  await inspect(page.locator('[data-slot-id="delivery-2"]'), 'Open retained invocation',
    { runtime_id: busy.runtime_id, slot_id: 'delivery-2', invocation_id: 'invocation-delivery-2' });
  await shot('preparing-failed-paused');
  data.availability = 'stale';
  await refresh();
  assert.match(await page.locator('#delivery-pool-status').innerText(), /^Last observed:/);
  assert.match(await idleRow.innerText(), /Last observed idle/);
  assert.ok(!(await idleRow.innerText()).includes('Available for admission'));
  await page.evaluate(async () => { await refreshFactory(); });
  assert.match(await page.locator('#factory-status').innerText(), /^⚠ Stale/);
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    await shot(`stale-${width}`);
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  data = { availability: 'unavailable', snapshot: null };
  await refresh();
  assert.match(await page.locator('#delivery-pool-status').innerText(), /^Last observed:/);
  fail = true;
  await refresh();
  assert.match(await page.locator('#factory-status').innerText(), /Disconnected/);
  assert.ok(!(await idleRow.innerText()).includes('Available for admission'));
  await shot('disconnected-desktop');
  fail = false;
  data = { availability: 'live', snapshot: busy };
  busy.delivery.slots = [idle('delivery-1'), idle('delivery-2'), idle('delivery-3')];
  busy.merger.slots = [idle('merger-1')];
  await refresh();
  assert.match(await page.locator('#delivery-pool-status').innerText(), /^0 of 3 slots active · 3 idle/);
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    await shot(`idle-${width}`);
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  busy.merger = { capacity: 0, slots: [] };
  await refresh();
  assert.match(await page.locator('#merger-pool-status').innerText(), /Pool disabled/);
  // Long observed identities and paths remain accessible at mobile and effective 200% zoom.
  busy.delivery.slots[0] = slot('delivery-1', 'long-identity-'.repeat(16));
  busy.delivery.slots[0].worktree_path = 'H:\\deep\\' + 'long-directory\\'.repeat(30);
  busy.delivery.slots[0].branch = 'nightweaver/' + 'long-branch'.repeat(25);
  await refresh();
  await first.locator('summary').click();
  for (const width of [375, 720, 1440]) {
    await page.setViewportSize({ width, height: 900 });
    await shot(`long-diagnostics-${width}`);
    assert.ok(await first.locator('button').isVisible());
  }
  // Release clears current task metadata while preserving the exact invocation.
  await page.setViewportSize({ width: 1440, height: 900 });
  for (const outcome of ['completed', 'failed']) {
    busy.delivery.slots = [slot('delivery-1', 'ns-retained-' + outcome), idle('delivery-2'), idle('delivery-3')];
    await refresh();
    await first.locator('button').focus();
    const invocation = { ...busy.delivery.slots[0].invocation, status: outcome };
    busy.delivery.slots[0] = { slot_id: 'delivery-1', status: 'idle', bead_id: null, role: null,
      stage: null, branch: null, worktree_path: null, repair_cycles: null, max_repair_cycles: null,
      diff: null, invocation };
    busy.revision++;
    await page.waitForFunction(() => document.querySelector('[data-slot-id="delivery-1"]').dataset.status === 'idle');
    assert.equal(await page.evaluate(() => document.activeElement.dataset.invocationId), invocation.invocation_id);
    for (const initialLoad of [false, true]) {
      if (initialLoad) {
        await page.reload({ waitUntil: 'networkidle' });
        await first.getByRole('button', { name: 'Open retained invocation', exact: true }).waitFor();
      }
      assert.match(await page.locator('#delivery-pool-status').innerText(), /^0 of 3 slots active · 3 idle/);
      assert.equal(await first.locator('details, dl, .slot-timing, .slot-metrics').count(), 0);
      assert.doesNotMatch(await first.innerText(), /Unknown|Unavailable/);
      await inspect(first, 'Open retained invocation', { runtime_id: busy.runtime_id,
        slot_id: 'delivery-1', invocation_id: invocation.invocation_id });
      assert.ok((await page.locator('#invocation-title').innerText()).includes(invocation.bead_id));
    }
  }
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    await shot(`idle-retained-${width}`);
    await inspect(first, 'Open retained invocation', { runtime_id: busy.runtime_id,
      slot_id: 'delivery-1', invocation_id: busy.delivery.slots[0].invocation.invocation_id });
  }
  assert.deepEqual(errors, []);
  console.log('Factory QA passed: summaries, occupancy, idle/disabled, unavailable metadata, runtime-scoped disclosure, changed polling/focus, replacement identity, preparing/failed/paused, stale/no-snapshot/disconnected retention, released completed/failed invocation identity/output on changed polls and initial loads, long content at 1440/720/375.');
} finally {
  await browser.close();
}
