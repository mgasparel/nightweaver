import { overviewChecks } from './overview-checks.mjs';
import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { resolve } from 'node:path';

const url = process.env.NIGHTWEAVER_QA_URL;
assert.ok(url, 'Set NIGHTWEAVER_QA_URL from aspire describe.');
const output = resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/studio');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const errors = [];
const mutations = [];
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  page.on('request', request => { if (!['GET', 'HEAD'].includes(request.method())) mutations.push(request.url()); });
  const { projectId } = await (await page.request.get(`${url}/api/studio/context`)).json();
  const base = `/projects/${encodeURIComponent(projectId)}`;
  await page.goto(url);
  await page.waitForURL(`**${base}/overview`);
  await page.getByRole('heading', { name: `${projectId} overview` }).waitFor();
  await page.reload();
  await page.getByRole('heading', { name: `${projectId} overview` }).waitFor();
  assert.match(await page.locator('.overview-summary').innerText(), /Runtime · (live|idle|stale|stopped|unavailable)/);

  const nav = page.getByRole('navigation', { name: 'Project navigation' });
  for (const [route, label] of [['delivery', 'Delivery floor'], ['attention', 'Attention'], ['catch-up', 'Catch up'], ['beads', 'Beads']]) {
    await nav.getByRole('link', { name: label, exact: true }).focus();
    await page.keyboard.press('Enter');
    await page.waitForURL(`**${base}/${route}`);
    await page.getByRole('heading', { name: label, exact: true, level: 1 }).waitFor();
    // A visible route heading can precede React's navigation focus effect.
    await page.waitForFunction(() => document.querySelector('h1') === document.activeElement);
    assert.equal(await page.locator('h1').evaluate(node => node === document.activeElement), true);
    await page.reload();
    await page.getByRole('heading', { name: label, exact: true, level: 1 }).waitFor();
  }
  await page.getByRole('searchbox', { name: 'Search Beads' }).fill('retained query');
  await page.getByRole('combobox', { name: 'View', exact: true }).selectOption('all');
  await page.getByRole('button', { name: 'Search project' }).click();
  await page.waitForURL('**?q=retained+query&view=all');
  await page.reload();
  assert.equal(await page.getByRole('searchbox', { name: 'Search Beads', exact: true }).inputValue(), 'retained query');
  await nav.getByRole('link', { name: 'Delivery floor' }).click();
  await page.goBack();
  assert.equal(await page.getByRole('searchbox', { name: 'Search Beads', exact: true }).inputValue(), 'retained query');
  await page.goForward();
  await page.getByRole('heading', { name: 'Delivery floor', exact: true, level: 1 }).waitFor();

  for (const [hash, route] of [['factory', 'delivery'], ['attention', 'attention'], ['catch-up', 'catch-up'], ['beads', 'beads'], ['cleanup', 'cleanup']]) {
    await page.goto(`${url}/#${hash}`);
    await page.waitForURL(`**${base}/${route}`);
    assert.equal((await page.reload()).status(), 200);
  }
  // Retire the Overview preview's section interest before selecting two new Workspaces.
  await page.waitForTimeout(31000);
  for (const beadId of ['ns-mbd.1', 'ns-mbd.1.2']) {
    await page.goto(`${url}/#bead/${beadId}`);
    await page.waitForURL(`**${base}/beads/${beadId}`);
    assert.equal((await page.reload()).status(), 200);
    await page.getByRole('heading', { name: `Bead ${beadId}`, exact: true }).waitFor();
    await page.getByRole('link', { name: 'History', exact: true }).click();
    await page.waitForURL(`**${base}/beads/${beadId}?tab=history`);
    assert.equal((await page.reload()).status(), 200);
    await page.getByRole('heading', { name: `Bead ${beadId}`, exact: true }).waitFor();
    await page.locator('.workspace-tabs a[aria-current="page"]').filter({ hasText: 'History' }).waitFor();
  }
  for (const path of ['/api/missing', '/studio/assets/missing.js', `${base}/beads/ns-mbd.1/missing.js`]) {
    assert.equal((await page.request.get(`${url}${path}`)).status(), 404);
  }

  await page.goto(`${url}/studio#bead/qa-%3Cscript%3E`);
  await page.waitForURL(`**${base}/beads/qa-%3Cscript%3E`);
  await page.getByRole('heading', { name: 'Bead qa-<script>', exact: true }).waitFor();
  await page.getByRole('link', { name: 'History', exact: true }).focus();
  await page.keyboard.press('Enter');
  await page.waitForURL('**?tab=history');
  await page.reload();
  await page.locator('.workspace-tabs a[aria-current="page"]').filter({ hasText: 'History' }).waitFor();
  await page.getByRole('link', { name: 'Output', exact: true }).click();
  await page.goBack();
  await page.locator('.workspace-tabs a[aria-current="page"]').filter({ hasText: 'History' }).waitFor();
  await page.goto(`${url}/projects/foreign/overview`);
  await page.getByRole('heading', { name: 'Project unavailable' }).waitFor();
  await page.goto(`${url}${base}/unknown`);
  await page.getByRole('heading', { name: 'Page not found' }).waitFor();

  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto(`${url}${base}/overview`);
    await page.getByRole('heading', { name: `${projectId} overview` }).waitFor();
    await page.waitForFunction(() => /\d+ paused Beads/.test(document.querySelector('.overview-summary')?.textContent ?? ''), null, { timeout: 90000 });
    assert.equal(await page.locator('.overview .project-card, .overview .future-project').count(), 0);
    assert.deepEqual(await page.locator('.studio-overview > .panel').evaluateAll(nodes => nodes.map(node => node.classList.contains('attention-panel') ? 'Attention' : node.classList.contains('floor-panel') ? 'Delivery' : 'Catch up')), ['Attention', 'Delivery', 'Catch up']);
    await page.getByRole('heading', {name: 'Completed outcomes'}).waitFor();
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `overflow at ${width}`);
    await page.screenshot({ path: `${output}/overview-${width}.png`, fullPage: true });
    for (const label of ['All projects', 'Add project']) {
      const trigger = page.getByRole('button', { name: label, exact: false }).first();
      await trigger.focus();
      await page.keyboard.press('Enter');
      await page.getByRole('dialog').waitFor();
      await page.getByText('Unavailable', { exact: true }).waitFor();
      assert.equal(await page.getByRole('dialog').locator('input,textarea,select').count(), 0);
      // Escape restores native focus before the queued close event runs React's
      // onClose. Wait for that event before checking focus or moving it again.
      const closeEvent = await page.getByRole('dialog').evaluateHandle(dialog => ({
        closed: new Promise(resolve => dialog.addEventListener('close', () => resolve(true), { once: true }))
      }));
      await page.keyboard.press('Escape');
      await closeEvent.evaluate(event => event.closed);
      await closeEvent.dispose();
      assert.equal(await trigger.evaluate(node => node === document.activeElement), true);
    }
    await nav.getByRole('link', { name: 'Attention', exact: true }).focus();
    await page.keyboard.press('Enter');
    await page.getByRole('heading', { name: 'Attention', exact: true, level: 1 }).waitFor();
    await page.locator('.attention-row').first().waitFor({ timeout: 90000 });
    await page.screenshot({ path: `${output}/attention-${width}.png`, fullPage: true });
  }
  await overviewChecks(page, url, base, projectId, output);
  assert.deepEqual(mutations, [], 'Preview must never issue mutations');
  assert.deepEqual(errors, [], 'No browser errors');

  console.log(JSON.stringify({ projectId, widths: [1440, 375, 720], errors, mutations, output, checks: 'direct reload, history, keyboard, query state, legacy IDs, unknown project/route, dialogs and focus' }));
} catch (error) {
  const page = browser.contexts()[0]?.pages()[0];
  if (page) {
    await page.screenshot({ path: `${output}/failure.png`, fullPage: true });
    await writeFile(`${output}/failure.txt`, await page.locator('body').innerText());
  }
  await writeFile(`${output}/browser-errors.json`, JSON.stringify(errors, null, 2));
  throw error;
} finally {
  await browser.close();
}
