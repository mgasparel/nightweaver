import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';

export async function studioDeliveryQA(mode) {
  const base = process.env.NIGHTWEAVER_QA_URL;
  assert(base, 'Set discovered NIGHTWEAVER_QA_URL');
  const output = process.env.NIGHTWEAVER_QA_OUTPUT || `TestResults/studio-${mode}`;
  await mkdir(output, { recursive: true });
  const browser = await chromium.launch({ headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      const Native = window.EventSource;
      window.logs = [];
      window.EventSource = class extends EventTarget {
        constructor(url) { super(); if (!String(url).includes('/api/telemetry/stream')) return new Native(url); this.url = url; this.closed = false; window.logs.push(this); }
        close() { this.closed = true; }
      };
    });
    const original = await (await page.request.get(base + '/api/factory/state')).json();
    assert(original.snapshot?.runtime_id);
    let data = structuredClone(original);
    const slot = data.snapshot.delivery.slots[0];
    slot.stage = 'stage:implement';
    slot.status = 'running';
    slot.invocation.status = 'running';
    slot.branch = 'nightweaver/' + 'long-branch-'.repeat(18);
    slot.worktree_path = 'H:\\Target Repository\\' + 'long-path-'.repeat(30);
    slot.invocation.configured_model = 'configured-only';
    slot.diff = { status: 'available', added: 23, deleted: 7, binary: 2, untracked: 4 };
    let health = 'fresh';
    await page.route('**/api/operations/v1/projects/qa/resources?**', async route => {
      if (new URL(route.request().url()).searchParams.get('key') !== 'factory') return route.continue();
      const response = await route.fetch(); const value = await response.json();
      value.data = data; value.health = health; value.error = health === 'fresh' ? null : { code: 'fixture', message: 'Runtime observation failed', retryable: true };
      value.sources.runtime.health = health;
      await route.fulfill({ response, json: value });
    });
        await page.goto(base + '/projects/qa/overview');
    await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'live');
    const deliveryPool = page.getByRole('region', { name: 'Delivery pool', exact: true });
    const occupied = data.snapshot.delivery.slots.filter(row => row.status !== 'idle').length;
    assert.match(await page.locator('#factory-status').innerText(), new RegExp(`${occupied} occupied Delivery bays`));
    assert.equal(await page.locator('.delivery-bay').count(), Math.min(occupied, 3));
    assert.equal(await page.locator('[data-slot-id="delivery-1"] .delivery-progress-caption').innerText(), 'Implement → Review\nStage 1 of 4');
    assert.equal(await page.getByRole('region', { name: 'Merger pool', exact: true }).count(), 0);
    assert.equal(await page.getByRole('region', { name: 'Planning pool', exact: true }).count(), 0);
    assert.match(await page.locator('.delivery-preview-footer').innerText(), /Planning.*occupied/);
    assert.match(await page.locator('.delivery-preview-footer').innerText(), /Singleton Merger.*occupied/);
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 900 });
      assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: `${output}/overview-${width}.png`, fullPage: true });
    }
    await page.goto(base + '/projects/qa/delivery');
    const healthNode = page.locator('#factory-status');
    await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'live');
    const first = page.locator('[data-slot-id="delivery-1"]');
    assert.match(await first.innerText(), /qa-live-1/);
    assert.match(await first.innerText(), /Title unavailable/);
    assert.match(await first.innerText(), /Type unavailable/);
    const beadLink = first.getByRole('link', { name: slot.bead_id, exact: true });
    assert.equal(await beadLink.getAttribute('href'), `/projects/qa/beads/${slot.bead_id}`);
    await beadLink.focus();
    assert(await beadLink.evaluate(node => node === document.activeElement && !node.closest('button')));
    await beadLink.press('Enter');
    await page.waitForURL(base + `/projects/qa/beads/${slot.bead_id}`);
    await page.goto(base + '/projects/qa/delivery');
    await first.waitFor();
    assert.equal(await first.locator('details[open]').count(), 0);
    await first.locator('summary').click(); await first.locator('summary').focus();
    assert.match(await first.innerText(), /Configured model\nconfigured-only/);
    assert.match(await first.innerText(), /Observed model\nUnavailable/);
    await page.waitForTimeout(2400);
    assert(await first.locator('summary').evaluate(node => node === document.activeElement));
    assert(await first.locator('details').evaluate(node => node.open));
    const capture = async name => {
      for (const width of [1440, 375, 720]) {
        await page.setViewportSize({ width, height: 900 });
        assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
        for (const track of await page.locator('.delivery-progress').all()) {
          assert.equal(await track.locator('.delivery-segments').getAttribute('aria-hidden'), 'true');
          assert(await track.evaluate(node => node.scrollWidth <= node.clientWidth));
          assert(await track.locator('.delivery-segments').evaluate(node => {
            const boxes = [...node.children].map(segment => segment.getBoundingClientRect());
            return boxes.length === 4 && boxes.every(box => box.height === 5 && Math.abs(box.width - boxes[0].width) < 1) && Math.abs(boxes[1].left - boxes[0].right - 6) < 1;
          }));
        }
        await page.screenshot({ path: `${output}/${name}-${width}.png`, fullPage: true });
        await page.screenshot({ path: `${output}/${name}-${width}-viewport.png` });
      }
    };
    await capture('busy');
    for (const [stage, caption] of [['review', 'Review → QA'], ['qa', 'QA → Merge'], ['merge', 'Merge · final stage'], ['implement', 'Implement → Review']]) {
      slot.stage = `stage:${stage}`;
      await first.locator('.delivery-progress-caption').getByText(caption, { exact: true }).waitFor();
    }
    slot.stage = 'stage:review'; slot.status = 'paused';
    await first.getByText('Paused at Review', { exact: true }).waitFor();
    assert.equal(await first.locator('.delivery-progress').getAttribute('data-neutral'), 'true');
    const originalBead = slot.bead_id;
    slot.bead_id = 'qa-' + 'long-bead-identity-'.repeat(14);
    await first.getByRole('link', { name: slot.bead_id, exact: true }).waitFor();
    await capture('paused-long-identity');
    slot.stage = 'stage:future';
    await first.getByText('Stage unavailable', { exact: true }).waitFor();
    assert.equal(await first.locator('.delivery-stage-position').count(), 0);
    assert.equal(await first.locator('[data-position="current"]').count(), 0);
    await capture('unknown');
    slot.bead_id = originalBead; slot.stage = 'stage:implement'; slot.status = 'running';
    await first.getByText('Implement → Review', { exact: true }).waitFor();
    await first.getByRole('button').click();
    const emit = async (type, data) => { await page.evaluate(({ type, data }) => window.logs.findLast(s => !s.closed).dispatchEvent(new MessageEvent(type, { data: JSON.stringify(data) })), { type, data }); await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))); };
    const logCount = await page.evaluate(() => window.logs.length);
    assert.equal(new URL(await page.evaluate(() => window.logs.at(-1).url), base).searchParams.get('invocation_id'), slot.invocation.invocation_id);
    await emit('log', { cursor: 1, text: '\x1b[31m' + Array.from({ length: 2005 }, (_, i) => 'line ' + i).join('\n') + '\0<script>literal</script>' });
    const text = await page.locator('#invocation-output').textContent();
    assert.equal(text.split('\n').length, 2000); assert(!text.includes('\x1b') && !text.includes('\0'));
    assert.equal(await page.locator('#invocation-output script').count(), 0);
    await emit('log', { cursor: 1, text: 'duplicate' }); assert.equal(await page.locator('#invocation-output').textContent(), text);
    await page.locator('#invocation-pause').click();
    await page.locator('#invocation-output').evaluate(node => { node.scrollTop = 50; });
    await page.locator('#invocation-pause').focus();
    const originalId = slot.invocation.invocation_id;
    slot.invocation.invocation_id = 'new-occupant';
    await page.getByText('Selected invocation is no longer current.', { exact: false }).waitFor();
    data.snapshot.runtime_id = 'runtime-replaced-during-inspection';
    await page.waitForTimeout(2400);
    assert.equal(await page.evaluate(() => window.logs.length), logCount);
    assert((await page.locator('#invocation-metadata').textContent()).includes(originalId));
    assert.equal(await page.locator('#invocation-output').textContent(), text);
    assert.equal(await page.locator('#invocation-pause').innerText(), 'Resume follow');
    assert.equal(await page.evaluate(() => document.activeElement.id), 'invocation-pause');
    assert.equal(await page.locator('#invocation-output').evaluate(node => node.scrollTop), 50);
    await page.locator('#invocation-dialog summary').click();
    await capture('retained');
    await page.locator('#invocation-output').scrollIntoViewIfNeeded();
    await capture('output');
    for (let n = 0; n < 8; n++) { await page.keyboard.press('Tab'); assert(await page.locator('#invocation-dialog').evaluate(node => node.contains(document.activeElement)), JSON.stringify(await page.evaluate(() => ({ focus: document.activeElement.outerHTML.slice(0,300), open: document.querySelector('#invocation-dialog').open, modal: document.querySelector('#invocation-dialog').matches(':modal') })))); }
    await emit('error'); await page.getByText('Disconnected · Reconnecting… Agent execution may still be running.', { exact: true }).waitFor();
    await emit('log', { cursor: 2, text: '界'.repeat(100000) });
    assert(await page.locator('#invocation-output').evaluate(node => new TextEncoder().encode(node.textContent).length <= 256 * 1024));
    await emit('reset', { cursor: 0 }); assert.equal(await page.locator('#invocation-output').textContent(), '');
    await emit('log', { cursor: 1, text: 'after reset' });
    await emit('end', { status: 'failed' }); await page.getByText('Stream ended: failed', { exact: true }).waitFor();
    assert(await page.evaluate(() => window.logs.at(-1).closed));
    await page.keyboard.press('Escape'); await page.waitForFunction(() => document.activeElement.id === 'factory-title');
    slot.invocation.status = 'failed'; slot.status = 'settling';
    await page.getByText('Invocation: failed', { exact: true }).waitFor();
    data.snapshot.runtime_id = 'replacement-runtime';
    await first.locator('summary').focus();
    await page.waitForFunction(() => document.activeElement.id === 'factory-title');
    await first.getByRole('button').click(); await page.keyboard.press('Escape');
    await page.waitForFunction(() => document.activeElement.matches('[data-open-telemetry-slot="delivery-1"]'));
    data.availability = 'stale'; health = 'stale';
    await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'stale');
    await first.getByText('Last observed · stale · Observed at Implement', { exact: true }).waitFor();
    assert.equal(await first.locator('.delivery-progress').getAttribute('data-neutral'), 'true');
    await capture('stale');
    data.availability = 'stopped'; health = 'fresh';
    await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'stopped');
    data.availability = 'live';
    for (const pool of [data.snapshot.planning, data.snapshot.delivery, data.snapshot.merger]) for (const row of pool?.slots || []) row.status = 'idle';
    await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'idle');
    assert(await first.getByRole('button', { name: 'Open retained invocation' }).isVisible());
    await capture('idle-retained');
    data = { availability: 'unavailable', reason: 'missing', snapshot: null }; health = 'unavailable';
    await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'unavailable');
    assert.match(await healthNode.innerText(), /unavailable/);
    await capture('unavailable');
    assert.deepEqual(errors, []);
    await writeFile(`${output}/results.json`, JSON.stringify({ passed: true, mode, checks: ['shared operational interleaving', 'exact retained identity', 'runtime replacement', 'line/UTF-8 bounds', 'reset', 'failed terminal cessation', 'disconnect', 'follow/focus', 'live/idle/stale/stopped/unavailable', '1440/375/720 geometry'] }, null, 2));
    console.log('Studio ' + mode + ' QA passed');
  } finally { await browser.close(); }
}
