import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL from aspire describe.');
const output = fileURLToPath(new URL('../../TestResults/invocation/', import.meta.url));
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  // Deterministic stream events cover display bounds and reset. telemetry.mjs separately
  // exercises the native endpoint, reconnect, Last-Event-ID, de-duplication and completion.
  await page.addInitScript(() => {
    window.qaStreams = [];
    window.EventSource = class extends EventTarget {
      constructor(url) { super(); this.url = url; this.closed = false; window.qaStreams.push(this); }
      close() { this.closed = true; }
    };
  });
  await page.goto(url, { waitUntil: 'networkidle' });
  const original = await page.evaluate(async () => (await fetch('/api/factory/state')).json());
  assert.ok(original.snapshot?.runtime_id);
  const busy = structuredClone(original.snapshot);
  busy.target_repository = 'H:\\Target Repository\\' + 'long-repository-name-'.repeat(8);
  const selected = busy.delivery.slots[0];
  selected.role = 'Implementer';
  selected.worktree_path = busy.target_repository + '\\worktrees\\' + 'deep-path-'.repeat(15);
  selected.branch = 'nightweaver/' + 'long-branch-'.repeat(18);
  let data = { availability: 'live', snapshot: busy };
  await page.route('**/api/factory/state', route => route.fulfill({ json: data }));
  const refresh = async () => {
    if (data.snapshot) data.snapshot.revision++;
    await page.evaluate(async () => { await refreshFactory(); });
  };
  const health = page.locator('#factory-status');
  const expectHealth = async state => assert.equal(await health.getAttribute('data-state'), state);
  const emit = async (type, data) => page.evaluate(({ type, data }) => {
    window.qaStreams.at(-1).dispatchEvent(new MessageEvent(type, { data: JSON.stringify(data) }));
  }, { type, data });
  const shot = async name => {
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth));
    await page.screenshot({ path: `${output}/${name}.png` });
  };
  await refresh();
  await expectHealth('live');
  assert.match(await health.innerText(), /● Live.*Runtime.*Observed.*Target Repository/s);
  assert.equal(await page.evaluate(() => !!document.querySelector('.factory-health-bar #factory-status') &&
    document.querySelector('#factory-status').compareDocumentPosition(document.querySelector('.bay-grid')) === Node.DOCUMENT_POSITION_FOLLOWING), true);
  await page.locator('#factory').scrollIntoViewIfNeeded();
  await shot('health-busy-1440');
  const trigger = page.locator(`[data-open-telemetry-slot="${selected.slot_id}"]`);
  const open = async () => {
    await trigger.focus();
    await page.keyboard.press('Enter');
    await page.locator('#invocation-dialog').waitFor({ state: 'visible' });
  };
  await open();
  assert.equal(new URL(await page.evaluate(() => window.qaStreams.at(-1).url), url).searchParams.get('invocation_id'), selected.invocation.invocation_id);
  await emit('status', { availability: 'live', status: 'running' });
  await emit('log', { cursor: 1, text: '\u001b[31m' + Array.from({ length: 2005 }, (_, i) => `line ${i}`).join('\n') + '\u0000' });
  const text = await page.locator('#invocation-output').textContent();
  assert.equal(text.split('\n').length, 2000);
  assert.ok(!text.includes('\u001b') && !text.includes('\u0000'));
  await emit('log', { cursor: 1, text: 'DUPLICATE' });
  assert.equal(await page.locator('#invocation-output').textContent(), text);
  await page.locator('#invocation-pause').click();
  await page.locator('#invocation-output').evaluate(node => { node.scrollTop = 50; });
  const scroll = await page.locator('#invocation-output').evaluate(node => node.scrollTop);
  await page.locator('#invocation-pause').focus();
  await refresh();
  assert.equal(await page.evaluate(() => document.activeElement.id), 'invocation-pause');
  assert.equal(await page.locator('#invocation-output').textContent(), text);
  assert.equal(await page.locator('#invocation-output').evaluate(node => node.scrollTop), scroll);
  assert.equal(await page.locator('#invocation-pause').innerText(), 'Resume follow');
  const identity = selected.invocation.invocation_id;
  selected.invocation.invocation_id = 'replacement';
  await refresh();
  assert.match(await page.locator('#invocation-status').innerText(), /Selected invocation is no longer current/);
  assert.equal(await page.evaluate(() => window.qaStreams.length), 1);
  assert.ok((await page.locator('#invocation-metadata').innerText()).includes(identity));
  assert.equal(await page.locator('#invocation-output').textContent(), text);
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    await shot(`retained-${width}`);
    await page.locator(".telemetry-actions").scrollIntoViewIfNeeded();
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    const geometry = await page.evaluate(() => {
      const dialog = document.querySelector('#invocation-dialog').getBoundingClientRect();
      const toolbar = document.querySelector('.telemetry-actions').getBoundingClientRect();
      const output = document.querySelector('#invocation-output');
      return { top: toolbar.top, bottom: toolbar.bottom, dialogTop: dialog.top, dialogBottom: dialog.bottom,
        scrollTop: output.scrollTop, scrollHeight: output.scrollHeight, clientHeight: output.clientHeight };
    });
    assert.ok(geometry.top >= geometry.dialogTop && geometry.bottom <= geometry.dialogBottom, JSON.stringify(geometry));
    await shot(`output-${width}`);
    // Native modal Tab wrapping keeps focus inside despite polling and reflow.
    for (let i = 0; i < 7; i++) {
      await page.keyboard.press('Tab');
      assert.equal(await page.evaluate(() => document.querySelector('#invocation-dialog').contains(document.activeElement)), true);
    }
  }
  await page.keyboard.press('Escape');
  assert.equal(await page.evaluate(() => document.activeElement.id), 'factory-title');
  assert.equal(await page.evaluate(() => window.qaStreams.at(-1).closed), true);
  await open();
  await emit('log', { cursor: 1, text: '界'.repeat(100000) });
  assert.ok(await page.locator('#invocation-output').evaluate(node => new TextEncoder().encode(node.textContent).length <= 256 * 1024));
  await emit('log', { cursor: 2, text: 'tail' });
  assert.equal(await page.locator('#invocation-truncation').isVisible(), true);
  await emit('reset', {});
  assert.equal(await page.locator('#invocation-output').textContent(), '');
  assert.equal(await page.locator('#invocation-truncation').isVisible(), false);
  await emit('log', { cursor: 1, text: 'after-reset' });
  await emit('end', { status: 'timed_out' });
  assert.equal(await page.locator('#invocation-status').innerText(), '✕ Stream ended: timed_out');
  assert.equal(await page.evaluate(() => window.qaStreams.at(-1).closed), true);
  await page.locator('#invocation-close').click();
  assert.equal(await page.evaluate(() => document.activeElement.dataset.openTelemetrySlot), selected.slot_id);
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    await open();
    await page.keyboard.press('Escape');
    assert.equal(await page.evaluate(() => document.activeElement.dataset.openTelemetrySlot), selected.slot_id);
  }
  // Reopen in the same task before the earlier native close event is delivered.
  await page.evaluate(() => {
    closeTelemetry();
    const slot = retainedFactorySnapshot.delivery.slots[0];
    openTelemetry(retainedFactorySnapshot.runtime_id, 'delivery', slot);
  });
  await page.waitForTimeout(100);
  assert.equal(await page.locator('#invocation-dialog').isVisible(), true);
  await emit('end', { reason: 'slot_reused' });
  assert.match(await page.locator('#invocation-status').innerText(), /^— Stream ended: slot_reused.*Agent outcome unavailable/);
  await page.keyboard.press('Escape');
  const destination = await page.locator('#beads-table-body [data-bead-workspace-link]').first().getAttribute('href');
  await open();
  await page.evaluate(hash => { location.hash = hash; }, destination);
  await page.locator('#invocation-dialog').waitFor({ state: 'hidden' });
  await page.waitForFunction(() => document.activeElement.id === 'workspace-title');
  assert.equal(await page.evaluate(() => window.qaStreams.at(-1).closed), true);
  await page.evaluate(() => { location.hash = '#factory'; });
  await page.locator('#console-deck').waitFor({ state: 'visible' });
  data = { availability: 'stale', snapshot: busy };
  await refresh();
  await expectHealth('stale');
  assert.match(await health.innerText(), /Last heartbeat.*Showing last validated snapshot/s);
  await page.locator('#factory').scrollIntoViewIfNeeded();
  await shot('health-stale');
  data = { availability: 'unavailable', reason: 'runtime stopped', snapshot: null };
  await refresh();
  await expectHealth('unavailable');
  assert.match(await health.innerText(), /No active runtime observations.*runtime stopped.*Showing last validated snapshot/s);
  await shot('health-unavailable');
  // A transport rejection must keep last validated data without claiming it is current.
  await page.unroute('**/api/factory/state');
  await page.route('**/api/factory/state', route => route.fulfill({ body: 'invalid-json' }));
  await page.evaluate(async () => { await refreshFactory(); });
  await expectHealth('disconnected');
  await shot('health-disconnected');
  await page.unroute('**/api/factory/state');
  await page.route('**/api/factory/state', route => route.fulfill({ json: data }));
  for (const pool of [busy.delivery, busy.merger]) pool.slots = pool.slots.map(s => ({ slot_id: s.slot_id, status: 'idle' }));
  data = { availability: 'live', snapshot: busy };
  await refresh();
  await expectHealth('idle');
  assert.match(await health.innerText(), /◌ Idle · Fresh runtime/);
  await shot('health-idle');
  // A structurally invalid snapshot cannot overwrite the validated runtime identity.
  data = { availability: 'live', snapshot: { runtime_id: 'malformed' } };
  await refresh();
  await expectHealth('unavailable');
  assert.ok(!(await health.innerText()).includes('malformed'));
  const fresh = await browser.newPage();
  fresh.on('pageerror', e => errors.push(e.message));
  let initial = { availability: 'unavailable', reason: 'no runtime', snapshot: null };
  await fresh.route('**/api/factory/state', route => route.fulfill({ json: initial }));
  await fresh.goto(url, { waitUntil: 'networkidle' });
  assert.equal(await fresh.locator('#factory-status').getAttribute('data-state'), 'unavailable');
  assert.match(await fresh.locator('#factory-status').innerText(), /No active runtime observations.*no runtime/s);
  assert.ok(!(await fresh.locator('#factory-status').innerText()).includes('Showing last validated'));
  initial = { availability: 'stopped', snapshot: busy };
  await fresh.evaluate(async () => { await refreshFactory(); });
  assert.equal(await fresh.locator('#factory-status').getAttribute('data-state'), 'unavailable');
  assert.match(await fresh.locator('#factory-status').innerText(), /runtime stopped.*Runtime.*Showing last validated/s);
  await fresh.close();
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ result: 'PASS', checks: 'health states, retained identity/focus/scroll, byte/line bounds, reset, keyboard/modal reflow', output }));
} finally {
  await browser.close();
}
