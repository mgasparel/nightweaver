import { writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL to the web endpoint from aspire describe.');

const browser = await chromium.launch({ headless: true });
let releaseFiles = [];
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  const streamUrls = new Map();
  const pendingExtraHeaders = new Map();
  let reconnectLastEventId = null;
  let notifyReconnect;
  const reconnectSeen = new Promise(resolve => { notifyReconnect = resolve; });
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Network.enable');
  const captureReconnectHeader = (requestId, headers) => {
    const requestUrl = streamUrls.get(requestId);
    if (!requestUrl) {
      pendingExtraHeaders.set(requestId, headers);
      return;
    }
    if (new URL(requestUrl).searchParams.get('slot_id') !== 'delivery-1') return;
    const lastEventId = Object.entries(headers).find(([name]) => name.toLowerCase() === 'last-event-id')?.[1];
    if (typeof lastEventId === 'string') {
      reconnectLastEventId = lastEventId;
      notifyReconnect(lastEventId);
    }
  };
  cdp.on('Network.requestWillBeSent', event => {
    streamUrls.set(event.requestId, event.request.url);
    const pending = pendingExtraHeaders.get(event.requestId);
    if (pending) captureReconnectHeader(event.requestId, pending);
  });
  cdp.on('Network.requestWillBeSentExtraInfo', event => captureReconnectHeader(event.requestId, event.headers));
  await page.goto(url, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Nightweaver console' }).waitFor();
  await page.waitForFunction(() => {
    const cards = Array.from(document.querySelectorAll('#delivery-slots .factory-slot'));
    return cards.some(card => card.textContent.includes('qa-live-1') && card.dataset.status === 'running')
      && cards.some(card => card.textContent.includes('qa-live-2') && card.dataset.status === 'running');
  }, null, { timeout: 15000 });

  const liveState = await page.evaluate(async () => {
    const response = await fetch('/api/factory/state');
    if (!response.ok) throw new Error(`Factory state returned ${response.status}.`);
    return response.json();
  });
  if (!liveState.snapshot?.target_repository) throw new Error('The live snapshot has no target repository identity.');
  const fixtureRoot = dirname(liveState.snapshot.target_repository);
  releaseFiles = ['qa-live-1.release', 'qa-live-2.release'].map(name => join(fixtureRoot, name));

  const slotOne = liveState.snapshot.delivery.slots.find(slot => slot.bead_id === 'qa-live-1');
  if (!slotOne?.invocation) throw new Error('The first live fixture invocation is missing from the snapshot.');
  let firstStreamIntercepted = false;
  let reconnectUrl = null;
  let streamRequests = 0;
  await page.route('**/api/telemetry/stream**', async route => {
    const requestUrl = new URL(route.request().url());
    if (requestUrl.searchParams.get('slot_id') === slotOne.slot_id) {
      streamRequests += 1;
      if (!firstStreamIntercepted) {
        firstStreamIntercepted = true;
        const id = `${liveState.snapshot.runtime_id}.${slotOne.invocation.invocation_id}.5`;
        await route.fulfill({
          status: 200,
          contentType: 'text/event-stream; charset=utf-8',
          body: [
            `id: ${id}\nevent: status\ndata: ${JSON.stringify({ runtimeId: liveState.snapshot.runtime_id, availability: 'live', status: 'running' })}\n\n`,
            `id: ${id}\nevent: log\ndata: ${JSON.stringify({ runtimeId: liveState.snapshot.runtime_id, slotId: slotOne.slot_id, invocationId: slotOne.invocation.invocation_id, text: 'live-', cursor: 5 })}\n\n`
          ].join('')
        });
        return;
      }
      reconnectUrl = requestUrl.toString();
    }
    await route.continue();
  });

  const firstCard = page.locator('#delivery-slots .factory-slot').filter({ hasText: 'qa-live-1' });
  const firstTrigger = firstCard.locator('[data-open-telemetry-slot="delivery-1"]');
  await firstTrigger.click();
  const firstDialog = page.getByRole('dialog', { name: /qa-live-1/ });
  await firstDialog.waitFor({ state: 'visible' });
  await page.waitForFunction(() => document.querySelector('#invocation-output')?.textContent.includes('live-marker-before-exit slot-one'),
    null, { timeout: 15000 });
  const firstText = await page.locator('#invocation-output').textContent();
  const expectedResumeId = `${liveState.snapshot.runtime_id}.${slotOne.invocation.invocation_id}.5`;
  const seenResumeId = await Promise.race([
    reconnectSeen,
    page.waitForTimeout(15000).then(() => { throw new Error('EventSource did not reconnect after the controlled disconnect.'); })
  ]);
  if (!firstText.includes('live-marker-before-exit slot-one')
      || firstText.includes('live-marker-before-exit slot-two')
      || (firstText.match(/live-marker-before-exit slot-one/g) ?? []).length !== 1
      || seenResumeId !== expectedResumeId || reconnectLastEventId !== expectedResumeId
      || reconnectUrl?.includes('cursor=') === true)
    throw new Error(`The first drawer did not resume once at the last event ID: ${JSON.stringify({ firstText, seenResumeId, reconnectLastEventId, reconnectUrl, expectedResumeId })}`);
  if (!(await page.locator('#invocation-metadata').textContent()).includes('qa-live-1'))
    throw new Error('The first dialog metadata does not identify its selected task.');

  await page.getByRole('button', { name: 'Pause follow' }).click();
  await page.getByRole('button', { name: 'Resume follow' }).waitFor();
  await page.getByRole('button', { name: 'Jump to latest' }).click();
  await page.getByRole('button', { name: 'Pause follow' }).waitFor();
  await writeFile(releaseFiles[0], 'release', 'utf8');
  await page.waitForFunction(() => document.querySelector('#invocation-output')?.textContent.includes('live-marker-after-release slot-one'),
    null, { timeout: 15000 });
  await page.getByText('✓ Stream ended: completed', { exact: true }).waitFor({ timeout: 15000 });
  const completedRequestCount = streamRequests;
  await page.waitForTimeout(3500);
  if (streamRequests !== completedRequestCount)
    throw new Error('The browser retried the completed EventSource after its end event.');
  await page.getByRole('button', { name: 'Close invocation telemetry' }).click();
  await firstDialog.waitFor({ state: 'hidden' });
  await page.waitForFunction(() => document.querySelector('[data-open-telemetry-slot="delivery-1"]') === document.activeElement,
    null, { timeout: 5000 });

  const secondCard = page.locator('#delivery-slots .factory-slot').filter({ hasText: 'qa-live-2' });
  const secondTrigger = secondCard.getByRole('button', { name: 'Open live invocation', exact: true });
  const secondDialog = page.getByRole('dialog', { name: /qa-live-2/ });
  for (const layout of [
    { label: '375px narrow', width: 375, height: 812, deviceScaleFactor: 1 },
    { label: 'desktop at 200% zoom', width: 640, height: 450, deviceScaleFactor: 2 }
  ]) {
    await cdp.send('Emulation.setDeviceMetricsOverride', {
      width: layout.width, height: layout.height, deviceScaleFactor: layout.deviceScaleFactor, mobile: false
    });
    await secondTrigger.click();
    await secondDialog.waitFor({ state: 'visible' });
    await page.waitForFunction(() => document.querySelector('#invocation-output')?.textContent.includes('live-marker-before-exit slot-two'),
      null, { timeout: 15000 });
    const secondGeometry = await secondDialog.evaluate(dialog => {
      const rect = dialog.getBoundingClientRect();
      return { left: rect.left, right: rect.right, viewport: window.innerWidth,
        documentWidth: document.documentElement.scrollWidth, clientWidth: document.documentElement.clientWidth,
        devicePixelRatio: window.devicePixelRatio };
    });
    if (secondGeometry.left < 0 || secondGeometry.right > secondGeometry.viewport + 1
        || secondGeometry.documentWidth > secondGeometry.clientWidth + 1
        || secondGeometry.devicePixelRatio !== layout.deviceScaleFactor)
      throw new Error(`The ${layout.label} telemetry drawer overflows its viewport: ${JSON.stringify(secondGeometry)}`);
    await page.keyboard.press('Escape');
    await secondDialog.waitFor({ state: 'hidden' });
    await page.waitForFunction(() => document.querySelector('[data-open-telemetry-slot="delivery-2"]') === document.activeElement,
      null, { timeout: 5000 });
  }
  await writeFile(releaseFiles[1], 'release', 'utf8');

  const staleState = JSON.parse(JSON.stringify(liveState));
  staleState.availability = 'stale';
  staleState.reason = 'heartbeat-expired';
  staleState.snapshot.observed_at = new Date(Date.now() - 20000).toISOString();
  await page.route('**/api/factory/state', route => route.fulfill({ json: staleState }));
  await page.waitForFunction(() => document.querySelector('#factory-status')?.dataset.state === 'stale',
    null, { timeout: 5000 });
  const staleCards = await page.locator('#delivery-slots .factory-slot[data-status="running"]').count();
  if (staleCards < 2) throw new Error('The stale snapshot did not retain its last observed occupied slots explicitly.');
  if (errors.length) throw new Error(errors.join('\n'));
  console.log('Playwright telemetry QA passed: native slot isolation, EventSource resume without duplicate output, completion/end without retry, follow controls, Escape/focus, narrow 200% layout, and stale occupancy.');
} finally {
  for (const path of releaseFiles) {
    try { await writeFile(path, 'release', 'utf8'); } catch { /* AppHost teardown also releases both processes. */ }
  }
  await browser.close();
}
