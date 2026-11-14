import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL from aspire describe; use the workflow scenario.');
const output = process.env.NIGHTWEAVER_QA_OUTPUT || '../../TestResults/workflow';
await mkdir(output, { recursive: true });
const browser = await chromium.launch();
const errors = [], writes = [], checked = [], geometry = [];
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  await page.emulateMedia({ reducedMotion: 'reduce' });
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  page.on('request', request => { if (request.method() !== 'GET') writes.push(request.url()); });
  await page.goto(url, { waitUntil: 'networkidle' });
  const inventory = await page.evaluate(async () => (await fetch('/api/beads?view=all')).json());
  const cases = inventory.filter(b => b.title.startsWith('Workflow: '));
  assert(cases.length >= 20);
  const digest = await page.evaluate(async () => (await fetch('/api/digest?window=all')).json());
  const digestItems = [...(digest.threads || []).flatMap(t => [t.root, ...t.items]), ...(digest.standaloneOpen || []), ...(digest.standaloneCompleted || [])];
  const attention = await page.evaluate(async () => (await fetch('/api/attention')).json());
  for (const bead of cases) {
    const data = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), bead.id);
    assert.deepEqual(data.presentation, bead.presentation, `Workspace/Explorer facts for ${bead.title}`);
    const digestItem = digestItems.find(i => i.id === bead.id);
    assert(digestItem, `Digest includes ${bead.title}`);
    assert.deepEqual(digestItem.presentation, bead.presentation);
    const item = attention.find(i => i.beadId === bead.id);
    if (item) assert.deepEqual(item.presentation, bead.presentation);
    for (const relation of data.relations) {
      const target = inventory.find(b => b.id === relation.id);
      assert.equal(relation.role, target?.presentation.role || 'Role unavailable');
    }
    checked.push({ title: bead.title, presentation: bead.presentation });
  }
  function find(name) { const bead = cases.find(b => b.title === `Workflow: ${name}`); assert(bead, name); return bead; }
  assert.equal(find('manual Plan').presentation.role, 'Plan Bead');
  assert.equal(find('manual linked Plan').presentation.publication, null);
  assert.equal(find('published graph').presentation.publication, 'Published graph observed');
  assert.equal(find('held graph').presentation.publication, 'Publication hold');
  assert.equal(find('partial graph').presentation.publication, 'Publication hold / verification unavailable');
  assert.equal(find('conflict graph').presentation.publication, 'Publication needs attention');
  assert(find('unknown stage').presentation.diagnostic);
  assert(find('conflicting pauses').presentation.diagnostic);
  const historical = attention.find(i => i.beadId === find('historical technical attention').id);
  assert.equal(historical.kind, 'needs-human');
  assert.equal(historical.canRetry, false);
  assert.equal(historical.canAbort, false);

  async function open(name) {
    const bead = find(name);
    await page.goto(`${url}/#bead/${encodeURIComponent(bead.id)}`);
    await page.waitForFunction(id => document.querySelector('#workspace-id')?.textContent === id && !document.querySelector('#workspace-refresh')?.disabled, bead.id);
    await page.locator('#workspace-title').filter({ hasText: bead.title }).waitFor();
    assert.match(await page.locator('#workspace-meta').innerText(), new RegExp(bead.presentation.role));
    return bead;
  }
  async function capture(name, width, expanded) {
    await page.setViewportSize({ width, height: 1000 });
    await open(name);
    const details = page.locator('#workspace-workflow details');
    await details.evaluate((node, value) => node.open = value, expanded);
    await page.locator('#workspace-workflow').scrollIntoViewIfNeeded();
    const size = await page.evaluate(() => ({ width: innerWidth, scroll: document.documentElement.scrollWidth }));
    assert(size.scroll <= size.width, JSON.stringify(size));
    geometry.push({ name, expanded, ...size });
    await page.screenshot({ path: `${output}/${name.replaceAll(' ', '-')}-${width}-${expanded ? 'expanded' : 'compact'}.png` });
  }
  await open('incompatible stage');
  assert.match(await page.locator('#workspace-planning').innerText(), /Planning state unavailable/);
  await open('claimed stored work');
  await page.locator('#workspace-workflow summary').click();
  await page.locator('#workspace-workflow summary').focus();
  await page.keyboard.press('Tab');
  await page.keyboard.press('Shift+Tab');
  await page.evaluate(() => loadWorkspace(workspaceCurrentId, { manual: true }));
  assert.equal(await page.locator('#workspace-workflow details').evaluate(node => node.open), true);
  assert.equal(await page.locator('#workspace-workflow summary').evaluate(node => node === document.activeElement), true);
  assert.match(await page.locator('#workspace-workflow').innerText(), /2 used \/ 3 configured/);
  assert.match(await page.locator('#workspace-workflow').innerText(), /Tracker ownership: fixture-owner/);
  assert.doesNotMatch(await page.locator('#workspace-workflow').innerText(), /Running/);
  assert.match(await page.locator('#workspace-environment').innerText(), /unavailable|Unknown|unknown|No retained/i);
  await page.screenshot({ path: `${output}/workflow-focus-1440.png` });
  for (const width of [1440, 375, 720]) {
    await capture('claimed stored work', width, false);
    await capture('long literal content', width, true);
    await capture('conflicting pauses', width, true);
    await capture('unknown stage', width, true);
    await capture('invalid repair budget', width, true);
    await capture('historical technical attention', width, true);
    await capture('held graph', width, false);
    await capture('partial graph', width, true);
    await capture('conflict graph', width, true);
  }
  assert.equal(await page.locator('#workspace-workflow img, #workspace-workflow script').count(), 0);
  const claimed = find('claimed stored work');
  const observed = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), claimed.id);
  await page.route(`**/api/beads/${claimed.id}`, route => route.fulfill({ json: {
    ...observed, environment: { ...observed.environment, processState: 'stale', warning: 'Retained runtime observation is stale.' }
  } }));
  await open('claimed stored work');
  assert.match(await page.locator('#workspace-environment').innerText(), /stale/);
  assert.equal(await page.locator('#workspace-workflow .workflow-summary').innerText(), claimed.presentation.summary);
  await page.setViewportSize({ width: 720, height: 1000 });
  await page.locator('#workspace-workflow').scrollIntoViewIfNeeded();
  await page.locator('#workspace-environment').locator('xpath=..').screenshot({ path: `${output}/stale-observation-720.png` });
  await page.unroute(`**/api/beads/${claimed.id}`);
  // Source label disclosures retain both keyboard focus and expansion on refresh.
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.goto(url + '/#factory');
  await page.locator('[data-view=all]').click();
  await page.locator('#beads-search').fill('Workflow: long literal content');
  await page.waitForFunction(() => document.querySelector('#beads-table-body')?.textContent.includes('Workflow: long literal content'));
  const source = page.locator('#beads-table-body summary').first();
  await source.click();
  await source.focus();
  await page.evaluate(() => renderBeads(explorerResults));
  assert.equal(await source.evaluate(node => node.parentElement.open), true);
  assert.equal(await source.evaluate(node => node === document.activeElement), true);
  await page.screenshot({ path: `${output}/explorer-desktop.png` });
  const after = await page.evaluate(async () => (await fetch('/api/beads?view=all')).json());
  assert.deepEqual(after, inventory, 'Viewing performs no workflow mutation');
  assert.deepEqual(writes, []);
  assert.deepEqual(errors, []);
  await writeFile(`${output}/results.json`, JSON.stringify({ checked, geometry, writes, errors }, null, 2));
  console.log(JSON.stringify({ cases: checked.length, captures: geometry.length, writes, errors, output }));
} finally { await browser.close(); }
