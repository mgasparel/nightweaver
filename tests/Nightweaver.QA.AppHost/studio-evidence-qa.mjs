// Exact isolated AppHost only. Real browser POSTs, disposable external bd changes,
// and deterministic read projections for bounded-history/source-state edge cases.
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

const output = path.resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/studio-evidence');
fs.mkdirSync(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.setDefaultTimeout(90000);
  const errors = [], external = [], posts = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => {
    if (new URL(r.url()).origin !== new URL(base).origin) external.push(r.url());
    if (r.method() === 'POST') posts.push(r.url());
  });
  const bead = JSON.parse(bd(['create', '--title', 'Native structured evidence navigation', '--type', 'task', '--json']));
  const add = text => { bd(['comments', 'add', bead.id, typeof text === 'string' ? text : JSON.stringify(text)]); return JSON.parse(bd(['comments', bead.id, '--json'])).at(-1).id.toString(); };
  const candidate = add('Old candidate source, outside the initial eight comments.');
  const review = add('Old review source.');
  for (let i = 0; i < 9; i++) add(`Ordinary intervening note ${i}`);
  const pr = 'https://example.invalid/pull/53';
  const hash = 'a'.repeat(40);
  const rejection = url => ({ schema: 'nightweaver-merger-rejection-v1', kind: 'design', summary: 'Native rejection evidence', subject: 'Evidence parity', details: 'Inspect explicit evidence', pull_request: url, checked_head: hash });
  const rejected = add(rejection(pr));
  const verified = add(`nightweaver-verified-merge-v1 pull_request=${pr} checked_head=${hash} merge_commit=${hash} required_checks=passed source_comment=${candidate}`);
  const approval = add({ schema: 'nightweaver-planning-outcome-v1', bead_id: bead.id, role: 'plan_challenger', outcome: 'Approved', provider: 'standard-agent-cli', invocation_id: 'fixture', invocation_log: 'H:/fixture/log', classification_diagnostic: 'Exact source identity',
    planning_approval: { plan_comment_id: candidate, plan_revision: 'd'.repeat(64), review_comment_id: review, review_revision: 'e'.repeat(64), grounding: { checkout_revision: hash, default_revision: hash, product_inputs: 'c'.repeat(64) } } });
  const unsafe = ['javascript:alert(1)', 'file:///H:/private', 'https://user:secret@example.invalid/'];
  const unsafeIds = unsafe.map(url => add(rejection(url)));
  const missing = add(`nightweaver-verified-merge-v1 pull_request=${pr} checked_head=${hash} merge_commit=${hash} required_checks=passed source_comment=missing-source`);
  const native = await (await page.request.get(`${base}/api/beads/${bead.id}`)).json();
  for (const id of [rejected, verified, approval, ...unsafeIds, missing]) assert.equal(native.comments.find(c => c.id === id).message.state, 'supported');
  assert(native.comments.find(c => c.id === approval).message.sections.some(s => s.commentId === candidate));
  await page.goto(`${base}/projects/qa/beads/${bead.id}?tab=history`);
  const comment = id => page.locator(`.bead-comment[data-comment-id="${id}"]`);
  const focusedSource = async id => {
    await page.waitForFunction(id => {
      const n = [...document.querySelectorAll('.bead-comment')].find(n => n.dataset.commentId === id);
      return n?.open && document.activeElement === n.querySelector(':scope > summary');
    }, id);
  };
  await comment(verified).waitFor();
  assert.equal(await comment(candidate).count(), 0);
  for (const id of [rejected, verified]) {
    await comment(id).locator(':scope > summary').click();
    const field = comment(id).locator('details').filter({ has: page.locator('summary', { hasText: /^Pull request$/ }) });
    if (await field.count()) await field.locator('summary').click();
    const link = comment(id).getByRole('link', { name: pr, exact: true });
    assert.equal(await link.getAttribute('href'), pr);
    assert.equal(await link.getAttribute('rel'), 'noopener noreferrer');
    assert.equal(await link.getAttribute('target'), '_blank');
    await link.focus();
  }
  const sourceField = comment(verified).locator('details').filter({ has: page.locator('summary', { hasText: /^Source comment$/ }) });
  await sourceField.locator('summary').click();
  const sourceLink = sourceField.getByRole('link', { name: `View source comment ${candidate}`, exact: true });
  await sourceLink.focus(); await page.keyboard.press('Enter');
  await comment(candidate).locator(':scope > summary').waitFor();
  await focusedSource(candidate);
  // Real external tracker refresh must retain focus, source and disclosures.
  add('External update while reading source');
  await page.getByText(`${native.comments.length + 1} comments`, { exact: false }).waitFor();
  await focusedSource(candidate);
  assert(await sourceField.evaluate(n => n.open));
  await comment(candidate).locator(':scope > summary').click();
  await sourceLink.focus(); await page.keyboard.press('Enter');
  await focusedSource(candidate);
  await comment(approval).locator(':scope > summary').click();
  for (const [label, id] of [['Candidate comment', candidate], ['Review comment', review]]) {
    const field = comment(approval).locator('details').filter({ has: page.locator('summary', { hasText: new RegExp(`^${label}$`) }) });
    await field.locator('summary').click();
    await field.getByRole('link', { name: `View source comment ${id}`, exact: true }).focus();
    await page.keyboard.press('Enter');
    await focusedSource(id);
  }
  for (const id of unsafeIds) {
    await comment(id).locator(':scope > summary').click();
    assert.equal(await comment(id).getByRole('link').count(), 0);
  }
  await comment(missing).locator(':scope > summary').click();
  await comment(missing).getByText('Source comment', { exact: true }).click();
  await comment(missing).getByText('Source comment missing-source is unavailable in this history.', { exact: true }).waitFor();
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    await comment(approval).scrollIntoViewIfNeeded();
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({ path: path.join(output, `evidence-${width}.png`), fullPage: true });
  }
  assert.deepEqual(errors, []); assert.deepEqual(external, []); assert.deepEqual(posts, []);
  const result = { passed: true, checks: ['actual native merger rejection, verified merge and Planning approval projections', 'bare safe evidence links', 'keyboard source reveal outside eight recent comments', 'repeat activation', 'external refresh focus/disclosure retention', 'explicit missing source', 'unsafe and credential URL rejection', 'no automatic external requests or commands', '1440/375/720 bounded reflow'], errors, external, posts };
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
} finally { await browser.close(); }
