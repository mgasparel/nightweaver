import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';
const base = process.env.NIGHTWEAVER_QA_URL;
if (!base) throw new Error('Set NIGHTWEAVER_QA_URL from aspire describe.');
const output = process.env.NIGHTWEAVER_QA_OUTPUT || '../../TestResults/relations';
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const errors = [];
const writes = [];
page.on('pageerror', error => errors.push(error.message));
page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
page.on('request', request => { if (request.method() !== 'GET') writes.push(request.url()); });
const results = {};
try {
  await page.goto(base);
  const inventory = await page.evaluate(async () => (await fetch('/api/beads')).json());
  const find = title => { const bead = inventory.find(row => row.title === title); assert.ok(bead, title); return bead; };
  const epic = find('QA relations epic');
  const child = find('QA relation child 00 <script>literal</script>');
  const nested = find('QA relation nested child');
  const standalone = find('QA relation standalone');
  async function open(bead) {
    await page.goto(`${base}/#bead/${encodeURIComponent(bead.id)}`);
    await page.waitForFunction(id => document.querySelector('#workspace-status').textContent === `Workspace refreshed for ${id}.`, bead.id);
  }
  await open(epic);
  const native = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), epic.id);
  assert.deepEqual(native.relationObservation, { incoming: 'complete', outgoing: 'complete' });
  assert.equal(native.relations.filter(row => row.direction === 'incoming' && row.type === 'parent-child').length, 17);
  assert.equal(new Set(native.relations.map(row => `${row.id}:${row.type}:${row.direction}`)).size, native.relations.length);
  await page.locator('#workspace-meta').getByText('Type: epic', { exact: true }).waitFor();
  const group = label => page.locator('.workspace-relation-group').filter({ has: page.getByRole('heading', { name: label, exact: true }) });
  assert.equal(await group('Children (17)').locator('li').count(), 15);
  for (const label of ['Blocked by (1)', 'Blocks (1)', 'Discovered from (1)', 'Discovered work (1)']) assert.equal(await group(label).count(), 1);
  const next = page.getByRole('button', { name: 'Next Children', exact: true });
  assert.ok(await next.evaluate(node => node.getBoundingClientRect().height) >= 32);
  await next.focus(); await page.keyboard.press('Enter');
  assert.equal(await group('Children (17)').locator('li').count(), 2);
  assert.equal(await page.locator('.workspace-relation-range:focus').textContent(), '16–17 of 17');
  assert.notEqual(await page.locator('.workspace-relation-range:focus').evaluate(node => getComputedStyle(node).outlineStyle), 'none');
  await page.screenshot({ path: `${output}/pagination-focus.png`, fullPage: true });
  const refreshed = page.waitForResponse(response => response.url().endsWith(`/api/beads/${epic.id}`));
  await page.locator('#workspace-refresh').click(); await refreshed;
  await page.waitForFunction(() => !document.querySelector('#workspace-refresh').disabled);
  assert.equal(await group('Children (17)').locator('li').count(), 2);
  await page.getByRole('button', { name: 'Previous Children', exact: true }).focus(); await page.keyboard.press('Enter');
  assert.equal(await group('Children (17)').locator('li').count(), 15);
  if (native.relations.filter(row => row.direction === 'incoming' && row.type === 'parent-child').findIndex(row => row.id === child.id) >= 15) await next.click();
  const childLink = page.locator(`#workspace-relations a[href="#bead/${child.id}"]`);
  await childLink.focus(); await page.keyboard.press('Enter');
  await page.waitForFunction(id => document.querySelector('#workspace-status').textContent === `Workspace refreshed for ${id}.`, child.id);
  assert.equal(await group('Parent (1)').locator('a').getAttribute('href'), `#bead/${epic.id}`);
  assert.equal(await group('Children (1)').locator('a').getAttribute('href'), `#bead/${nested.id}`);
  assert.match(await group('Children (1)').textContent(), /Type: spike/);
  assert.equal(await page.locator('#workspace-title:focus').count(), 1);
  await open(standalone);
  assert.match(await page.locator('#workspace-relations').textContent(), /No tracker relations are recorded/);
  assert.match(await page.locator('#workspace-meta').textContent(), /Type: decision/);
  await open(epic);
  const previous = page.getByRole('button', { name: 'Previous Children', exact: true });
  if (await previous.isEnabled()) await previous.click();
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    await page.locator('#workspace-relations-title').scrollIntoViewIfNeeded();
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth), true);
    await page.screenshot({ path: `${output}/relations-${width}.png`, fullPage: true });
  }
  // Presentation-only failure fixtures: native directional HTTP reads are covered by WorkspaceRelationsWebTests.
  let mode = 'partial';
  await page.route(`**/api/beads/${epic.id}`, async route => {
    const response = structuredClone(native);
    if (mode === 'loading') { await new Promise(resolve => setTimeout(resolve, 400)); }
    else if (mode === 'partial') { response.relationObservation.incoming = 'unavailable'; response.relations = native.relations.filter(row => row.direction === 'outgoing'); }
    else if (mode === 'failed') { response.relationObservation = { incoming: 'unavailable', outgoing: 'unavailable' }; response.relations = []; }
    else if (mode === 'unknown') {
      response.relations = [{ id: 'missing-id', type: '<script>custom-edge</script>'.repeat(20), direction: 'incoming', title: null, status: null, issueType: null },
        { id: child.id, type: 'relates-to', direction: 'outgoing', title: '<img src=x onerror=alert(1)>', status: 'open', issueType: 'custom-type' }];
    }
    await route.fulfill({ json: response });
  });
  for (const state of ['partial', 'failed', 'unknown']) {
    mode = state;
    const response = page.waitForResponse(response => response.url().endsWith(`/api/beads/${epic.id}`));
    await page.locator('#workspace-refresh').click(); await response;
    await page.waitForFunction(() => !document.querySelector('#workspace-refresh').disabled);
    const text = await page.locator('#workspace-relations').textContent();
    if (state !== 'unknown') {
      assert.match(text, /Incoming relations \(including children\) unavailable/);
      assert.doesNotMatch(text, /No tracker relations are recorded/);
      assert.equal(await page.locator('#workspace-relations a').count(), state === 'partial' ? 2 : 0);
    } else {
      assert.match(text, /Type: Unavailable/); assert.match(text, /Related Bead details unavailable/);
      assert.match(text, /Type: custom-type/);
      assert.equal(await page.locator('#workspace-relations script, #workspace-relations img').count(), 0);
    }
    if (state === 'unknown') {
      for (const width of [375, 720]) {
        await page.setViewportSize({ width, height: 900 });
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth), true);
      }
    }
    await page.screenshot({ path: `${output}/${state}.png`, fullPage: true });
  }
  mode = 'loading';
  await open(standalone);
  await page.goto(`${base}/#bead/${epic.id}`);
  await page.getByText('Loading tracker relations…', { exact: true }).waitFor();
  await page.waitForFunction(id => document.querySelector('#workspace-status').textContent === `Workspace refreshed for ${id}.`, epic.id);
  const after = await page.evaluate(async () => (await fetch('/api/beads')).json());
  assert.deepEqual(after, inventory);
  assert.deepEqual(writes, []); assert.deepEqual(errors, []);
  results.nativeChildren = 17; results.checks = 'Native types, mixed directions, nested hierarchy, exact keyboard navigation, 15-row pagination, empty/loading/partial/failed/missing/custom/hostile values, 1440/375/720 overflow, read-only inventory';
  console.log(JSON.stringify(results));
} finally {
  await writeFile(`${output}/results.json`, JSON.stringify({ ...results, errors, writes }, null, 2));
  await browser.close();
}
