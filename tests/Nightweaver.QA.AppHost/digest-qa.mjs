import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';
import { digestFixture, refreshDigest } from './digest-fixture.mjs';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL from aspire describe.');
const configPath = process.env.NIGHTWEAVER_QA_CONFIG;
assert(configPath, 'Set NIGHTWEAVER_QA_CONFIG from the exact AppHost');
const root = path.dirname(path.resolve(configPath));
assert(path.basename(root).startsWith('nightweaver-qa-'));
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
const repo = path.resolve(config.target_repository.path);
assert.equal(repo, path.join(root, 'repo')); assert.equal(config.name, 'qa');
const env = {...process.env};
for (const key of Object.keys(env)) if (/^(BEADS_|BD_|GIT_)/.test(key)) delete env[key];
for (const key of ['HOME', 'USERPROFILE', 'APPDATA', 'XDG_CONFIG_HOME']) env[key] = path.join(root, 'home');
Object.assign(env, {GIT_CONFIG_GLOBAL: path.join(root, 'home', 'gitconfig'), GIT_CONFIG_NOSYSTEM: '1', BD_NO_HOOKS: 'true', BD_DISABLE_METRICS: '1', BD_DOLT_AUTO_START: 'false', OTEL_SDK_DISABLED: 'true'});
for (const key of ['BD_IMPORT_AUTO', 'BD_EXPORT_AUTO', 'BD_BACKUP_ENABLED', 'BD_DOLT_AUTO_PUSH', 'BD_EVENTS_EXPORT']) env[key] = 'false';
const bd = args => execFileSync('bd', ['--sandbox', ...args], {cwd: repo, env, encoding: 'utf8', timeout: 30000});
const output = process.env.NIGHTWEAVER_QA_OUTPUT ?? new URL('../../TestResults/digest/', import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1');
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
  await page.goto(url + '/projects/qa/catch-up', { waitUntil: 'networkidle' });
  await page.waitForFunction(() => document.querySelector('#digest-status').textContent === '');
  assert.equal(await page.locator('#digest-body').isVisible(), false);
  const snapshot = await page.evaluate(async () => (await (await fetch('/api/digest')).json()));
  // Refresh obtains the exact snapshot used by the actual UI, observed from its response.
  const response = page.waitForResponse(r => new URL(r.url()).searchParams.get('key') === 'base:digest:auto');
  await page.locator('#digest-refresh').click();
  await response;
  await page.waitForTimeout(200);
  const displayedCutoff = await page.locator('#catch-up').getAttribute('data-generated-at');
  let entered; let release;
  const arrived = new Promise(resolve => entered = resolve);
  const gate = new Promise(resolve => release = resolve);
  await page.route('**/api/digest/catch-up', async route => { entered(); await gate; await route.continue(); });
  const save = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith('/api/digest/catch-up'));
  await page.locator('#digest-mark-caught-up').click();
  await arrived;
  const external = JSON.parse(bd(['create', '--title', 'External activity during checkpoint POST', '--type', 'task', '--json']));
  release();
  assert.equal((await save).status(), 200);
  await page.unroute('**/api/digest/catch-up');
  await page.waitForURL('**/catch-up?window=since_caught_up');
  const persisted = await page.evaluate(async () => (await (await fetch('/api/digest')).json()));
  assert.equal(Date.parse(persisted.lastCaughtUpAt), Date.parse(displayedCutoff));
  assert.equal(await page.locator('#digest-body').isVisible(), false);
  assert.ok(snapshot.metrics);
  await page.locator('#digest-toggle').click();
  await page.locator(`[data-bead-id="${external.id}"]`).waitFor();
  // External tracker changes converge automatically without moving focus or saving a bookmark.
  await page.locator('#digest-toggle').focus();
  const later = JSON.parse(bd(['create', '--title', 'External automatic digest update', '--type', 'task', '--json']));
  await page.locator(`[data-bead-id="${later.id}"]`).waitFor();
  assert.equal(await page.evaluate(() => document.activeElement.id), 'digest-toggle');
  const unchanged = await page.evaluate(async () => (await (await fetch('/api/digest')).json()));
  assert.equal(Date.parse(unchanged.lastCaughtUpAt), Date.parse(displayedCutoff));

  let data = structuredClone(busy);
  let failRead = false;
  let failSave = false;
  let invalidSave = false;
  let posted;
  await digestFixture(page, () => ({data, fail: failRead}));
  await page.route('**/api/digest/catch-up', async route => {
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
  await page.goto(url + '/projects/qa/catch-up', { waitUntil: 'networkidle' });
  const refresh = () => refreshDigest(page);
  const count = async (selector, expected) => assert.equal(await page.locator(selector).count(), expected);
  const noOverflow = async () => assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth), true);
  const height = await page.locator('#catch-up').evaluate(element => element.getBoundingClientRect().height);
  console.log(`Collapsed busy desktop digest: ${height}px`);
  assert.ok(height < 260);
  await page.screenshot({ path: `${output}/busy-desktop.png`, fullPage: false });
  await noOverflow();
  await page.locator('#digest-toggle').focus();
  await page.keyboard.press('Enter');
  await count('#digest-threads > article', 5);
  await count('#digest-threads .digest-item-row', 0);
  await page.locator('#digest-threads > article:first-child .digest-activity-toggle').click();
  await count('#digest-threads > article:first-child .digest-thread-items > .digest-item-row', 4);
  await count('#digest-standalone > .digest-item-row', 6);
  await count('[data-bead-id="child-0-0"] .digest-inline-toggle', 0);
  await count('[data-bead-id="child-0-1"] .digest-summary.is-clamped', 1);
  const threadMore = page.getByRole('button', { name: 'Show 4 more in thread', exact: true }).first();
  await threadMore.focus();
  await page.keyboard.press('Space');
  await count('#digest-threads > article:first-child .digest-thread-items > .digest-item-row', 8);
  assert.equal(await page.evaluate(() => document.activeElement.textContent), 'Show fewer in thread');
  await refresh();
  assert.equal(await page.evaluate(() => document.activeElement.textContent), 'Show fewer in thread');
  await count('#digest-threads > article:first-child .digest-thread-items > .digest-item-row', 8);
  assert.equal(await page.locator('#digest-threads > article:first-child .digest-summary').first().getAttribute('class'), 'digest-summary is-clamped');
  const summary = page.locator('#digest-threads > article:first-child .digest-thread-activity > .digest-item-row .digest-inline-toggle');
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
  await page.waitForTimeout(200);
  await count('#digest-threads .digest-item-row', 0);
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
  // A newly requested filter failing its first read keeps the prior successful
  // resource visible, with explicit old-window context and the filter focused.
  await page.locator('#digest-toggle').click();
  const retained = await page.locator('#catch-up').getAttribute('data-generated-at');
  await page.locator('[data-digest-window="30d"]').click();
  await refresh();
  assert.equal(await page.locator('#catch-up').getAttribute('data-generated-at'), retained);
  await count('#digest-threads > article', 5);
  assert.match(await page.locator('#digest-status').textContent(), /last confirmed/);
  assert.equal(await page.evaluate(() => document.activeElement.dataset.digestWindow), '30d');
  await page.screenshot({ path: `${output}/failed-window-desktop.png`, fullPage: false });
  await page.locator('#digest-toggle').click();
  failRead = false;
  await refresh();
  await page.waitForTimeout(200);
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
  // A late successful save must not overwrite a filter chosen while it waited.
  data = {...structuredClone(busy), generatedAt: '2026-11-12T12:00:01Z'};
  await refresh();
  let postedEntered; let finishPost;
  const postArrived = new Promise(resolve => postedEntered = resolve);
  const holdPost = new Promise(resolve => finishPost = resolve);
  const delayedPost = async route => { postedEntered(); await holdPost; await route.fallback(); };
  await page.route('**/api/digest/catch-up', delayedPost);
  await page.locator('#digest-mark-caught-up').click();
  await postArrived;
  await page.locator('[data-digest-window="7d"]').click();
  const lateSave = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith('/api/digest/catch-up'));
  finishPost(); await lateSave;
  await page.waitForFunction(() => !document.querySelector('#digest-mark-caught-up').disabled);
  assert.equal(new URL(page.url()).searchParams.get('window'), '7d');
  assert.equal(await page.locator('#digest-body').isVisible(), true);
  await page.unroute('**/api/digest/catch-up', delayedPost);
  data = { ...empty, lastCaughtUpAt: posted, isCaughtUp: true, window: 'since_caught_up' };
  await refresh();
  assert.equal(await page.locator('#digest-caught-up-bar').isVisible(), true);
  await page.locator('#digest-view-24h').click();
  await page.waitForTimeout(200);
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
  await page.locator('#digest-threads > article:first-child .digest-activity-toggle').focus();
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
    data = { ...empty, isCaughtUp: false };
    await refresh();
    await page.locator('#digest-toggle').click();
    await noOverflow();
    await page.screenshot({ path: `${output}/empty-${width}.png`, fullPage: false });
  }
  // Deliberate HTTP error fixtures emit network console errors; all other errors fail QA.
  assert.deepEqual(errors.filter(error => !/Failed to load resource.*(503|409)/.test(error)), []);
  console.log('Digest QA passed: actual checkpoint persistence, collapsed activity and 5/4/6 bounds, summary disclosure, focus, windows, failures, newer activity and responsive screenshots.');
} finally {
  await browser.close();
}
