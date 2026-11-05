import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL to the web endpoint from aspire describe.');

const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage();
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  await page.goto(url, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Nightweaver console' }).waitFor();
  await page.waitForFunction(() => {
    const cards = Array.from(document.querySelectorAll('#delivery-slots .factory-slot'));
    return cards.some(card => card.textContent.includes('qa-live-1') && card.dataset.status === 'running')
      && cards.some(card => card.textContent.includes('qa-live-2') && card.dataset.status === 'running');
  }, null, { timeout: 15000 });

  const openSlot = async (beadId, expected, forbidden) => {
    const card = page.locator('#delivery-slots .factory-slot').filter({ hasText: beadId });
    const trigger = card.getByRole('button', { name: 'Open live invocation', exact: true });
    await trigger.click();
    const dialog = page.getByRole('dialog', { name: new RegExp(beadId) });
    await dialog.waitFor({ state: 'visible' });
    await page.waitForFunction(marker => document.querySelector('#invocation-output')?.textContent.includes(marker), expected, { timeout: 15000 });
    const output = await page.locator('#invocation-output').textContent();
    if (!output.includes(expected) || output.includes(forbidden))
      throw new Error(`The ${beadId} dialog showed incorrect slot output: ${output}`);
    if (!(await page.locator('#invocation-metadata').textContent()).includes(beadId))
      throw new Error(`The ${beadId} dialog metadata does not identify its selected task.`);
    await page.getByRole('button', { name: 'Close invocation telemetry' }).click();
    await dialog.waitFor({ state: 'hidden' });
    if (!(await trigger.evaluate(element => element === document.activeElement)))
      throw new Error(`Closing ${beadId} did not restore focus to its trigger.`);
  };

  await openSlot('qa-live-1', 'live-marker-before-exit slot-one', 'live-marker-before-exit slot-two');
  await openSlot('qa-live-2', 'live-marker-before-exit slot-two', 'live-marker-before-exit slot-one');

  const liveState = await page.evaluate(async () => {
    const response = await fetch('/api/factory/state');
    if (!response.ok) throw new Error(`Factory state returned ${response.status}.`);
    return response.json();
  });
  const staleState = JSON.parse(JSON.stringify(liveState));
  staleState.availability = 'stale';
  staleState.reason = 'heartbeat-expired';
  staleState.snapshot.observed_at = new Date(Date.now() - 20000).toISOString();
  await page.route('**/api/factory/state', route => route.fulfill({ json: staleState }));
  await page.waitForFunction(() => document.querySelector('#factory-status')?.textContent.startsWith('stale:'),
    null, { timeout: 5000 });
  const staleCards = await page.locator('#delivery-slots .factory-slot[data-status="running"]').count();
  if (staleCards < 2) throw new Error('The stale snapshot did not retain its last observed occupied slots explicitly.');
  if (errors.length) throw new Error(errors.join('\n'));
  console.log('Playwright telemetry QA passed: two live native invocations stayed isolated by slot, dialog focus returned on close, and stale availability retained the last observed occupancy.');
} finally {
  await browser.close();
}
