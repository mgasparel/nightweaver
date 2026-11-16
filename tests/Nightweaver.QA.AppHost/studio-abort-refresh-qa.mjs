import assert from 'node:assert/strict';
import { chromium } from 'playwright';
const base = process.env.NIGHTWEAVER_QA_URL;
assert(base, 'Use the exact isolated endpoint');
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage();
  const errors = [], posts = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => { if (r.method() === 'POST') posts.push(r.url()); });
  await page.addInitScript(() => {
    const Native = window.EventSource;
    window.operationStreams = [];
    window.EventSource = class extends Native {
      constructor(...args) { super(...args); if (String(args[0]).includes('/operations/v1/')) window.operationStreams.push(this); }
    };
  });
  const attention = await (await page.request.get(base + '/api/attention')).json();
  const item = attention.find(row => row.title === 'QA needs human abort');
  assert(item);
  await page.goto(`${base}/projects/qa/beads/${encodeURIComponent(item.beadId)}`);
  const abort = page.getByRole('button', { name: 'Abort & Close', exact: true });
  await abort.click({ timeout: 90000 });
  const cancel = page.getByRole('button', { name: 'Cancel', exact: true });
  await cancel.waitFor();
  assert(await cancel.evaluate(n => n === document.activeElement));
  await page.evaluate(() => {
    const stream = window.operationStreams.at(-1);
    stream.close(); stream.dispatchEvent(new Event('error'));
  });
  // A transport replacement must disable commands while keeping this exact confirmation and focus.
  assert(await cancel.isVisible(), 'Reconnection must retain the confirmation');
  assert(await cancel.evaluate(n => n === document.activeElement), 'Reconnection must retain Cancel focus');
  assert(await page.getByRole('button', { name: 'Confirm Abort & Close', exact: true }).isDisabled());
  await page.waitForFunction(() => ![...document.querySelectorAll('button')].find(n => n.textContent === 'Confirm Abort & Close')?.disabled);
  assert(await cancel.isVisible());
  assert(await cancel.evaluate(n => n === document.activeElement));
  await page.keyboard.press('Escape');
  assert(await abort.evaluate(n => n === document.activeElement));
  assert.deepEqual(errors, []); assert.deepEqual(posts, []);
  console.log(JSON.stringify({ passed: true, retainedConfirmation: item.beadId, commands: posts, errors }));
} finally { await browser.close(); }
