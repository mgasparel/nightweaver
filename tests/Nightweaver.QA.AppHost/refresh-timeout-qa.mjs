// Run against the exact isolated AppHost described in README.md.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright';

const base = process.env.NIGHTWEAVER_QA_URL;
assert(base, 'Set the direct URL discovered from the exact isolated AppHost');
const output = path.resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/refresh-timeout');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 375, height: 1000 } });
  const errors = []; const posts = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (request.method() === 'POST') posts.push(new URL(request.url()).pathname); });
  await page.goto(base + '/studio');
  await page.waitForFunction(() => /^\d+/.test(document.querySelector('#attention-navigation-count')?.textContent.trim() ?? ''), null, { timeout: 90000 });
  await page.evaluate(() => {
    const nativeFetch = window.fetch.bind(window);
    window.refreshProbe = { started: 0, aborted: false, releases: [] };
    window.fetch = async (url, options) => {
      if (!String(url).endsWith('/operations/v1/projects/qa/refresh')) return nativeFetch(url, options);
      const probe = window.refreshProbe; const index = probe.started++;
      if (index > 1) return nativeFetch(url, options);
      if (index === 0) options.signal.addEventListener('abort', () => { probe.aborted = true; });
      // Hold actual 202 responses, ignoring abort to exercise late settlement too.
      const response = await nativeFetch(url, { ...options, signal: undefined });
      const body = await response.arrayBuffer();
      return new Promise(resolve => { probe.releases[index] = () => resolve(new Response(body, { status: response.status, headers: response.headers })); });
    };
  });
  const refresh = page.locator('.synchronization').getByRole('button', { name: /^Refresh/ });
  await refresh.click();
  await page.waitForFunction(() => window.refreshProbe.releases[0]);
  await page.waitForFunction(() => window.refreshProbe.aborted, null, { timeout: 20000 });
  await page.locator('.synchronization [role="alert"]').filter({ hasText: 'Refresh outcome unknown' }).waitFor();
  await page.locator('.synchronization [role="status"]').filter({ hasText: 'Live connection' }).waitFor();
  assert.equal(posts.length, 1, 'timeout never replays POST');
  assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await page.screenshot({ path: path.join(output, 'refresh-timeout-375.png'), fullPage: true });
  await refresh.click(); await page.waitForFunction(() => window.refreshProbe.releases[1]);
  await page.evaluate(() => window.refreshProbe.releases[0]());
  // Give the old completion its microtasks before exercising replacement deduplication.
  await page.evaluate(() => new Promise(resolve => setTimeout(resolve, 100)));
  await refresh.click(); assert.equal(posts.length, 2);
  await page.evaluate(() => window.refreshProbe.releases[1]());
  await page.locator('.synchronization [role="alert"]').waitFor({ state: 'hidden' });
  assert.deepEqual(errors, []);
  assert.deepEqual(posts, Array(2).fill('/api/operations/v1/projects/qa/refresh'));
  await page.screenshot({ path: path.join(output, 'refresh-recovered-375.png'), fullPage: true });
  console.log(JSON.stringify({ timeoutAborted: true, explicitAttempts: 2, posts, errors }));
} finally { await browser.close(); }
