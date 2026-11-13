import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { chromium } from 'playwright';

const baseUrl = process.env.NIGHTWEAVER_QA_URL;
if (!baseUrl) throw new Error('Set NIGHTWEAVER_QA_URL to the URL from aspire describe.');
const artifacts = `../../TestResults/planning-readiness-${Date.now()}`;
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
const errors = [];
page.on('pageerror', error => errors.push(error.message));
page.on('console', message => {
  // The deliberate stale request below produces Chromium's standard HTTP error.
  if (message.type() === 'error' && !message.text().includes('409 (Conflict)')) errors.push(message.text());
});
try {
  await page.goto(baseUrl);
  const beads = await page.evaluate(async () => (await fetch('/api/beads')).json());
  const product = beads.find(bead => bead.title === 'QA workspace markdown');
  assert.ok(product);
  await page.goto(`${baseUrl}/#bead/${encodeURIComponent(product.id)}`);
  const action = page.getByRole('button', { name: 'Ready for Planning', exact: true });
  await action.waitFor({ state: 'visible' });
  assert.equal(await action.isEnabled(), true);
  const initial = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), product.id);
  assert.equal(initial.planning.ready, false);
  await mkdir(artifacts, { recursive: true });
  await page.screenshot({ path: `${artifacts}/planning-backlog-desktop.png`, fullPage: true });
  await page.route('**/ready-for-planning', route => route.continue({
    postData: JSON.stringify({ expected: '0'.repeat(64) })
  }), { times: 1 });
  const staleResponse = page.waitForResponse(response => response.url().endsWith('/ready-for-planning') && response.request().method() === 'POST');
  await action.click();
  const stale = (await staleResponse).status();
  assert.equal(stale, 409);
  await page.waitForFunction(() => document.querySelector('#workspace-planning')?.textContent.includes('Refresh'));
  assert.equal(await action.isEnabled(), false);
  const unchanged = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), product.id);
  assert.equal(unchanged.planning.ready, false);
  await page.locator('#workspace-refresh').click();
  await page.waitForFunction(() => !document.querySelector('#workspace-planning button')?.disabled);
  const resultPromise = page.waitForResponse(response => response.url().endsWith('/ready-for-planning') && response.request().method() === 'POST');
  await action.click();
  const result = await resultPromise;
  assert.equal(result.status(), 200, await result.text());
  await page.waitForFunction(() => document.querySelector('#workspace-planning')?.textContent.includes('Ready for Planning')
    && !document.querySelector('#workspace-planning button'));
  await page.reload();
  await page.waitForFunction(() => document.querySelector('#workspace-planning')?.textContent.includes('Ready for Planning'));
  const persisted = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), product.id);
  assert.equal(persisted.planning.ready, true);
  assert.ok(persisted.bead.labels.includes('ready-for-planning'));
  await page.screenshot({ path: `${artifacts}/planning-readiness-desktop.png`, fullPage: true });
  await page.setViewportSize({ width: 375, height: 850 });
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), true);
  await page.screenshot({ path: `${artifacts}/planning-readiness-mobile.png`, fullPage: true });
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({ product: product.id, stale, authorized: result.status(), persisted: persisted.planning.ready, errors, artifacts }));
} finally {
  await browser.close();
}
