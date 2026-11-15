// Exact isolated AppHost only. Disposable native inventory/dependency changes,
// real shared observations, and a retained-source-health presentation fixture.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const base = process.env.NIGHTWEAVER_QA_URL;
const configPath = process.env.NIGHTWEAVER_QA_CONFIG;
assert(base && configPath, 'Use the exact discovered URL/config');
const root = path.dirname(path.resolve(configPath));
assert(path.basename(root).startsWith('nightweaver-qa-'));
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
const repo = path.resolve(config.target_repository.path);
assert.equal(repo, path.join(root, 'repo')); assert.equal(config.name, 'qa');
const env = { ...process.env };
for (const key of Object.keys(env)) if (/^(BEADS_|BD_|GIT_)/.test(key)) delete env[key];
for (const key of ['HOME', 'USERPROFILE', 'APPDATA', 'XDG_CONFIG_HOME']) env[key] = path.join(root, 'home');
Object.assign(env, { GIT_CONFIG_GLOBAL: path.join(root, 'home', 'gitconfig'), GIT_CONFIG_NOSYSTEM: '1',
  BD_DISABLE_METRICS: '1', BD_NO_HOOKS: 'true', BD_DOLT_AUTO_START: 'false', OTEL_SDK_DISABLED: 'true' });
for (const key of ['BD_IMPORT_AUTO', 'BD_EXPORT_AUTO', 'BD_BACKUP_ENABLED', 'BD_DOLT_AUTO_PUSH', 'BD_EVENTS_EXPORT']) env[key] = 'false';
const bd = args => execFileSync('bd', ['--sandbox', ...args], { cwd: repo, env, encoding: 'utf8', timeout: 30000 });
const output = path.resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/studio-explorer');
fs.mkdirSync(output, { recursive: true });
const rows = Array.from({ length: 81 }, (_, i) => ({ id: `qa-studio-explorer-${String(i).padStart(3, '0')}`,
  title: `Studio Explorer item ${String(i).padStart(3, '0')} · A carefully scoped delivery task`, issue_type: 'task',
  status: 'open', priority: 2, labels: [], created_at: '2026-11-14T00:00:00Z', updated_at: '2026-11-14T00:00:00Z' }));
