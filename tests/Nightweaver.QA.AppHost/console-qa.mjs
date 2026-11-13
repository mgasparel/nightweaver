import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const url = process.env.NIGHTWEAVER_QA_URL;
if (!url) throw new Error('Set NIGHTWEAVER_QA_URL from aspire describe.');
const baseline = process.argv.includes('--baseline');
const output = fileURLToPath(new URL(`../../TestResults/console/${baseline ? 'baseline' : 'candidate'}/`, import.meta.url));
await mkdir(output, { recursive: true });
const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('console', m => { if (m.type() === 'error' && !m.text().includes('503')) errors.push(m.text()); });
  if (baseline) {
    const ref = process.env.NIGHTWEAVER_QA_BASELINE ?? 'HEAD';
    for (const [route, file, contentType] of [
      [url.replace(/\/$/, '') + '/', 'index.html', 'text/html'],
      ['**/js/app.js', 'js/app.js', 'text/javascript'],
      ['**/css/site.css', 'css/site.css', 'text/css']
    ]) {
      const body = execFileSync('git', ['show', `${ref}:src/Nightweaver.Web/wwwroot/${file}`], { encoding: 'utf8' });
      await page.route(route, r => r.fulfill({ contentType, body }));
    }
  }
  await page.goto(url, { waitUntil: 'networkidle' });
  const original = await page.evaluate(async () => (await fetch('/api/factory/state')).json());
  const digestItem = id => ({ id, title: 'Operational milestone '.repeat(10), type: 'task', priority: 1,
    status: 'closed', activityKind: 'closed', activityAt: new Date().toISOString(), outcome: 'resolved',
    outcomeLabel: 'Resolved', summary: 'A complete milestone summary with recovery context. '.repeat(8),
    commits: ['a'.repeat(40)], pullRequests: [], fromId: null });
  let digest = { generatedAt: new Date().toISOString(), window: '24h', lastCaughtUpAt: null, isCaughtUp: false,
    metrics: { completedTotal: 64, completedFeatures: 0, completedTasks: 64, completedBugs: 0,
      activeThreads: 7, openOrUpdatedTotal: 2, waitingForGuidance: 4 },
    threads: Array.from({ length: 7 }, (_, i) => ({ root: digestItem(`root-${i}`), completedCount: 8, openCount: 0,
      items: Array.from({ length: 8 }, (_, j) => digestItem(`child-${i}-${j}`)) })),
    standaloneCompleted: Array.from({ length: 8 }, (_, i) => digestItem(`solo-${i}`)), standaloneOpen: [] };
  await page.route(/\/api\/digest(?:\?.*)?$/, route => route.fulfill({ json: digest }));
  const tracker = Array.from({ length: 211 }, (_, i) => ({ id: `qa-inventory-${i}`, title: `Operational Bead ${i}`,
    type: 'task', priority: 2, status: 'open', labels: [], assignee: null }));
  await page.route('**/api/beads?*', route => route.fulfill({ json: tracker }));
  await page.evaluate(async () => { await loadBeads(); });
  const runtime = structuredClone(original.snapshot);
  const slot = (id, role, stage) => ({ slot_id: id, bead_id: `qa-${id}`, status: 'running', role, stage,
    repair_cycles: 1, max_repair_cycles: 3, branch: `nightweaver/qa-${id}`,
    worktree_path: 'H:\\Nightweaver\\' + 'long-path\\'.repeat(20),
    invocation: { bead_id: `qa-${id}`, invocation_id: `inv-${id}`, cli: 'codex', status: 'running',
      started_at: new Date().toISOString(), deadline_at: new Date(Date.now() + 600000).toISOString() },
    diff: { status: 'available', added: 23, deleted: 7, binary: 0, untracked: 0 } });
  runtime.delivery = { capacity: 3, slots: [slot('delivery-1', 'Implementer', 'stage:implement'),
    slot('delivery-2', 'Reviewer', 'stage:review'), { slot_id: 'delivery-3', status: 'idle' }] };
  runtime.merger = { capacity: 1, slots: [slot('merger-1', 'Merger', 'stage:merge')] };
  let factory = { availability: 'live', snapshot: runtime };
  let items = Array.from({ length: 4 }, (_, i) => ({ beadId: `qa-pause-${i}`, title: `Resolve operator question ${i}`,
    kind: i % 2 ? 'needs-human' : 'product-decision-required', priority: i, type: 'task', status: 'open',
    stage: 'stage:implement', fromRole: 'implementer', repairCycles: i, maxRepairCycles: 3,
    question: 'Which repository should own the recovery boundary?', diagnostic: 'Review found an unresolved recovery boundary.',
    labels: [i % 2 ? 'needs-human' : 'product-decision-required'], canDecide: true, canRetry: true, canAbort: true }));
  let fail = false;
  await page.route('**/api/attention', route => fail ? route.fulfill({ status: 503, json: {} }) : route.fulfill({ json: items }));
  await page.route('**/api/factory/state', route => route.fulfill({ json: factory }));
  const refresh = async () => {
    runtime.revision++;
    await page.evaluate(async () => { await loadAttention(); await refreshFactory(); await loadDigest(); });
  };
  const setIdle = () => {
    items = [];
    runtime.delivery.slots = runtime.delivery.slots.map(s => ({ slot_id: s.slot_id, status: 'idle' }));
    runtime.merger.slots = [{ slot_id: 'merger-1', status: 'idle' }];
    factory.availability = 'live';
    digest = { ...digest, lastCaughtUpAt: digest.generatedAt, isCaughtUp: true,
      threads: [], standaloneCompleted: [], standaloneOpen: [],
      metrics: { completedTotal: 0, completedFeatures: 0, completedTasks: 0, completedBugs: 0,
        activeThreads: 0, openOrUpdatedTotal: 0, waitingForGuidance: 0 } };
  };
  const shot = async name => {
    await page.evaluate(() => window.scrollTo(0, 0));
    if (!baseline) assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth), name);
    await page.screenshot({ path: `${output}/${name}.png`, fullPage: true });
    if (name.startsWith('busy-') || name.startsWith('idle-')) await page.screenshot({ path: `${output}/${name}-viewport.png` });
  };
  await refresh();
  assert.match(await page.locator('#merger-pool-status').innerText(), /1 of 1 slot active/);
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    await shot(`busy-${width}`);
  }
  if (baseline) {
    items[0].title = 'LongTitleWithoutSpaces'.repeat(10).slice(0, 200);
    items[0].sourceAuthor = 'long-author-'.repeat(20);
    items[0].question = 'Full current question and diagnostics must remain reachable. '.repeat(30);
    await refresh();
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 900 });
      await shot(`long-expanded-${width}`);
    }
    fail = true;
    factory.availability = 'stale';
    await refresh();
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 900 });
      await shot(`retained-error-${width}`);
    }
    fail = false;
    setIdle();
    await refresh();
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 900 });
      await shot(`idle-${width}`);
    }
    assert.deepEqual(errors, []);
    console.log('Baseline captured.');
  }
  else {
    assert.equal(await page.locator('.attention-card').count(), 2);
    assert.deepEqual(await page.locator('.attention-card').evaluateAll(cards => cards.map(c => c.dataset.beadId)), ['qa-pause-0', 'qa-pause-1']);
    assert.match(await page.locator('#attention-status').innerText(), /4 Beads/);
    const toggle = page.locator('#attention-show-all');
    assert.equal(await toggle.innerText(), 'Show all 4 paused Beads (2 more)');
    await toggle.click();
    assert.equal(await toggle.getAttribute('aria-expanded'), 'true');
    assert.equal(await page.locator('.attention-card').count(), 4);
    const third = page.locator('[data-bead-id="qa-pause-2"]');
    await third.locator('.attention-compose > summary').click();
    await third.locator('textarea').fill('Keep this draft across refresh and disclosure');
    await third.locator('textarea').focus();
    await third.locator('textarea').evaluate(e => e.setSelectionRange(5, 12, 'backward'));
    await refresh();
    assert.equal(await third.locator('textarea').inputValue(), 'Keep this draft across refresh and disclosure');
    assert.equal(await third.locator('textarea').evaluate(e => e === document.activeElement), true);
    assert.deepEqual(await third.locator('textarea').evaluate(e => [e.selectionStart, e.selectionEnd, e.selectionDirection]), [5, 12, 'backward']);
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 900 });
      await shot(`expanded-queue-${width}`);
    }
    await page.evaluate(() => { attentionShowAll.click(); });
    assert.equal(await toggle.getAttribute('aria-expanded'), 'false');
    assert.equal(await toggle.evaluate(e => e === document.activeElement), true);
    await toggle.click();
    assert.equal(await third.locator('textarea').inputValue(), 'Keep this draft across refresh and disclosure');
    assert.equal(await third.locator('textarea').isVisible(), true);
    let release;
    const held = new Promise(resolve => { release = resolve; });
    await page.route('**/api/beads/qa-pause-2/decide', async route => {
      await held;
      await route.fulfill({ json: { beadId: 'qa-pause-2', action: 'note_only', resolved: false, commentRecorded: true, stateChanged: false } });
    });
    const submitted = page.waitForRequest(r => r.method() === 'POST' && r.url().endsWith('/qa-pause-2/decide'));
    await third.getByRole('button', { name: 'Save Note', exact: true }).click();
    await submitted;
    await refresh();
    assert.equal(await third.getAttribute('aria-busy'), 'true');
    assert.equal(await third.locator('textarea').isDisabled(), true);
    assert.equal(await third.locator('textarea').inputValue(), 'Keep this draft across refresh and disclosure');
    release();
    await third.getByText('Note saved. This Bead remains paused.', { exact: true }).waitFor();
    await page.waitForFunction(() => document.querySelector('[data-bead-id="qa-pause-2"]').getAttribute('aria-busy') === 'false');
    await toggle.click();
    await page.setViewportSize({ width: 1440, height: 900 });
    for (const selector of ['#factory-status', '#delivery-pool-status', '#merger-pool-status', '.slot-activity']) {
      for (const element of await page.locator(selector).all()) {
        assert.ok(await element.evaluate(e => e.getBoundingClientRect().bottom <= 900), `Busy overview exposes ${selector}`);
      }
    }
    items[0].title = 'LongTitleWithoutSpaces'.repeat(10).slice(0, 200);
    items[0].sourceAuthor = 'long-author-'.repeat(20);
    items[0].question = 'Full current question and diagnostics must remain reachable. '.repeat(30);
    await refresh();
    const workspaceLink = page.locator('.attention-workspace-link').first();
    await workspaceLink.focus();
    await refresh();
    assert.equal(await workspaceLink.evaluate(e => e === document.activeElement), true);
    await page.locator('.attention-context > summary').first().click();
    await page.locator('.attention-context > summary').first().focus();
    items[0].repairCycles++;
    await refresh();
    assert.equal(await page.locator('.attention-context').first().getAttribute('open'), '');
    assert.equal(await page.locator('.attention-context > summary').first().evaluate(e => e === document.activeElement), true);
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 900 });
      await shot(`long-expanded-${width}`);
    }
    fail = true;
    factory.availability = 'stale';
    await refresh();
    assert.equal(await page.locator('.attention-card').count(), 2);
    assert.match(await page.locator('#attention-status').innerText(), /last confirmed/);
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 900 });
      await shot(`retained-error-${width}`);
    }
    fail = false;
    await page.locator('.attention-context > summary').first().focus();
    setIdle();
    await refresh();
    assert.equal(await page.locator('#attention-status').evaluate(e => e === document.activeElement), true);
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 900 });
      await shot(`idle-${width}`);
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    assert.ok(await page.locator('#attention').evaluate(e => e.getBoundingClientRect().height <= 64), 'Compact clear row');
    assert.ok(await page.locator('#beads-title').evaluate(e => e.getBoundingClientRect().bottom <= 900), 'Idle Explorer visible');
    assert.ok(await page.locator('#beads-table-body tr').first().evaluate(e => e.getBoundingClientRect().bottom <= 900), 'Idle initial Explorer result visible');
    fail = true;
    await refresh();
    assert.equal(await page.locator('#attention-empty').isVisible(), false);
    assert.equal(await page.locator('#attention').evaluate(e => e.classList.contains('attention-clear')), false);
    await shot('empty-read-failed');
    assert.deepEqual(errors, []);
    console.log('Integrated console bounds, disclosures, drafts, focus, hierarchy, responsive and retained-error checks passed.');
  }
} finally { await browser.close(); }
