// Run against the exact isolated AppHost described in README.md.
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';

const base = process.env.NIGHTWEAVER_QA_URL;
assert(base, 'Set the direct URL discovered from the exact isolated AppHost');
const output = process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/runtime-refresh';
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const results = [];
async function until(page, condition, message) {
  const deadline = Date.now() + 15000;
  while (!condition() && Date.now() < deadline) await page.waitForTimeout(100);
  assert(condition(), message);
}
try {
  const context = await browser.newContext();
  const attention = await (await context.request.get(base + '/api/attention')).json();
  const bead = attention.find(row => row.title === 'QA needs human')?.beadId;
  assert(bead, 'default disposable QA fixture has a Workspace Bead');
  await context.close();
  for (const [name, routePath] of [
    ['delivery', '/projects/qa/delivery'],
    ['overview', '/projects/qa/overview'],
    ['output', `/projects/qa/beads/${encodeURIComponent(bead)}?tab=output`],
  ]) {
    const page = await browser.newPage({ viewport: { width: 375, height: 1000 } });
    const errors = []; let reads = 0; let posts = 0; let cachedAttempt;
    let releasePost;
    page.on('pageerror', error => errors.push(error.message));
    await page.route('**/api/operations/v1/projects/qa/resources?**', async route => {
      if (new URL(route.request().url()).searchParams.get('key') !== 'factory') return route.continue();
      const response = await route.fetch(); const envelope = await response.json();
      // Initial subscription may return an unavailable envelope before collection.
      if (!envelope.lastAttemptAt) return route.fulfill({ response, json: envelope });
      cachedAttempt ??= envelope.lastAttemptAt;
      envelope.lastAttemptAt = cachedAttempt;
      reads++;
      await route.fulfill({ response, json: envelope });
    });
    await page.route('**/api/operations/v1/projects/qa/refresh', async route => {
      assert.deepEqual(route.request().postDataJSON(), { keys: ['factory'] });
      posts++;
      // Accepted reconciliation whose collector never publishes a new attempt.
      // Hold the second POST to check that enabled controls still coalesce it.
      if (posts === 2) await new Promise(resolve => { releasePost = resolve; });
      await route.fulfill({ status: 202, json: { accepted: true } });
    });
    try {
      await page.goto(base + routePath);
      if (name === 'overview') {
        assert.equal(await page.locator('#factory-status button').count(), 0);
        await page.locator('.floor-panel').getByRole('link', {name: 'View floor'}).click();
        await page.waitForURL('**/projects/qa/delivery');
      }
      const refresh = page.locator('#factory-status button');
      await refresh.waitFor();
      await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'live');
      const accepted = page.waitForResponse(response => response.url().endsWith('/projects/qa/refresh') && response.status() === 202);
      await refresh.click(); await accepted;
      await page.waitForFunction(() => document.querySelector('#factory-status button')?.textContent === 'Refresh runtime again');
      const afterPost = reads;
      await until(page, () => reads >= afterPost + 3, 'successful equal-attempt cache reads continue across polls');
      assert.equal(posts, 1, 'polling never replays reconciliation');
      assert(await refresh.isEnabled(), 'stalled observation keeps manual retry available');
      assert.equal(await refresh.innerText(), 'Refresh runtime again');
      await refresh.click();
      await until(page, () => !!releasePost, 'explicit retry reaches the reconciliation endpoint');
      assert.equal(posts, 2, 'explicit retry issues a new POST after acceptance');
      await refresh.click();
      await page.waitForTimeout(200);
      assert.equal(posts, 2, 'concurrent equal refreshes share the pending POST');
      releasePost(); releasePost = undefined;
      for (const width of [1440, 375, 720]) {
        await page.setViewportSize({ width, height: 1000 });
        assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
        await page.screenshot({ path: `${output}/${name}-${width}.png`, fullPage: true });
      }
      assert.deepEqual(errors, []);
      results.push({ name, reads, posts, cachedAttempt, errors });
    } finally { releasePost?.(); await page.close(); }
  }
  await writeFile(`${output}/results.json`, JSON.stringify({ passed: true, results }, null, 2));
  console.log(JSON.stringify({ passed: true, results }));
} finally { await browser.close(); }
