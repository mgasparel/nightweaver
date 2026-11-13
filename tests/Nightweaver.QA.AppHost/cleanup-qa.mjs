import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, readFile, access, writeFile } from 'node:fs/promises';
import path from 'node:path';

const url = process.env.NIGHTWEAVER_QA_URL;
assert(url, 'Use the direct web endpoint discovered by aspire describe');
const output = process.env.NIGHTWEAVER_QA_OUTPUT ?? '../../TestResults/cleanup';
await mkdir(output, { recursive: true });
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
const errors = [];
page.on('pageerror', error => errors.push(error.message));
page.on('console', message => { if (message.type() === 'error' && !message.text().includes('409')) errors.push(message.text()); });
const state = () => page.evaluate(async () => (await fetch('/api/cleanup')).json());
const post = (item, overrides = {}) => page.evaluate(async ({ item, overrides, runtimeId }) => {
  const response = await fetch(`/api/cleanup/${item.beadId}/retry`, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Nightweaver-Request': 'console-v1' },
    body: JSON.stringify({ runtimeId, revision: item.revision, requestId: crypto.randomUUID().replaceAll('-', ''), ...overrides }) });
  return { status: response.status, body: await response.json() };
}, { item, overrides, runtimeId: real.runtimeId });
let real;
try {
  await page.goto(url);
  await page.waitForFunction(() => document.querySelectorAll('.cleanup-count').length === 4);
  await page.locator('#housekeeping').screenshot({ path: path.join(output, 'compact-1440.png') });
  await page.locator('#cleanup-disclosure > summary').click();
  await page.waitForFunction(() => document.querySelectorAll('.cleanup-row').length >= 2);
  real = await state();
  assert.equal(real.canRetry, true);
  const target = real.items.find(item => item.record.attempts === 4);
  const protectedItem = real.items.find(item => item.record.diagnostic === 'Paused QA environment is protected.');
  assert(target && protectedItem);
  assert.equal(target.status, 'needs-attention');
  assert.equal(await readFile(path.join(protectedItem.record.tmp.path, 'protected-evidence'), 'utf8'), 'preserve');
  assert.equal((await post(target, { revision: '0'.repeat(64) })).status, 409);
  assert.equal((await post(protectedItem)).status, 409);
  const before = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), target.beadId);
  assert.equal(before.bead.status, 'closed');
  const card = page.locator('.cleanup-row').filter({ has: page.getByRole('link', { name: target.beadId, exact: true }) });
  await card.getByRole('button', { name: 'Retry cleanup', exact: true }).click();
  await page.waitForFunction(() => document.querySelector('#cleanup-message').textContent.includes('Queued for owner inspection'));
  const deadline = Date.now() + 30000;
  let after;
  do {
    after = await state();
    if (after.items.some(item => item.beadId === target.beadId && item.status === 'resolved' && item.record.tmp.confirmedAbsent)) break;
    await page.waitForTimeout(250);
  } while (Date.now() < deadline);
  const resolved = after.items.find(item => item.beadId === target.beadId);
  await writeFile(path.join(output, 'real-retry-evidence.json'), JSON.stringify({ before, target, after }, null, 2));
  assert.equal(resolved.status, 'resolved');
  assert.equal(resolved.record.attempts, 5);
  assert.equal(resolved.retry.outcome, 'accepted');
  await assert.rejects(access(target.record.tmp.path));
  assert.equal(await readFile(path.join(protectedItem.record.tmp.path, 'protected-evidence'), 'utf8'), 'preserve');
  const afterBead = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), target.beadId);
  assert.deepEqual(afterBead.bead, before.bead);
  await page.locator('#cleanup-refresh').click();
  await page.waitForFunction(() => document.querySelector('#cleanup-rows').textContent.includes('Resolved'));
  await page.locator('#housekeeping').screenshot({ path: path.join(output, 'real-resolved.png') });

  // Deterministic reads exercise the full inventory and responsive states; mutations above
  // used the real same-origin endpoint and runtime-owned durable inbox.
  const items = Array.from({ length: 27 }, (_, index) => ({ ...target,
    beadId: `qa-cleanup-${String(index).padStart(2, '0')}`, revision: String(index).padStart(64, 'A'),
    status: ['pending', 'retry-scheduled', 'needs-attention', 'resolved'][index % 4], retry: index === 2 ? { outcome: 'expired', requestId: 'old-owner' } : index === 6 ? { outcome: 'queued', requestId: 'queued-request' } : null,
    record: { ...target.record, diagnostic: index % 4 === 2 ? 'Access denied. Inspect permissions; Retry cannot bypass a denial.' : null,
      lastAttempt: new Date(Date.now() - 60000).toISOString(),
      nextAttempt: index % 4 === 1 ? new Date(Date.now() + 120000).toISOString() : null,
      resolvedAt: index % 4 === 3 ? new Date().toISOString() : null,
      worktree: { path: 'H:\\disposable\\worktrees\\' + 'long-owned-resource-'.repeat(12), confirmedAbsent: index % 4 === 3 },
      tmp: { ...target.record.tmp, confirmedAbsent: index % 4 === 3 } } }));
  let unavailable = false, ownerState = 'live';
  await page.route('**/api/cleanup?*', async route => {
    const request = new URL(route.request().url());
    const q = request.searchParams.get('q') ?? '', filter = request.searchParams.get('status');
    const matching = items.filter(item => (!filter || item.status === filter) && item.beadId.includes(q));
    const pages = Math.max(1, Math.ceil(matching.length / 8)), current = Math.min(pages, Number(request.searchParams.get('page') ?? 1));
    await route.fulfill({ json: unavailable ? { ...real, availability: 'unavailable', canRetry: false, counts: null, reason: 'Corrupt cleanup store; evidence retained.', items: [], page: 1, pages: 1, total: 0 }
      : { ...real, availability: ownerState, canRetry: ownerState === 'live', counts: { pending: 7, 'retry-scheduled': 7, 'needs-attention': 7, resolved: 6 }, total: matching.length, page: current, pages, items: matching.slice((current - 1) * 8, current * 8) } });
  });
  await page.locator('#cleanup-refresh').click();
  await page.waitForFunction(() => document.querySelector('#cleanup-range').textContent.includes('of 27'));
  assert.equal(await page.locator('.cleanup-row').count(), 8);
  const details = page.locator('.cleanup-row details > summary').first();
  await details.click(); await details.focus();
  await page.evaluate(() => document.querySelector('#cleanup-refresh').click());
  await page.waitForTimeout(350);
  assert.equal(await page.locator('.cleanup-row details').first().getAttribute('open'), '');
  assert.equal(await page.evaluate(() => document.activeElement.dataset.cleanupFocus), 'qa-cleanup-00:details');
  await page.locator('#cleanup-next').click();
  await page.waitForFunction(() => document.querySelector('#cleanup-range').textContent.startsWith('9–16'));
  await page.locator('#cleanup-search').fill('qa-cleanup-26');
  await page.waitForFunction(() => document.querySelectorAll('.cleanup-row').length === 1);
  assert.match(await page.locator('#cleanup-rows').innerText(), /qa-cleanup-26/);
  await page.locator('#cleanup-search').fill('');
  await page.waitForFunction(() => document.querySelectorAll('.cleanup-row').length === 8);
  await page.locator('#cleanup-filter').selectOption('needs-attention');
  await page.waitForFunction(() => document.querySelectorAll('.cleanup-row').length === 7);
  assert.equal(await page.locator('.cleanup-row:not(.cleanup-needs-attention)').count(), 0);
  await page.locator('#cleanup-filter').selectOption('');
  await page.waitForFunction(() => document.querySelectorAll('.cleanup-row').length === 8);
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 1000 });
    await page.locator('.cleanup-row details').first().evaluate(node => { node.open = true; });
    await page.locator('#housekeeping').scrollIntoViewIfNeeded();
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `Overflow at ${width}`);
    await page.locator('#housekeeping').screenshot({ path: path.join(output, `states-${width}.png`) });
  }
  ownerState = 'stopped';
  await page.locator('#cleanup-refresh').click();
  await page.waitForFunction(() => document.querySelector('#cleanup-health').textContent.includes('Owner stopped'));
  assert.equal(await page.getByRole('button', { name: 'Retry cleanup', exact: true }).count(), 2);
  for (const button of await page.getByRole('button', { name: 'Retry cleanup', exact: true }).all()) assert.equal(await button.isDisabled(), true);
  await page.locator('#housekeeping').screenshot({ path: path.join(output, 'stopped.png') });
  unavailable = true;
  await page.locator('#cleanup-refresh').click();
  await page.waitForFunction(() => document.querySelector('#cleanup-health').textContent.startsWith('Unavailable'));
  assert.match(await page.locator('#cleanup-range').innerText(), /not an empty queue/);
  await page.locator('#housekeeping').screenshot({ path: path.join(output, 'unavailable.png') });
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ native: { bead: target.beadId, priorAttempts: 4, finalAttempts: 5, status: resolved.status, protectedEvidence: 'preserved' }, viewports: [1440, 375, 720], errors, output }));
} finally { await browser.close(); }