const input = path.join(root, 'studio-explorer.jsonl');
fs.writeFileSync(input, rows.map(row => JSON.stringify(row)).join('\n'));
bd(['import', input]);
const browser = await chromium.launch({ headless: true });
const checks = []; const errors = []; const requests = [];
const mark = text => { checks.push(text); console.log(text); };
let page;
try {
  page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  page.setDefaultTimeout(60000);
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => requests.push(new URL(request.url()).pathname));
  const list = page.locator('.explorer-list li');
  const search = page.getByRole('searchbox', { name: 'Search Beads', exact: true });
  const submit = page.getByRole('button', { name: 'Search project', exact: true });
  const view = page.getByRole('combobox', { name: 'View', exact: true });
  const waitText = text => page.locator('.explorer-pagination').getByText(text, { exact: true }).waitFor({ timeout: 90000 });
  const capture = async name => {
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 1000 });
      assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `${name}-${width}.png`), fullPage: false });
    }
    await page.setViewportSize({ width: 1440, height: 1000 });
  };
  await page.goto(base + '/projects/qa/beads');
  assert.equal(await view.inputValue(), 'ready');
  await search.fill('Studio Explorer'); await view.selectOption('all'); await submit.click();
  await waitText('81 matching Beads · Page 1 of 3');
  assert.equal(await list.count(), 40);
  const bounded = await page.evaluate(async () => (await (await fetch('/api/operations/v1/projects/qa/resources?key=explorer:all:U3R1ZGlvIEV4cGxvcmVy:1')).json()).data);
  assert.equal(bounded.items.length, 40); assert.equal(bounded.total, 81);
  await capture('inventory');
  await page.getByRole('link', { name: 'Next page', exact: true }).click();
  await waitText('81 matching Beads · Page 2 of 3');
  assert.equal(await list.count(), 40);
  await page.goBack(); await waitText('81 matching Beads · Page 1 of 3');
  await page.goForward(); await waitText('81 matching Beads · Page 2 of 3');
  await page.reload(); await waitText('81 matching Beads · Page 2 of 3');
  await search.focus();
  bd(['update', rows[40].id, '--title', 'Studio Explorer externally renamed']);
  await page.getByRole('link', { name: 'Studio Explorer externally renamed', exact: true }).waitFor();
  assert(await search.evaluate(node => node === document.activeElement));
  assert(new URL(page.url()).searchParams.get('page') === '2');
  mark('bounded 40-row pages, stable ID order, URL reload/back/forward, external title and retained page/focus');
  const selected = list.first().locator('.explorer-title');
  const selectedHref = await selected.getAttribute('href');
  await selected.click();
  await page.locator('.bead-facts').waitFor({ timeout: 90000 });
  assert.equal(new URL(page.url()).pathname, new URL(selectedHref, base).pathname);
  await page.getByRole('link', { name: 'Back to Explorer' }).click();
  await waitText('81 matching Beads · Page 2 of 3');
  await page.getByRole('link', { name: 'Next page', exact: true }).click();
  await waitText('81 matching Beads · Page 3 of 3');
  await list.first().locator('.explorer-title').focus();
  bd(['update', rows[80].id, '--title', 'Removed from current search']);
  await waitText('80 matching Beads · Page 2 of 2');
  await page.getByText('Page 3 no longer exists.', { exact: false }).waitFor();
  assert.equal(new URL(page.url()).searchParams.get('page'), '2');
  assert(await page.locator('h1').evaluate(node => node === document.activeElement));
  await capture('clamped');
  mark('Workspace exact identity/context; current-page removal clamps URL with accessible explanation and safe focus');
  bd(['dep', 'add', rows[0].id, rows[1].id]);
  await view.selectOption('blocked'); await submit.click();
  await waitText('1 matching Beads · Page 1 of 1');
  assert((await list.locator('.explorer-title').getAttribute('href')).includes(rows[0].id));
  bd(['dep', 'remove', rows[0].id, rows[1].id]);
  await waitText('0 matching Beads · Page 1 of 1');
  for (const filter of ['active', 'closed']) {
    await view.selectOption(filter); await submit.click(); await waitText('0 matching Beads · Page 1 of 1');
  }
  mark('native dependency removal and Active/Closed empty views');
  await view.selectOption('ready'); await submit.click();
  await page.getByRole('button', { name: 'Refresh view', exact: true }).click();
  await waitText('80 matching Beads · Page 1 of 2');
  mark('all five views, native external dependency/readiness membership and empty results');
  await page.getByRole('searchbox', { name: 'Search current project', exact: true }).fill('externally renamed');
  await page.getByRole('button', { name: 'Search', exact: true }).click();
  await waitText('1 matching Beads · Page 1 of 1');
  assert.equal(new URL(page.url()).searchParams.get('view'), 'all');
  await page.getByRole('button', { name: 'All projects', exact: false }).click();
  await page.getByRole('dialog').waitFor(); await page.keyboard.press('Escape');
  assert(!requests.some(url => /global|cross-project/.test(url)));
  mark('header search uses current project across all views; deferred cross-project control makes no unsupported request');
  // Source health fixture keeps the real envelope identity, coordinator and live stream.
  await page.route('**/resources?*', async route => {
    const response = await route.fetch(); const json = await response.json();
    if (json.key?.startsWith('explorer:') && json.data) {
      json.health = 'stale'; json.error = { code: 'source-timeout', message: 'Tracker timed out; retained results.', retryable: true };
    }
    await route.fulfill({ response, json });
  });
  await page.getByRole('button', { name: 'Refresh view', exact: true }).click();
  await page.getByText('Tracker timed out; retained results.', { exact: true }).first().waitFor();
  assert.equal(await list.count(), 1); await capture('retained');
  mark('stale retained result remains visibly historical at all three layouts');
  await page.unroute('**/resources?*');
  await page.goto(base + '/projects/qa/beads?view=all&page=01');
  await page.getByRole('alert').filter({ hasText: 'Invalid Explorer query' }).waitFor();
  assert.equal(await list.count(), 0);
  assert.equal(errors.length, 0, errors.join('\n'));
  mark('malformed queries render recovery without an invalid observation request; no page errors');
  fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify({ checks, errors }, null, 2));
} catch (error) {
  console.error(errors); console.error(await page?.locator('body').innerText());
  await page?.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }); throw error;
} finally { await browser.close(); }
