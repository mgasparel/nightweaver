import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import assert from 'node:assert/strict';
import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL from aspire describe.');
const output = process.env.NIGHTWEAVER_QA_OUTPUT ?? 'TestResults/planning-telemetry';
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
let release;
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await page.goto(url + '/projects/qa/delivery');
  await page.waitForFunction(() => document.querySelector('[aria-label="Planning pool"] .delivery-bay[data-status="running"]'));
  const live = await page.evaluate(() => fetch('/api/factory/state').then(r => r.json()));
  assert.equal(live.availability, 'live');
  assert.equal(live.snapshot.schema_version, 2);
  const slot = live.snapshot.planning.slots.find(s => s.status === 'running');
  assert.ok(slot.invocation.process_id > 0);
  assert.equal(slot.invocation.purpose, 'planner');
  release = join(dirname(live.snapshot.target_repository), 'qa-live-planning.release');
  let projected;
  await page.route('**/api/operations/v1/projects/qa/resources?**', async route => {
    if (new URL(route.request().url()).searchParams.get('key') !== 'factory' || !projected) return route.continue();
    const response = await route.fetch(); const envelope = await response.json();
    envelope.data = projected;
    envelope.health = projected.availability === 'stale' ? 'stale' : 'fresh';
    await route.fulfill({ response, json: envelope });
  });
  const trigger = page.locator(`[data-open-telemetry-slot="${slot.slot_id}"]`);
  for (const width of [1440, 720, 375]) {
    await page.setViewportSize({ width, height: 1000 });
    await page.locator('#factory-title').scrollIntoViewIfNeeded();
    assert.ok(await page.locator('[aria-label="Planning pool"]').textContent().then(s => s.includes('1 of 2')));
    await page.screenshot({ path: join(output, `planning-live-${width}.png`), fullPage: true });
    await trigger.focus();
    await page.keyboard.press('Enter');
    await page.waitForFunction(() => document.querySelector('#invocation-output')?.textContent.includes('live-marker-before-exit planning-native'));
    const text = await page.locator('#invocation-output').textContent();
    assert.ok(!text.includes('live-marker-before-exit slot-one'));
    assert.ok((await page.locator('#invocation-metadata').textContent()).includes(slot.invocation.invocation_id));
    assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
    await page.screenshot({ path: join(output, `planning-log-${width}.png`), fullPage: true });
    await page.keyboard.press('Escape');
    await page.waitForFunction(id => document.activeElement?.dataset.openTelemetrySlot === id, slot.slot_id);
  }
  await writeFile(release, 'release', 'utf8');
  await page.waitForFunction(id => document.querySelector(`[data-slot-id="${id}"]`)?.dataset.status === 'idle', slot.slot_id, { timeout: 15000 });
  await page.screenshot({ path: join(output, 'planning-idle.png'), fullPage: true });
  const stale = structuredClone(live);
  stale.availability = 'stale';
  stale.snapshot.observed_at = new Date(Date.now() - 20000).toISOString();
  stale.snapshot.revision += 1000;
  projected = stale;
  await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'stale');
  await page.screenshot({ path: join(output, 'planning-stale.png'), fullPage: true });
  const legacy = structuredClone(live);
  legacy.snapshot.schema_version = 1;
  legacy.snapshot.revision += 2000;
  delete legacy.snapshot.planning;
  projected = legacy;
  await page.waitForFunction(() => document.querySelector('[aria-label="Planning pool"]')?.textContent.includes('Observations unavailable'));
  assert.equal(await page.locator('[aria-label="Planning pool"] .delivery-bay').count(), 0);
  await page.screenshot({ path: join(output, 'planning-legacy.png'), fullPage: true });
  assert.deepEqual(errors, []);
  await writeFile(join(output, 'results.json'), JSON.stringify({ nativePid: slot.invocation.process_id,
    runtimeId: live.snapshot.runtime_id, invocation: slot.invocation.invocation_id,
    checks: ['live native identity', 'Planning stream isolation', 'keyboard/focus', '1440/720/375 no overflow',
      'actual completion and idle', 'stale qualification', 'legacy unavailable'], errors }, null, 2));
  console.log('Planning telemetry QA passed.');
} finally {
  if (release) await writeFile(release, 'release', 'utf8').catch(() => {});
  await browser.close();
}
