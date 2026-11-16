import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';

const base = process.env.NIGHTWEAVER_QA_URL;
assert(base, 'Set the exact discovered NIGHTWEAVER_QA_URL');
const output = process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/status-stability';
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [];
  const responses = [];
  let version;
  let epoch;
  let sourceHealth = 'fresh';
  await page.addInitScript(() => {
    const Native = window.EventSource;
    window.operationStreams = [];
    window.EventSource = class extends Native {
      constructor(url, options) {
        super(url, options);
        if (String(url).includes('/api/operations/')) window.operationStreams.push(this);
      }
    };
  });
  await page.route('**/api/operations/v1/projects/qa/resources?**', async route => {
    if (new URL(route.request().url()).searchParams.get('key') !== 'factory') return route.continue();
    const response = await route.fetch(); const value = await response.json();
    epoch = value.epoch;
    if (version) {
      value.revision = String(version);
      value.health = sourceHealth;
      value.error = sourceHealth === 'fresh' ? null : { code: 'fixture', message: 'Source observation failed', retryable: true };
      await new Promise(resolve => setTimeout(resolve, 500));
    }
    await route.fulfill({ response, json: value });
  });
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', async response => {
    if (!response.url().includes('/resources?')) return;
    const value = await response.json().catch(() => null);
    if (value) responses.push({ key: value.key, revision: value.revision, health: value.health, error: value.error?.code });
  });
  await page.goto(base + '/projects/qa/overview');
  await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'live');
  await page.evaluate(() => {
    window.statusChanges = [];
    const capture = () => {
      const state = document.querySelector('#factory-status')?.dataset.state;
      if (window.statusChanges.at(-1)?.state !== state) window.statusChanges.push({ state, at: performance.now() });
    };
    capture();
    new MutationObserver(capture).observe(document.body, { subtree: true, childList: true, attributes: true, characterData: true });
  });
  // Drive the real coordinator with frequent notifications while replacement
  // HTTP responses remain in flight. Source content stays genuinely live.
  for (let i = 0; i < 8; i++) {
    version = 1000000 + i;
    await page.evaluate(frame => window.operationStreams.at(-1).dispatchEvent(
      new MessageEvent('invalidation', { data: JSON.stringify(frame) })), {
      schema: 'nightweaver-operations-v1', projectId: 'qa', epoch, versions: { factory: String(version) },
    });
    await page.waitForTimeout(800);
  }
  const changes = await page.evaluate(() => window.statusChanges);
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 1000 });
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({ path: `${output}/overview-${width}.png`, fullPage: true });
  }
  assert.deepEqual(changes.map(change => change.state), ['live'], 'Healthy current status must stay live between updates');
  sourceHealth = 'stale';
  await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'stale');
  sourceHealth = 'fresh';
  await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'live');
  await page.evaluate(() => window.operationStreams.at(-1).dispatchEvent(new Event('error')));
  await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'stale');
  await page.screenshot({ path: `${output}/disconnected-720.png`, fullPage: true });
  assert.deepEqual(errors, []);
  const result = { changes, responses, errors, checks: { stableUpdates: true, authoritativeStale: true, recovery: true, disconnected: true } };
  await writeFile(`${output}/evidence.json`, JSON.stringify(result, null, 2));
  console.log(JSON.stringify({ changes, errors, checks: result.checks }));
} finally {
  await browser.close();
}
