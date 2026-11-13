import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL from aspire describe.');
const output = process.env.NIGHTWEAVER_QA_OUTPUT ?? fileURLToPath(new URL('../../TestResults/explorer/', import.meta.url));
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const errors = [];
const items = Array.from({ length: 211 }, (_, i) => ({ id: `qa-${i}`, title: i === 210 ? 'Off-page needle' : `Operational Bead ${i} ${'long-context-'.repeat(13)}`,
  type: 'task', priority: 2, status: 'open', labels: [], assignee: null }));
let data = items;
let fail = false;
let delay = false;
let posts = 0;
let created;
let holdResponse;
let releaseResponse;
const queries = [];
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => {
    if (message.type() === 'error' && !message.text().includes('503')) errors.push(message.text());
  });
  // Only list responses are substituted. Intake and Workspace use the disposable native tracker.
  await page.route('**/api/beads?*', async route => {
    const query = new URL(route.request().url()).searchParams;
    queries.push(query);
    assert.deepEqual([...query.keys()].sort(), query.has('q') ? ['q', 'view'] : ['view']);
    if (holdResponse) await holdResponse;
    if (fail) return route.fulfill({ status: 503, json: { detail: 'Tracker unavailable' } });
    const response = data.filter(item => `${item.id} ${item.title}`.toLowerCase().includes((query.get('q') ?? '').toLowerCase()));
    if (delay && query.get('q') === 'slow') await new Promise(resolve => setTimeout(resolve, 600));
    await route.fulfill({ json: response });
  });
  page.on('request', request => { if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/beads') posts++; });
  await page.goto(url, { waitUntil: 'networkidle' });
  const rows = page.locator('#beads-table-body tr');
  const waitRange = text => page.waitForFunction(text => document.querySelector('#beads-range').textContent === text, text);
  const refresh = () => page.locator('#beads-retry').evaluate(button => button.click());
  const assertFocus = async selector => {
    assert.equal(await page.locator(selector).evaluate(element => element === document.activeElement), true);
    assert.equal(await page.locator(selector).isVisible(), true);
    assert.equal(await page.locator(selector).evaluate(element => getComputedStyle(element).outlineStyle), 'solid');
  };
  const filter = async view => {
    await page.locator(`[data-view="${view}"]`).click();
    await waitRange(`Showing 1–15 of ${data.length} beads`);
  };
  await waitRange('Showing 1–15 of 211 beads');
  assert.equal(queries[0].get('view'), 'ready');
  assert.equal(await page.locator('[data-view="ready"]').getAttribute('aria-pressed'), 'true');
  for (const view of ['ready', 'active', 'blocked', 'all', 'closed']) {
    await filter(view);
    assert.equal(await rows.count(), 15);
    await page.locator('#beads-next-page').focus();
    await page.keyboard.press('Enter');
    await waitRange('Showing 16–30 of 211 beads');
    assert.equal(await page.locator('#beads-next-page').evaluate(button => button === document.activeElement), true);
    assert.match(await page.locator('#beads-live').textContent(), /Showing 16–30 of 211 beads/);
    await refresh();
    await waitRange('Showing 16–30 of 211 beads');
    assert.equal(await page.locator('#beads-next-page').evaluate(button => button === document.activeElement), true);
  }
  // Boundary navigation must transfer focus before the activated button becomes disabled.
  data = items.slice(0, 16);
  await filter('ready');
  await page.locator('#beads-next-page').focus();
  await page.keyboard.press('Enter');
  await waitRange('Showing 16–16 of 16 beads');
  await assertFocus('#beads-prev-page');
  await page.keyboard.press('Enter');
  await waitRange('Showing 1–15 of 16 beads');
  await assertFocus('#beads-next-page');

  // Refresh to one/zero pages or an error keeps a visible, stable range focus target.
  for (const result of ['one', 'zero', 'error']) {
    data = items.slice(0, 16);
    await refresh();
    await waitRange('Showing 1–15 of 16 beads');
    await page.locator('#beads-next-page').focus();
    await page.keyboard.press('Enter');
    await waitRange('Showing 16–16 of 16 beads');
    await assertFocus('#beads-prev-page');
    data = result === 'one' ? items.slice(0, 1) : [];
    fail = result === 'error';
    await refresh();
    await waitRange(result === 'error' ? 'Results unavailable' : result === 'one' ? 'Showing 1–1 of 1 beads' : 'Showing 0–0 of 0 beads');
    await assertFocus('#beads-range');
    assert.equal(await page.locator('#beads-prev-page').isDisabled(), true);
    assert.equal(await page.locator('#beads-next-page').isDisabled(), true);
    if (result === 'one') await page.locator('#beads-pagination').screenshot({ path: `${output}/boundary-focus.png` });
    fail = false;
  }
  // A pending response must not steal focus after the user leaves pagination.
  data = items;
  await filter('ready');
  await page.locator('#beads-next-page').focus();
  await page.keyboard.press('ArrowRight');
  holdResponse = new Promise(resolve => { releaseResponse = resolve; });
  await refresh();
  await waitRange('Loading results…');
  await assertFocus('#beads-range');
  await page.locator('#beads-search').focus();
  releaseResponse();
  holdResponse = null;
  await waitRange('Showing 1–15 of 211 beads');
  await assertFocus('#beads-search');
  await page.locator('#beads-search').fill('needle');
  await waitRange('Showing 1–1 of 1 beads');
  assert.match(await rows.first().textContent(), /qa-210/);
  assert.equal(queries.at(-1).get('q'), 'needle');
  await page.locator('#beads-search').fill('absent');
  await waitRange('Showing 0–0 of 0 beads');
  assert.equal(await page.locator('#beads-page').textContent(), 'Page 1 of 1');
  assert.equal(await page.locator('#beads-no-matches').isVisible(), true);
  assert.equal(await page.locator('#beads-prev-page').isDisabled(), true);
  assert.equal(await page.locator('#beads-next-page').isDisabled(), true);
  delay = true;
  await page.locator('#beads-search').fill('slow');
  await page.waitForRequest(request => new URL(request.url()).searchParams.get('q') === 'slow');
  await page.locator('#beads-search').fill('needle');
  await waitRange('Showing 1–1 of 1 beads');
  await page.waitForTimeout(650);
  await waitRange('Showing 1–1 of 1 beads');
  await page.locator('#beads-search').fill('');
  await waitRange('Showing 1–15 of 211 beads');
  for (let i = 1; i < 15; i++) await page.locator('#beads-next-page').click();
  await waitRange('Showing 211–211 of 211 beads');
  assert.equal(await page.locator('#beads-page').textContent(), 'Page 15 of 15');
  assert.equal(await page.locator('#beads-next-page').isDisabled(), true);
  data = items.slice(0, 17);
  await refresh();
  await waitRange('Showing 16–17 of 17 beads');
  data = [];
  await refresh();
  await waitRange('Showing 0–0 of 0 beads');
  assert.equal(await page.locator('#beads-empty').isVisible(), true);
  await page.locator('#beads').screenshot({ path: `${output}/empty.png` });
  data = items;
  await filter('ready');
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    await page.locator('#beads').evaluate(section => section.scrollIntoView({ block: 'start', behavior: 'instant' }));
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth), true);
    assert.equal(await rows.count(), 15);
    await page.screenshot({ path: `${output}/large-${width}.png` });
  }
  fail = true;
  await refresh();
  await page.locator('#beads-error').waitFor();
  assert.equal(await page.locator('#beads-range').textContent(), 'Results unavailable');
  await page.locator('#beads').screenshot({ path: `${output}/error.png` });
  fail = false;
  await refresh();
  await waitRange('Showing 1–15 of 211 beads');
  const create = async failRefresh => {
    await page.locator('#new-bead-trigger').click();
    await page.locator('#intake-title-input').fill('Explorer native intake QA');
    await page.locator('#intake-description-input').fill('Disposable backlog Product to verify discoverability.');
    await page.locator('#intake-acceptance').fill('Creation stays successful when list refresh fails.');
    const response = page.waitForResponse(response => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/beads');
    fail = failRefresh;
    await page.locator('#intake-submit').click();
    const result = await response;
    assert.equal(result.status(), 201);
    created = (await result.json()).id;
    await page.waitForFunction(() => !document.querySelector('#bead-intake-dialog').open);
  };
  await create(true);
  await page.locator('#beads-error').waitFor();
  assert.match(await page.locator('#beads-create-success').textContent(), new RegExp(`Created ${created}.*Retry`));
  assert.equal(posts, 1);
  data = [...items, { ...items[0], id: created }];
  fail = false;
  await refresh();
  await waitRange('Showing 211–212 of 212 beads');
  assert.match(await rows.last().textContent(), new RegExp(created));
  await rows.last().locator('a').first().click();
  await page.locator('#workspace-title').filter({ hasText: 'Explorer native intake QA' }).waitFor();
  await page.locator('.workspace-back').click();
  await waitRange('Showing 211–212 of 212 beads');
  assert.equal(posts, 1);
  data = items;
  await create(false);
  await page.locator('#beads-show-created').waitFor();
  data = [{ ...items[0], id: created }];
  await page.locator('#beads-show-created').click();
  await waitRange('Showing 1–1 of 1 beads');
  assert.equal(queries.at(-1).get('view'), 'all');
  assert.equal(queries.at(-1).get('q'), created);
  assert.equal(posts, 2);
  assert.deepEqual(errors, []);
  console.log('Explorer QA passed: all filters bounded, full-list search, stale requests, reset/clamp, keyboard focus, responsive layouts, native intake/Workspace and failed refresh recovery.');
} finally {
  await browser.close();
}
