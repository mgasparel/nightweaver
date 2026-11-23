// Use the exact disposable AppHost. Hold browser section receipts, then use native writes.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const base = process.env.NIGHTWEAVER_QA_URL;
const configPath = process.env.NIGHTWEAVER_QA_CONFIG;
assert(base && configPath);
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
const output = path.resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/workspace-base');
fs.mkdirSync(output, { recursive: true });
const bead = JSON.parse(bd(['create', '--title', 'Independent Workspace base', '--description', 'Accepted independent text',
  '--type', 'feature', '--labels', 'product-decision-required,stage:plan', '--json']));
bd(['comments', 'add', bead.id, 'Choose the local fixture direction.']);
const browser = await chromium.launch({ headless: true });
const evidence = { checks: [], keys: [], timings: {}, errors: [] };
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.setDefaultTimeout(90000);
  page.on('pageerror', error => evidence.errors.push(error.message));
  let held = true, failedBase = false;
  await page.route('**/resources?*', async route => {
    const key = new URL(route.request().url()).searchParams.get('key');
    evidence.keys.push(key);
    const response = await route.fetch();
    if (key === `base:workspace:${bead.id}` && failedBase) {
      const body = await response.json();
      await route.fulfill({ response, json: { ...body, health: 'stale', error: { code: 'source-failed', message: 'Held base refresh', retryable: true } } });
    } else if (key?.startsWith(`section:${bead.id}:`) && held) {
      const body = await response.json();
      await route.fulfill({ response, json: { ...body, data: null, health: 'unavailable', error: null } });
    } else await route.fulfill({ response });
  });
  const start = performance.now();
  await page.goto(`${base}/projects/qa/beads/${bead.id}?tab=summary`);
  await page.getByRole('heading', { name: 'Independent Workspace base', exact: true }).waitFor();
  await page.locator('.bead-facts').waitFor();
  evidence.timings.summaryMs = performance.now() - start;
  assert.equal(await page.getByText('Accepted independent text', { exact: true }).count(), 0);
  assert.equal(await page.getByText('No comments recorded.', { exact: true }).count(), 0);
  assert.equal(await page.getByRole('button', { name: 'Send & Resume', exact: true }).count(), 0);
  assert(await page.locator('[aria-label="text source"]').innerText().then(text => text.includes('loading')));
  evidence.checks.push('Direct link shows base while all detail receipts are held. Unloaded data is not empty.');
  for (const width of [1440, 720, 375]) {
    await page.setViewportSize({ width, height: 900 });
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({ path: path.join(output, `held-${width}.png`), fullPage: true });
  }
  held = false;
  for (const name of ['text', 'history', 'relations', 'environment']) {
    const source = page.locator(`[aria-label="${name} source"]`).first();
    await source.getByRole('button', { name: /Refresh/ }).click();
  }
  await page.getByText('Accepted independent text', { exact: true }).waitFor();
  const input = page.getByRole('textbox', { name: `Guidance for ${bead.id}` });
  await input.fill('Preserve this draft across section and base changes.');
  const send = page.getByRole('button', { name: 'Send & Resume', exact: true });
  await send.waitFor();
  await page.waitForFunction(() => ![...document.querySelectorAll('button')].find(node => node.textContent === 'Send & Resume')?.disabled);
  const historyStart = performance.now();
  await page.getByRole('link', { name: 'History', exact: true }).focus();
  await page.keyboard.press('Enter');
  await page.getByText('Choose the local fixture direction.', { exact: true }).first().waitFor();
  evidence.timings.historyMs = performance.now() - historyStart;
  const outputStart = performance.now();
  await page.getByRole('link', { name: 'Output', exact: true }).click();
  await page.getByRole('heading', { name: 'Invocation output', exact: true }).waitFor();
  evidence.timings.outputMs = performance.now() - outputStart;
  await page.getByRole('link', { name: 'Summary', exact: true }).click();
  assert.equal(await input.inputValue(), 'Preserve this draft across section and base changes.');
  failedBase = true;
  await page.locator('.bead-freshness').first().getByRole('button', { name: /Refresh/ }).click();
  await page.getByText('Held base refresh', { exact: true }).waitFor();
  assert(await page.locator('.bead-facts').isVisible());
  assert.equal(await page.getByText('This Bead no longer exists. Actions are unavailable.', { exact: true }).count(), 0);
  evidence.checks.push('Base refresh failure keeps readable facts and never confirms absence.');
  failedBase = false;
  bd(['update', bead.id, '--remove-label', 'product-decision-required', '--title', 'Current Workspace title']);
  await page.locator('.bead-freshness').first().getByRole('button', { name: /Refresh/ }).click();
  await page.getByRole('heading', { name: 'Current Workspace title', exact: true }).waitFor();
  await page.waitForFunction(() => ![...document.querySelectorAll('button')].some(node => node.textContent === 'Send & Resume'));
  assert.equal(await input.inputValue(), 'Preserve this draft across section and base changes.');
  evidence.checks.push('Current unpaused base removes action availability and preserves the draft.');
  bd(['delete', bead.id, '--force']);
  await page.locator('.bead-freshness').first().getByRole('button', { name: /Refresh/ }).click();
  await page.getByText('This Bead no longer exists. Actions are unavailable.', { exact: true }).waitFor();
  assert.equal(await input.inputValue(), 'Preserve this draft across section and base changes.');
  assert.equal(await send.count(), 0);
  evidence.checks.push('Confirmed deletion removes current actions and keeps the exact draft.');
  assert(!evidence.keys.some(key => key === `workspace:${bead.id}`));
  assert.deepEqual(evidence.errors, []);
} finally {
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(evidence, null, 2));
  await browser.close();
}
