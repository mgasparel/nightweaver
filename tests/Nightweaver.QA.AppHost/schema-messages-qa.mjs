import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL from aspire describe; start the schema-messages scenario.');
const output = process.env.NIGHTWEAVER_QA_OUTPUT || '../../TestResults/schema-messages';
await mkdir(output, { recursive: true });
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  const errors = [], mutations = [], external = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    if (request.method() !== 'GET') mutations.push(request.url());
    if (new URL(request.url()).origin !== new URL(url).origin) external.push(request.url());
  });
  await page.goto(url, { waitUntil: 'networkidle' });
  const attention = await page.evaluate(async () => (await fetch('/api/attention')).json());
  const items = Array.isArray(attention) ? attention : attention.items;
  const primary = items.find(i => i.title === 'Schema messages: verification pause');
  const question = items.find(i => i.title === 'Schema messages: product question');
  const uncertain = items.find(i => i.title === 'Schema messages: uncertain current explanation');
  const malformed = items.find(i => i.title === 'Schema messages: malformed source');
  const uncertainQuestion = items.find(i => i.title === 'Schema messages: uncertain product question');
  assert(primary && question && uncertain && malformed && uncertainQuestion, 'Start with NIGHTWEAVER_QA_SCENARIO=schema-messages');
  assert.equal(primary.message.state, 'supported');
  assert.equal(uncertain.message.state, 'fallback');
  assert.equal(question.message.title, 'Planning question');
  const workspace = async item => {
    await page.goto(`${url.replace(/\/$/, '')}/#bead/${encodeURIComponent(item.beadId)}`);
    await page.locator('#workspace-title').filter({ hasText: item.title }).waitFor();
    await page.locator('#workspace-status').filter({ hasText: /loaded|refreshed/i }).waitFor();
  };
  await workspace(primary);
  const data = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), primary.beadId);
  assert.equal(data.history.activeCommentId, primary.sourceCommentId);
  assert.deepEqual(data.attention.message, primary.message);
  assert.equal(data.comments.find(c => c.id === primary.sourceCommentId).body, primary.message.original);
  const active = page.locator('#workspace-guidance-context');
  assert.match(await active.innerText(), /Verification could not establish/);
  assert.match(await active.innerText(), /Environment retained/);
  await page.locator('.workspace-older-history > summary').click();
  const history = page.locator('#workspace-history');
  for (const label of ['Reported conflict rejection', 'Reported design rejection', 'Check origin', 'Historical cleanup warning', 'Supplemental pause failure', 'Corrective prose'])
    assert.match(await history.innerText(), new RegExp(label));
  assert.equal(await page.locator('.semantic-message a, .semantic-message img, .semantic-message script').count(), 0);
  assert.equal(await page.evaluate(() => window.__schemaInjected), undefined);

  // Actual focus survives a refresh while the same source is retained.
  const original = active.locator(':scope > .semantic-message > .message-original').last();
  const summary = original.locator('summary');
  await summary.focus();
  await page.keyboard.press('Enter');
  assert.equal(await original.getAttribute('open'), '');
  await page.evaluate(() => loadWorkspace(workspaceCurrentId, { manual: true }));
  await page.waitForFunction(() => document.activeElement?.matches('[data-message-focus]'));
  assert.equal(await active.locator(':scope > .semantic-message > .message-original').last().getAttribute('open'), '');
  await active.locator(':scope > .semantic-message > .message-original > summary').last().click();

  // Browser geometry at ordinary desktop, narrow mobile, and 200% effective zoom.
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 1000 });
    await page.locator('#workspace-title').scrollIntoViewIfNeeded();
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Overflow at ${width}`);
    await page.screenshot({ path: `${output}/diagnostic-${width}.png`, fullPage: true });
    await active.screenshot({ path: `${output}/message-${width}.png` });
  }
  await page.setViewportSize({ width: 1440, height: 1000 });
  await workspace(question);
  for (const text of ['Which retention policy should apply?', 'Keep retained evidence', 'More disk space, but inspectable failures']) {
    assert.match(await page.locator('#workspace-guidance-context').innerText(), new RegExp(text.replace('?', '\\?')));
    assert.match(await page.locator('#workspace-history').innerText(), new RegExp(text.replace('?', '\\?')));
  }
  await page.screenshot({ path: `${output}/question-desktop.png`, fullPage: true });
  await workspace(uncertain);
  assert.match(await page.locator('#workspace-guidance-context').innerText(), /Unsupported Nightweaver contract/);
  assert.doesNotMatch(await page.locator('#workspace-guidance-context').innerText(), /Older supported explanation/);
  await page.locator('.workspace-older-history > summary').click();
  assert.match(await history.innerText(), /Older supported explanation/);
  await workspace(malformed);
  await page.locator('.workspace-older-history > summary').click();
  assert.match(await history.innerText(), /Malformed Nightweaver message/);
  await history.locator('.message-original > summary').last().click();
  assert.match(await history.innerText(), /Mixed prose/);
  await workspace(uncertainQuestion);
  assert.match(await active.innerText(), /Malformed Nightweaver message/);

  // Desk uses the exact same selected source and renderer; inventory remains bounded.
  await page.goto(url, { waitUntil: 'networkidle' });
  const showAll = page.locator('#attention-show-all:not([hidden])');
  if (await showAll.count()) await showAll.first().click();
  for (const item of [primary, question, uncertain, uncertainQuestion]) {
    const card = page.locator(`.attention-card[data-bead-id="${item.beadId}"]`);
    await card.waitFor();
    await card.locator('.attention-context > summary').click();
    assert.match(await card.innerText(), new RegExp(item.message.summary.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')));
    assert.match(await card.innerText(), new RegExp(`Source comment: ${item.sourceCommentId}`));
  }
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 1000 });
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `Desk overflow at ${width}`);
    await page.screenshot({ path: `${output}/desk-${width}.png`, fullPage: true });
  }
  assert.deepEqual(errors, []);
  assert.deepEqual(mutations, []);
  assert.deepEqual(external, []);
  await writeFile(`${output}/result.json`, JSON.stringify({ surfaces: ['history', 'workspace', 'desk'], widths: [1440, 375, 720], errors, mutations, external }, null, 2));
  console.log('Schema messages QA passed: all three surfaces, source selection, exact originals, keyboard refresh, responsive reflow, zero mutations/fetches.');
} finally { await browser.close(); }
