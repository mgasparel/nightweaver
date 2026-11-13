import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL from aspire describe.');
const baseline = process.argv.includes('--baseline');
const output = process.env.NIGHTWEAVER_QA_OUTPUT ?? fileURLToPath(new URL('../../TestResults/digest-threads/', import.meta.url));
await mkdir(output, { recursive: true });
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
  if (baseline) {
    for (const [pattern, file, contentType] of [
      ['**/js/app.js', 'js/app.js', 'text/javascript'],
      ['**/css/site.css', 'css/site.css', 'text/css']
    ]) {
      const body = execFileSync('git', ['show', `855c2b1:src/Nightweaver.Web/wwwroot/${file}`], { encoding: 'utf8' });
      await page.route(pattern, route => route.fulfill({ contentType, body }));
    }
  }
  const item = id => ({ id, title: 'Deliver readable Catch-Up summaries and preserve detailed operational context '.repeat(3),
    type: 'task', priority: 1, status: 'closed', activityKind: 'closed', activityAt: '2026-11-13T10:00:00Z',
    outcome: 'resolved', outcomeLabel: 'Resolved', summary: 'Detailed delivery context and verification history. '.repeat(12),
    commits: ['a'.repeat(40)], pullRequests: ['#42'], fromId: null });
  let data = { generatedAt: '2026-11-13T12:00:00Z', window: '24h', lastCaughtUpAt: null, isCaughtUp: false,
    metrics: { completedTotal: 22, completedFeatures: 0, completedTasks: 22, completedBugs: 0, activeThreads: 7,
      openOrUpdatedTotal: 7, waitingForGuidance: 0 },
    threads: [8, 1, 4, 3, 6, 2, 1].map((count, i) => ({ root: item(`thread-${i}`), completedCount: count - 1, openCount: 1,
      items: Array.from({ length: count }, (_, j) => ({ ...item(`child-${i}-${j}`), fromId: `thread-${i}`,
        ...(j === 0 ? { status: 'open', activityKind: 'updated', outcome: 'open', outcomeLabel: 'Updated' } : {}) })) })),
    standaloneCompleted: [], standaloneOpen: [] };
  data.threads[3].root.activityKind = 'context';
  await page.route(/\/api\/digest(?:\?.*)?$/, route => route.fulfill({ json: data }));
  await page.goto(url, { waitUntil: 'networkidle' });
  await page.locator('#digest-toggle').click();
  const section = page.locator('#digest-threads-section');
  const height = await section.evaluate(element => element.getBoundingClientRect().height);
  const mode = baseline ? 'baseline' : 'candidate';
  await section.screenshot({ path: `${output}/${mode}-lineage.png` });
  await page.locator('#catch-up').scrollIntoViewIfNeeded();
  await page.screenshot({ path: `${output}/${mode}-1440.png` });
  await writeFile(`${output}/${mode}-measurements.json`, JSON.stringify({ viewport: '1440x900', threads: 5, height }, null, 2));
  console.log(`${mode} initial lineage section: ${height}px`);
  if (baseline) process.exitCode = 0;
  else {
    const count = async (selector, expected) => assert.equal(await page.locator(selector).count(), expected);
    const noOverflow = async () => assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth));
    const card = i => page.locator(`[data-root-bead-id="thread-${i}"]`);
    const toggle = i => card(i).locator('.digest-activity-toggle');
    const refresh = async () => {
      await page.locator('#digest-refresh').evaluate(button => button.click());
      await page.waitForFunction(() => !document.querySelector('#digest-refresh').disabled);
    };
    await count('#digest-threads > article', 5);
    await count('#digest-threads .digest-item-row', 0);
    await count('#digest-threads .digest-summary', 0);
    for (const i of [0, 1, 2]) {
      await toggle(i).focus();
      await page.keyboard.press(i === 1 ? 'Space' : 'Enter');
      assert.equal(await toggle(i).getAttribute('aria-expanded'), 'true');
      assert.equal(await card(i).locator('.digest-thread-items > .digest-item-row').count(), [4, 1, 4][i]);
    }
    await count('[data-root-bead-id="thread-3"] .digest-item-row', 0);
    assert.equal(await card(1).locator('[data-bead-id="child-1-0"] a').getAttribute('href'), '#bead/child-1-0');
    const more = card(0).getByRole('button', { name: 'Show 4 more in thread', exact: true });
    await more.focus(); await page.keyboard.press('Enter');
    await count('[data-root-bead-id="thread-0"] .digest-thread-items > .digest-item-row', 8);
    await refresh();
    assert.equal(await page.evaluate(() => document.activeElement.textContent), 'Show fewer in thread');
    await page.keyboard.press('Enter');
    await count('[data-root-bead-id="thread-0"] .digest-thread-items > .digest-item-row', 4);
    await toggle(0).click();
    await count('[data-root-bead-id="thread-0"] .digest-item-row', 0);
    assert.equal(await toggle(1).getAttribute('aria-expanded'), 'true');
    await refresh();
    assert.equal(await page.evaluate(() => document.activeElement.dataset.digestFocus), 'activity:thread-0');
    // Keep the boundary thread focused as incoming activity moves it beyond the preview.
    await toggle(4).focus();
    await page.keyboard.press('Enter');
    assert.equal(await toggle(4).getAttribute('aria-expanded'), 'true');
    data.threads.unshift({ ...structuredClone(data.threads[4]), root: item('new-thread') });
    await refresh();
    assert.equal(await page.evaluate(() => document.activeElement.dataset.digestFocus), 'activity:thread-4');
    assert.equal(await toggle(4).getAttribute('aria-expanded'), 'true');
    await count('#digest-threads > article', 8);
    assert.equal(await page.locator('[data-root-bead-id="new-thread"] .digest-activity-toggle').getAttribute('aria-expanded'), 'false');
    assert.equal(await toggle(1).getAttribute('aria-expanded'), 'true');
    await toggle(4).scrollIntoViewIfNeeded();
    await page.screenshot({ path: `${output}/focused-boundary-refresh.png` });
    await page.keyboard.press('Enter');
    assert.equal(await toggle(4).getAttribute('aria-expanded'), 'false');
    assert.equal(await page.evaluate(() => document.activeElement.dataset.digestFocus), 'activity:thread-4');
    const ids = await page.locator('[id]').evaluateAll(elements => elements.map(element => element.id));
    assert.equal(new Set(ids).size, ids.length);
    for (const control of await page.locator('.digest-activity-toggle').all()) {
      const target = await control.getAttribute('aria-controls');
      assert.equal(await page.locator(`[id="${target}"]`).count(), 1);
    }
    await page.getByRole('button', { name: 'Show fewer threads', exact: true }).click();
    await count('#digest-threads > article', 5);
    await page.getByRole('button', { name: 'Show 3 more threads', exact: true }).click();
    await count('#digest-threads > article', 8);
    await page.getByRole('button', { name: 'Show fewer threads', exact: true }).click();
    while (await page.locator('.digest-activity-toggle[aria-expanded="true"]').count()) {
      await page.locator('.digest-activity-toggle[aria-expanded="true"]').first().click();
    }
    for (const width of [720, 375]) {
      await page.setViewportSize({ width, height: 900 });
      await noOverflow();
      await section.screenshot({ path: `${output}/candidate-${width}-lineage.png` });
      await toggle(0).click();
      await noOverflow();
      await section.screenshot({ path: `${output}/activity-${width}.png` });
      await toggle(0).click();
    }
    // Use an actual disposable Bead for root navigation, without changing backend contracts.
    const beads = await page.evaluate(async () => (await fetch('/api/beads?view=all')).json());
    const realId = (Array.isArray(beads) ? beads : beads.beads)[0].id;
    data.threads[0].root.id = realId;
    await refresh();
    await page.locator(`[data-root-bead-id="${realId}"] .digest-thread-title a`).click();
    await page.waitForFunction(() => location.hash.startsWith('#bead/'));
    assert.equal(decodeURIComponent(await page.evaluate(() => location.hash)), `#bead/${realId}`);
    await page.waitForFunction(id => document.querySelector('#workspace-id').textContent === id, realId);
    assert.deepEqual(errors, []);
    console.log('Thread QA passed: zero default children/descriptions; independent 1/4/8 activity; preview/all; refresh/focus across fifth-thread boundary; new threads; unique IDs; navigation; 375/720 reflow.');
  }
} finally { await browser.close(); }
