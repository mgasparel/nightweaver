import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL from aspire describe.');
const output = new URL('../../TestResults/digest/', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const errors = [];
const item = id => ({ id, title: 'Long operational milestone '.repeat(8), type: 'task', priority: 1,
  status: 'closed', activityKind: 'closed', activityAt: '2026-11-12T10:00:00Z', outcome: 'resolved',
  outcomeLabel: 'Resolved', summary: 'Full milestone context that remains available through accessible disclosure. '.repeat(8),
  commits: ['a'.repeat(40)], pullRequests: [], fromId: null });
const busy = { generatedAt: '2026-11-12T12:00:00Z', window: '24h', lastCaughtUpAt: null, isCaughtUp: false,
  metrics: { completedTotal: 69, completedFeatures: 7, completedTasks: 62, completedBugs: 0,
    activeThreads: 7, openOrUpdatedTotal: 4, waitingForGuidance: 4 },
  threads: Array.from({ length: 7 }, (_, i) => ({ root: item(`root-${i}`), completedCount: 8, openCount: 0,
    items: Array.from({ length: 8 }, (_, j) => item(`child-${i}-${j}`)) })),
  standaloneCompleted: Array.from({ length: 8 }, (_, i) => item(`solo-${i}`)),
  standaloneOpen: [item('open-0'), item('open-1')] };
busy.threads[0].items[0].summary = 'a'.repeat(140);
busy.threads[0].items[1].summary = 'b'.repeat(141);
const empty = { ...busy, threads: [], standaloneCompleted: [], standaloneOpen: [],
  metrics: { completedTotal: 0, completedFeatures: 0, completedTasks: 0, completedBugs: 0,
    activeThreads: 0, openOrUpdatedTotal: 0, waitingForGuidance: 0 } };
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  // Real disposable tracker: save through the UI and independently read the persisted checkpoint.
  await page.goto(url, { waitUntil: 'networkidle' });
  await page.waitForFunction(() => document.querySelector('#digest-status').textContent === '');
  assert.equal(await page.locator('#digest-body').isVisible(), false);
  const snapshot = await page.evaluate(async () => (await (await fetch('/api/digest')).json()));
  // Refresh obtains the exact snapshot used by the actual UI, observed from its response.
  const response = page.waitForResponse(r => new URL(r.url()).pathname === '/api/digest');
  await page.locator('#digest-refresh').click();
  const loaded = await (await response).json();
  await page.waitForFunction(() => !document.querySelector('#digest-refresh').disabled);
  const save = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith('/api/digest/catch-up'));
  await page.locator('#digest-mark-caught-up').click();
  assert.equal((await save).status(), 200);
  await page.waitForFunction(() => document.querySelector('[data-digest-window="since_caught_up"]').getAttribute('aria-pressed') === 'true');
  const persisted = await page.evaluate(async () => (await (await fetch('/api/digest')).json()));
  assert.equal(Date.parse(persisted.lastCaughtUpAt), Date.parse(loaded.generatedAt));
  assert.equal(await page.locator('#digest-body').isVisible(), false);
  assert.ok(snapshot.metrics);

  let data = structuredClone(busy);
  let failRead = false;
  let failSave = false;
  let invalidSave = false;
  let posted;
  await page.route('**/api/digest**', async route => {
    if (route.request().method() === 'POST') {
      posted = route.request().postDataJSON().caughtUpAt;
      if (failSave) return route.fulfill({ status: 409, json: { detail: 'Checkpoint save rejected' } });
      data = { ...empty, window: 'since_caught_up', lastCaughtUpAt: posted,
        standaloneOpen: [item('newer-activity')], metrics: { ...empty.metrics, openOrUpdatedTotal: 1 } };
      return route.fulfill({ json: { lastCaughtUpAt: invalidSave ? 'invalid' : posted } });
    }
    if (failRead) return route.fulfill({ status: 503, json: { detail: 'Tracker offline' } });
    const window = new URL(route.request().url()).searchParams.get('window');
    return route.fulfill({ json: { ...data, window: window ?? data.window } });
  });
  await page.reload({ waitUntil: 'networkidle' });
  const refresh = async () => {
    await page.locator('#digest-refresh').evaluate(button => button.click());
    await page.waitForFunction(() => !document.querySelector('#digest-refresh').disabled);
  };
  const count = async (selector, expected) => assert.equal(await page.locator(selector).count(), expected);
  const noOverflow = async () => assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth), true);
  const height = await page.locator('#catch-up').evaluate(element => element.getBoundingClientRect().height);
  console.log(`Collapsed busy desktop digest: ${height}px`);
  assert.ok(height >= 76 && height <= 96 && height < 6 * 16);
  await page.screenshot({ path: `${output}/busy-desktop.png`, fullPage: false });
  await noOverflow();
  await page.locator('#digest-toggle').focus();
  await page.keyboard.press('Enter');
  await count('#digest-threads > article', 5);
  await count('#digest-threads > article:first-child .digest-item-row', 4);
  await count('#digest-standalone > .digest-item-row', 6);
  await count('[data-bead-id="child-0-0"] .digest-inline-toggle', 0);
  await count('[data-bead-id="child-0-1"] .digest-summary.is-clamped', 1);
  const threadMore = page.getByRole('button', { name: 'Show 4 more in thread', exact: true }).first();
  await threadMore.focus();
  await page.keyboard.press('Space');
  await count('#digest-threads > article:first-child .digest-item-row', 8);
  assert.equal(await page.evaluate(() => document.activeElement.textContent), 'Show fewer in thread');
  await refresh();
  assert.equal(await page.evaluate(() => document.activeElement.textContent), 'Show fewer in thread');
  await count('#digest-threads > article:first-child .digest-item-row', 8);
  assert.equal(await page.locator('#digest-threads > article:first-child .digest-summary').first().getAttribute('class'), 'digest-summary is-clamped');
  const summary = page.locator('#digest-threads > article:first-child .digest-inline-toggle').first();
  await summary.focus();
  await page.keyboard.press('Enter');
  assert.equal(await summary.getAttribute('aria-expanded'), 'true');
  await refresh();
  assert.equal(await page.evaluate(() => document.activeElement.textContent), 'Show less');
  assert.equal(await page.locator('#digest-threads > article:first-child .digest-summary').first().textContent(), busy.threads[0].root.summary);
  await page.keyboard.press('Enter');
  assert.equal(await summary.getAttribute('aria-expanded'), 'false');
  await page.getByRole('button', { name: 'Show 2 more threads', exact: true }).click();
  await count('#digest-threads > article', 7);
  await page.getByRole('button', { name: 'Show 4 more standalone items', exact: true }).click();
  await count('#digest-standalone > .digest-item-row', 10);
  await page.screenshot({ path: `${output}/expanded-desktop.png`, fullPage: false });
  await page.getByRole('button', { name: 'Show fewer threads', exact: true }).click();
  await count('#digest-threads > article', 5);
  await page.getByRole('button', { name: 'Show fewer standalone items', exact: true }).click();
  await count('#digest-standalone > .digest-item-row', 6);
  await page.getByRole('button', { name: 'Show 2 more threads', exact: true }).click();
  await page.getByRole('button', { name: 'Show 4 more standalone items', exact: true }).click();
  await page.locator('[data-digest-window="7d"]').click();
  await page.waitForFunction(() => !document.querySelector('#digest-refresh').disabled);
  await count('#digest-threads > article:first-child .digest-item-row', 4);
  await count('#digest-threads > article', 5);
  await count('#digest-standalone > .digest-item-row', 6);
  assert.equal(await page.locator('#digest-body').isVisible(), true);
  await page.locator('#digest-toggle').click();
  await refresh();
  assert.equal(await page.locator('#digest-body').isVisible(), false);
  failRead = true;
  await refresh();
  assert.equal(await page.locator('#digest-error').isVisible(), true);
  assert.match(await page.locator('#digest-status').textContent(), /last confirmed/);
  await page.screenshot({ path: `${output}/error-desktop.png`, fullPage: false });
  failRead = false;
  await page.locator('#digest-retry').click();
  await page.waitForFunction(() => !document.querySelector('#digest-retry').disabled);
  assert.equal(await page.locator('#digest-body').isVisible(), false);
  await page.locator('#digest-toggle').click();
  failSave = true;
  await page.locator('#digest-mark-caught-up').click();
  await page.waitForFunction(() => !document.querySelector('#digest-mark-caught-up').disabled);
  assert.equal(await page.locator('#digest-body').isVisible(), true);
  assert.equal(await page.locator('#digest-error').isVisible(), true);
  failSave = false;
  invalidSave = true;
  await page.locator('#digest-mark-caught-up').click();
  await page.waitForFunction(() => !document.querySelector('#digest-mark-caught-up').disabled);
  assert.equal(await page.locator('#digest-body').isVisible(), true);
  assert.match(await page.locator('#digest-error-text').textContent(), /could not be verified/);
  invalidSave = false;
  await page.locator('#digest-mark-caught-up').click();
  await page.waitForFunction(() => document.querySelector('#digest-body').hidden);
  assert.equal(posted, busy.generatedAt);
  assert.equal(await page.locator('#digest-metric-open').textContent(), '1');
  await page.locator('#digest-toggle').click();
  await count('[data-bead-id="newer-activity"]', 1);
  data = { ...empty, lastCaughtUpAt: posted, isCaughtUp: true, window: 'since_caught_up' };
  await refresh();
  assert.equal(await page.locator('#digest-caught-up-bar').isVisible(), true);
  await page.locator('#digest-view-24h').click();
  await page.waitForFunction(() => !document.querySelector('#digest-refresh').disabled);
  assert.equal(await page.locator('#digest-body').isVisible(), true);
  await page.locator('#digest-toggle').click();
  assert.equal(await page.locator('#digest-caught-up-bar').isVisible(), false);
  await page.screenshot({ path: `${output}/caught-up-desktop.png`, fullPage: false });
  data = { ...empty, isCaughtUp: false };
  await refresh();
  await page.locator('#digest-toggle').click();
  assert.equal(await page.locator('#digest-empty').isVisible(), true);
  await page.screenshot({ path: `${output}/empty-desktop.png`, fullPage: false });
  // Removal of the focused item falls back to the persistent section control.
  data = structuredClone(busy);
  await refresh();
  await summary.focus();
  data = structuredClone(empty);
  await refresh();
  assert.equal(await page.evaluate(() => document.activeElement.id), 'digest-toggle');
  for (const width of [720, 375]) {
    await page.setViewportSize({ width, height: 900 });
    data = structuredClone(busy);
    await page.reload({ waitUntil: 'networkidle' });
    await noOverflow();
    await page.screenshot({ path: `${output}/busy-${width}.png`, fullPage: false });
    await page.locator('#digest-toggle').click();
    await noOverflow();
    await page.screenshot({ path: `${output}/expanded-${width}.png`, fullPage: false });
    await page.locator('#digest-toggle').click();
    failRead = true;
    await refresh();
    await noOverflow();
    await page.screenshot({ path: `${output}/error-${width}.png`, fullPage: false });
    failRead = false;
    data = { ...empty, isCaughtUp: true, lastCaughtUpAt: busy.generatedAt, window: 'since_caught_up' };
    await refresh();
    await noOverflow();
    await page.screenshot({ path: `${output}/caught-up-${width}.png`, fullPage: false });
  }
  // Deliberate HTTP error fixtures emit network console errors; all other errors fail QA.
  assert.deepEqual(errors.filter(error => !/Failed to load resource.*(503|409)/.test(error)), []);
  console.log('Digest QA passed: actual checkpoint persistence, 5/4/6 bounds, summary disclosure, focus, windows, failures, newer activity and responsive screenshots.');
} finally {
  await browser.close();
}
