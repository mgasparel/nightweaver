import assert from 'node:assert/strict';
import { mkdir, readFile, access, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

const base = process.env.NIGHTWEAVER_QA_URL;
assert(base, 'Use the isolated cleanup scenario direct endpoint');
const output = process.env.NIGHTWEAVER_QA_OUTPUT ?? '../../TestResults/studio-cleanup';
await mkdir(output, { recursive: true });
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
page.setDefaultTimeout(45000);
const errors = [], posts = [];
page.on('pageerror', error => errors.push(error.message));
page.on('request', request => { if (request.method() === 'POST' && request.url().includes('/api/cleanup/')) posts.push(request.postDataJSON()); });
const capture = async name => { for (const width of [1440, 375, 720]) {
  await page.setViewportSize({ width, height: 1000 });
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Overflow ${width}`);
  const control = page.getByRole('searchbox', { name: 'Search cleanup', exact: true });
  await control.focus();
  assert(await control.evaluate(node => document.activeElement === node), `Focus ${name} ${width}`);
  await page.screenshot({ path: `${output}/${name}-${width}.png`, fullPage: true });
} };
try {
  await page.goto(base + '/projects/qa/cleanup');
  await page.getByText(/2 matching obligations/).waitFor();
  const read = () => page.evaluate(async () => (await fetch('/api/cleanup')).json());
  const initial = await read();
  assert(initial.canRetry);
  const target = initial.items.find(item => item.record.attempts === 4);
  const protectedItem = initial.items.find(item => item.record.diagnostic === 'Paused QA environment is protected.');
  assert(target && protectedItem);
  const card = page.locator('.cleanup-card').filter({ has: page.getByRole('link', { name: target.beadId, exact: true }) });
  const retry = card.getByRole('button', { name: 'Retry cleanup', exact: true });
  await card.locator('summary').click();
  await card.locator('summary').focus();
  await page.evaluate(() => [...document.querySelectorAll('button')].find(n => n.textContent === 'Refresh view').click());
  await page.waitForTimeout(1500);
  assert(await card.locator('summary').evaluate(node => document.activeElement === node && node.parentElement.open));
  await capture('cleanup-before');
  await page.route('**/api/cleanup/*/retry', route => route.continue({ postData: JSON.stringify({ ...route.request().postDataJSON(), revision: '0'.repeat(64) }) }), { times: 1 });
  const conflict = page.waitForResponse(r => r.url().includes('/api/cleanup/') && r.request().method() === 'POST');
  await retry.click(); assert.equal((await conflict).status(), 409);
  await card.getByText(/Evidence changed/).waitFor();
  assert(await retry.isDisabled());
  await card.getByRole('button', { name: 'I reviewed current cleanup evidence' }).click();
  const beforeBead = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), target.beadId);
  const queued = page.waitForResponse(r => r.url().includes('/api/cleanup/') && r.request().method() === 'POST');
  await retry.click(); assert.equal((await queued).status(), 202);
  await card.getByText('Cleanup confirmed: worktree, registration and temporary files are absent.', { exact: true }).waitFor();
  const after = await read();
  const resolved = after.items.find(item => item.beadId === target.beadId);
  assert.equal(resolved.status, 'resolved'); assert.equal(resolved.record.attempts, 5);
  await assert.rejects(access(target.record.tmp.path));
  assert.equal(await readFile(path.join(protectedItem.record.tmp.path, 'protected-evidence'), 'utf8'), 'preserve');
  const afterBead = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), target.beadId);
  assert.deepEqual(beforeBead.bead, afterBead.bead);
  await capture('cleanup-resolved');
  // Only read projections are substituted below. Commands above used the native owner/inbox.
  const items = Array.from({ length: 27 }, (_, index) => ({ ...target, beadId: `qa-cleanup-${index}`, status: ['pending', 'retry-scheduled', 'needs-attention', 'resolved'][index % 4], retry: index === 2 ? { outcome: 'expired' } : null,
    record: { ...target.record, diagnostic: 'Access denied. Inspect the current owner and paths.', worktree: { path: 'H:/disposable/' + 'long-path-'.repeat(30), confirmedAbsent: false } } }));
  let unavailable = false, stopped = false;
  await page.route('**/resources?*', async route => {
    const response = await route.fetch(); const envelope = await response.json();
    if (!envelope.key?.startsWith('cleanup:')) return route.fulfill({ response });
    const [, requested, filter, encoded] = envelope.key.split(':');
    const q = Buffer.from(encoded, 'base64url').toString('utf8');
    const matching = items.filter(item => (filter === 'all' || item.status === filter) && item.beadId.includes(q));
    const pages = Math.max(1, Math.ceil(matching.length / 8)), current = Math.min(pages, Number(requested));
    envelope.data = { ...initial, availability: unavailable ? 'unavailable' : stopped ? 'stopped' : 'live', canRetry: !unavailable && !stopped,
      counts: unavailable ? null : { pending: 7, 'retry-scheduled': 7, 'needs-attention': 7, resolved: 6 }, total: matching.length, page: current, pages,
      reason: unavailable ? 'Corrupt cleanup evidence; resources retained.' : '', items: unavailable ? [] : matching.slice((current - 1) * 8, current * 8) };
    envelope.health = unavailable ? 'unavailable' : 'fresh';
    envelope.observedAt = unavailable ? null : new Date().toISOString();
    envelope.error = unavailable ? { code: 'source-failed', message: 'Cleanup evidence unavailable', retryable: true } : null;
    for (const source of Object.values(envelope.sources)) {
      source.health = envelope.health; source.observedAt = envelope.observedAt; source.error = envelope.error;
    }
    await route.fulfill({ json: envelope });
  });
  await page.getByRole('button', { name: 'Refresh view', exact: true }).click();
  await page.getByText(/27 matching obligations/).waitFor();
  assert.equal(await page.locator('.cleanup-card').count(), 8);
  await page.getByRole('button', { name: 'Next page', exact: true }).click();
  await page.getByText('Page 2 of 4 · 27 matching obligations', { exact: true }).waitFor();
  await page.getByRole('searchbox', { name: 'Search cleanup', exact: true }).fill('qa-cleanup-26');
  await page.getByText('Page 1 of 1 · 1 matching obligations', { exact: true }).waitFor();
  await page.getByRole('searchbox', { name: 'Search cleanup', exact: true }).fill('');
  await page.getByText('Page 1 of 4 · 27 matching obligations', { exact: true }).waitFor();
  await page.getByRole('combobox', { name: 'Status', exact: true }).selectOption('needs-attention');
  await page.getByText(/7 matching obligations/).waitFor();
  await page.locator('.cleanup-card summary').first().click();
  await capture('cleanup-states');
  stopped = true;
  await page.getByRole('button', { name: 'Refresh view', exact: true }).click();
  await page.getByText(/Owner stopped/).waitFor();
  assert(await page.getByRole('button', { name: 'Retry cleanup', exact: true }).first().isDisabled());
  await capture('cleanup-stopped');
  unavailable = true;
  await page.getByRole('button', { name: 'Refresh view', exact: true }).click();
  await page.getByText('Cleanup evidence unavailable; this is not an empty queue.', { exact: true }).waitFor();
  await capture('cleanup-unavailable');
  assert.deepEqual(errors, []);
  await writeFile(`${output}/result.json`, JSON.stringify({ target: target.beadId, resolved, posts, errors }, null, 2));
  console.log(JSON.stringify({ target: target.beadId, resolved: true, attempts: resolved.record.attempts, posts: posts.length, errors, output }));
} catch (error) {
  await page.screenshot({ path: `${output}/failure.png`, fullPage: true });
  console.log(await page.locator('main').innerText());
  throw error;
} finally { await browser.close(); }
