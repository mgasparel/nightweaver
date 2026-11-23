// Fresh exact isolated AppHost; real disposable tracker -> shared collector -> browser.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const base = process.env.NIGHTWEAVER_QA_URL;
const configPath = process.env.NIGHTWEAVER_QA_CONFIG;
assert(base && configPath, 'Use the exact discovered URL and config');
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
const create = title => JSON.parse(bd(['create', '--title', title, '--type', 'task', '--json'])).id;
const add = (id, text) => {
  bd(['comments', 'add', id, text]);
  return JSON.parse(bd(['comments', id, '--json'])).at(-1).id.toString();
};
const head = 'a'.repeat(40), commit = 'b'.repeat(40), pr = 'https://github.com/owner/repo/pull/42';
const agent = `nightweaver-merge-v1 pull_request=${pr} checked_head=${head} required_checks=passed merge_method=squash merge_commit=${commit}`;
const verified = source => `nightweaver-verified-merge-v1 pull_request=${pr} checked_head=${head} merge_commit=${commit} required_checks=passed source_comment=${source}`;
const merged = create('Verified delivery with retained cleanup warning');
const source = add(merged, agent);
add(merged, verified(source));
add(merged, 'nightweaver-finalization-v1 Verified merge closure completed; cleanup=failed: locked report; Product completion=safely ineligible.');
bd(['update', merged, '--status', 'closed', '--if-status', 'open', '--if-assignee', '']); // Same closure path as the host.
assert.equal(JSON.parse(bd(['show', merged, '--json']))[0].close_reason ?? '', '');
const ordinary = create('Ordinary commit note is not merge verification');
bd(['update', ordinary, '--notes', 'Proposed commit abc1234 and PR #7']);
add(ordinary, agent); bd(['close', ordinary]);
const late = create('Comment-only host evidence arrives after closure');
const lateSource = add(late, agent); bd(['update', late, '--status', 'closed']);
const noop = create('Legitimate duplicate closure'); bd(['close', noop, '--reason', 'duplicate; no fix claimed']);
const malformed = create('Matching issue URLs do not establish a merged pull request');
const issueUrl = 'https://github.com/owner/repo/issues/42';
const malformedSource = add(malformed, agent.replace(pr, issueUrl));
add(malformed, verified(malformedSource).replace(pr, issueUrl));
bd(['update', malformed, '--status', 'closed']);
const output = path.resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/digest-merge');
fs.mkdirSync(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const errors = [], external = [], posts = [];
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  let held = true;
  await page.route('**/api/operations/v1/projects/qa/resources?**', async route => {
    if (!new URL(route.request().url()).searchParams.get('key')?.startsWith('digest-merges:') || !held) return route.continue();
    const response = await route.fetch();
    const body = await response.json();
    body.data = null;
    await route.fulfill({response, json: body});
  });
  page.setDefaultTimeout(90000);
  page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => {
    if (new URL(r.url()).origin !== new URL(base).origin) external.push(r.url());
    if (r.method() === 'POST') posts.push(r.url());
  });
  const baseStarted = performance.now();
  await page.goto(`${base}/projects/qa/catch-up?window=all`);
  if (await page.locator('#digest-toggle').getAttribute('aria-expanded') === 'false') await page.locator('#digest-toggle').click();
  const row = id => page.locator(`.digest-item-row[data-bead-id="${id}"]`);
  await row(merged).getByText('Merge verification loading.', {exact: true}).waitFor();
  assert.match(await row(merged).innerText(), /Closed/);
  const baseReadyMs = performance.now() - baseStarted;
  const countBefore = await page.locator('#digest-metric-completed').innerText();
  await page.screenshot({path: path.join(output, 'digest-base-before-evidence.png'), fullPage: true});
  const competing = await (await page.request.get(`${base}/api/operations/v1/projects/qa/resources?key=${encodeURIComponent(`section:${late}:history`)}`)).json();
  assert.equal(competing.projectId, 'qa');
  await page.goto(`${base}/projects/qa/overview`);
  await page.locator('.digest-outcomes li').first().waitFor();
  assert.match(await page.locator('.digest-outcomes').innerText(), /Closed/);
  assert.doesNotMatch(await page.locator('.digest-outcomes').innerText(), /Merged/);
  await page.screenshot({path: path.join(output, 'overview-base-before-evidence.png'), fullPage: true});
  held = false;
  const evidenceStarted = performance.now();
  await page.goto(`${base}/projects/qa/catch-up?window=all`);
  if (await page.locator('#digest-toggle').getAttribute('aria-expanded') === 'false') await page.locator('#digest-toggle').click();
  await page.locator('#digest-refresh').click();
  await row(merged).getByRole('link', { name: 'Merged pull request', exact: true }).waitFor();
  const evidenceReadyMs = performance.now() - evidenceStarted;
  assert.equal(await page.locator('#digest-metric-completed').innerText(), countBefore);
  assert.equal(await row(merged).getByRole('link', { name: 'Merged pull request' }).getAttribute('href'), pr);
  assert.equal(await row(merged).getByRole('link', { name: commit.slice(0, 7) }).getAttribute('href'), `https://github.com/owner/repo/commit/${commit}`);
  assert.match(await row(merged).innerText(), /Cleanup needs attention: locked report/);
  await row(ordinary).getByText('Resolved', {exact: true}).waitFor();
  assert.equal(await row(ordinary).getByRole('link', { name: 'Merged pull request', exact: true }).count(), 0);
  await row(malformed).getByText('Merge verification invalid.', { exact: true }).waitFor();
  assert.match(await row(malformed).innerText(), /Resolved/);
  assert.equal(await row(malformed).getByRole('link', { name: 'Merged pull request', exact: true }).count(), 0);
  assert.match(await row(noop).innerText(), /No-Op \/ Duplicate/);
  await row(late).getByText('No host merge verification recorded.', { exact: true }).waitFor();
  add(late, verified(lateSource)); // Journal invalidation, no refresh command or reload.
  await row(late).getByRole('link', { name: 'Merged pull request', exact: true }).waitFor();
  // The full-response consumer has independent admission. It reuses the same
  // compact evidence once its own complete resource has been observed.
  const legacyDeadline = performance.now() + 30000;
  let legacyObservation;
  do {
    legacyObservation = await (await page.request.get(`${base}/api/operations/v1/projects/qa/resources?key=digest:all`)).json();
    if (legacyObservation.health === 'fresh' && !legacyObservation.synchronization?.pending) break;
    await page.waitForTimeout(100);
  } while (performance.now() < legacyDeadline);
  assert.equal(legacyObservation.health, 'fresh');
  const legacy = await (await page.request.get(`${base}/api/digest?window=all`)).json();
  assert.equal(legacy.standaloneCompleted.find(item => item.id === merged).outcome, 'merged');
  assert.equal(legacy.standaloneCompleted.find(item => item.id === noop).outcome, 'no_op');
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 1100 });
    await page.locator('#digest-toggle').focus();
    await row(merged).scrollIntoViewIfNeeded();
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `overflow at ${width}`);
    await page.screenshot({ path: path.join(output, `digest-merge-${width}.png`), fullPage: true });
  }
  assert.deepEqual(errors, []); assert.deepEqual(external, []);
  assert(posts.every(url => url.endsWith('/refresh')), 'Only explicit read refresh requests are allowed');
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ passed: true, merged, ordinary, late, noop, malformed, baseReadyMs, evidenceReadyMs,
    checks: ['native empty closure and producer protocol', 'exact safe PR/commit links', 'matching non-PR URLs stay invalid/resolved', 'separate cleanup warning', 'ordinary SHA/agent-only stays resolved', 'non-code duplicate', 'automatic comments-only refresh', 'legacy shared evidence reuse', '1440/375/720 reflow', 'no browser mutations or external requests'], errors }, null, 2));
} finally { await browser.close(); }
