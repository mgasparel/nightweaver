// Run against the exact isolated AppHost described in README.md.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

const base = process.env.NIGHTWEAVER_QA_URL;
assert(base, 'Set the direct URL discovered from the exact isolated AppHost');
const output = path.resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/read-timeout');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 375, height: 1000 } });
  const errors = []; const posts = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (request.method() === 'POST') posts.push(new URL(request.url()).pathname); });
  // A cold backend outage must expire even though no handshake/read ever succeeded.
  let outageRequests = 0;
  await page.route('**/api/operations/**', route => {
    outageRequests++;
    return route.fulfill({ status: 503, body: 'Disposable QA cold-start outage' });
  });
  const outageStarted = performance.now();
  await page.goto(base + '/studio');
  await page.locator('.synchronization [role="status"]').filter({ hasText: /^(Connecting|Recovering updates channel) · / }).waitFor();
  await page.locator('.synchronization [role="status"]').filter({ hasText: /^Disconnected · / }).waitFor({ timeout: 35000 });
  const outageElapsedMs = performance.now() - outageStarted;
  assert(outageElapsedMs >= 25000 && outageRequests > 1, 'Retries must not reset the no-evidence deadline');
  await page.screenshot({ path: path.join(output, 'cold-disconnected-375.png'), fullPage: true });
  await page.unroute('**/api/operations/**');
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await page.waitForFunction(() => /^\d+/.test(document.querySelector('#attention-navigation-count')?.textContent.trim() ?? ''), null, { timeout: 90000 });
  await page.locator('.synchronization [role="status"]').filter({ hasText: /^Live connection · / }).waitFor();
  await page.screenshot({ path: path.join(output, 'cold-recovered-375.png'), fullPage: true });
  await page.evaluate(() => {
    const nativeFetch = window.fetch.bind(window);
    window.readProbe = { started: 0, aborted: false, releases: [] };
    window.fetch = async (url, options) => {
      if (!String(url).includes('/operations/v1/projects/qa/resources?key=attention')) return nativeFetch(url, options);
      const probe = window.readProbe;
      const index = probe.started++;
      if (index > 1) return nativeFetch(url, options);
      if (index === 0) options.signal.addEventListener('abort', () => { probe.aborted = true; });
      // Hold a complete real response and deliberately ignore abort, like a late adapter.
      const response = await nativeFetch(url, { ...options, signal: undefined });
      const body = await response.arrayBuffer();
      return new Promise(resolve => { probe.releases[index] = () => resolve(new Response(body, { status: response.status, headers: response.headers })); });
    };
  });
  await page.waitForFunction(() => window.readProbe.releases[0]);
  const refresh = page.waitForResponse(response => response.url().endsWith('/operations/v1/projects/qa/refresh') && response.request().method() === 'POST');
  await page.locator('.synchronization').getByRole('button', { name: /^Refresh/ }).click();
  assert.equal((await refresh).status(), 202);
  await page.evaluate(() => window.dispatchEvent(new Event('focus')));
  await page.locator('.synchronization [role="alert"]').waitFor({ timeout: 20000 });
  await page.waitForFunction(() => window.readProbe.aborted && window.readProbe.releases[1]);
  await page.locator('.synchronization [role="status"]').filter({ hasText: /^Live connection · / }).waitFor();
  assert(!(await page.locator('.synchronization [role="status"]').innerText()).includes('Snapshot stale'),
    'A read timeout does not expire a recent retained snapshot or disconnect the live stream');
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await page.screenshot({ path: path.join(output, 'timeout-375.png'), fullPage: true });
  await page.evaluate(() => window.readProbe.releases[1]());
  await page.locator('.synchronization [role="alert"]').waitFor({ state: 'hidden' });
  try {
    await page.waitForFunction(() => document.querySelector('.synchronization [role="status"]')?.textContent.includes('Current'));
  } catch (error) {
    const response = await page.request.get(base + '/api/operations/v1/projects/qa/resources?key=attention');
    const { health, error: sourceError, pendingAgeMs, synchronization, sources } = await response.json();
    console.log(JSON.stringify({ recoveryFailure: { status: await page.locator('.synchronization').innerText(),
      health, sourceError, pendingAgeMs, synchronization, sources } }));
    await page.screenshot({ path: path.join(output, 'recovery-failed-375.png'), fullPage: true });
    throw error;
  }
  const recovered = await page.locator('#attention-navigation-count').innerText();
  await page.evaluate(() => window.readProbe.releases[0]());
  await page.locator('.synchronization').getByRole('button', { name: /^Refresh/ }).waitFor();
  assert.equal(await page.locator('#attention-navigation-count').innerText(), recovered);
  assert.deepEqual(errors, []);
  assert(posts.length >= 1 && posts.every(value => value === '/api/operations/v1/projects/qa/refresh'),
    'Explicit refresh and focus/wake only reconcile reads; no guarded mutation is replayed');
  await page.screenshot({ path: path.join(output, 'recovered-375.png'), fullPage: true });
  console.log(JSON.stringify({ coldOutage: { outageElapsedMs, outageRequests, recovered: true }, timeoutAborted: true, recovered, posts, errors }));
} finally { await browser.close(); }
