import { chromium } from 'playwright';
import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';

const url = process.env.NIGHTWEAVER_QA_URL;
const config = process.env.NIGHTWEAVER_QA_CONFIG;
assert(url && config, 'Use the exact isolated direct endpoint and config.');
assert.match(config, /nightweaver-qa-[a-f0-9]+[\\/]project.json$/);
const output = resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/activity');
await mkdir(output, { recursive: true });
const started = performance.now();
const browser = await chromium.launch({ headless: true });
const errors = [], mutations = [];
const results = { native: {}, presentation: {}, captures: [], complete: false };
const until = async (check, message) => {
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) { if (await check()) return; await new Promise(r => setTimeout(r, 100)); }
  throw new Error(message);
};
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (!['GET', 'HEAD'].includes(request.method()) && !/\/api\/operations\/v1\/projects\/qa\/refresh$/.test(request.url())) mutations.push(request.url()); });
  const { projectId } = await (await page.request.get(`${url}/api/studio/context`)).json();
  assert.equal(projectId, 'qa');
  const base = `/projects/${projectId}`;
  let native;
  await until(async () => {
    native = await (await page.request.get(`${url}/api/activity/v1/projects/qa/history`)).json();
    return native.records.some(row => row.kind === 'InvocationStarted' && row.bead === 'qa-live-1');
  }, 'Real runtime invocation did not reach journal and API.');
  await writeFile(join(output, 'native-history.json'), JSON.stringify(native, null, 2));
  await page.goto(`${url}${base}/activity`);
  await page.getByText('Agent invocation started.', { exact: true }).first().waitFor();
  const startedRow = native.records.find(row => row.kind === 'InvocationStarted' && row.bead === 'qa-live-1');
  const idOf = row => `1.${row.position.journal.replaceAll('-', '')}.${row.position.offset}`;
  const liveRow = page.locator(`[data-activity-id="${idOf(startedRow)}"]`);
  await liveRow.getByRole('button', { name: 'Show evidence' }).click();
  await liveRow.getByRole('button', { name: 'Open exact invocation output' }).click();
  await liveRow.getByLabel('Exact invocation output').filter({ hasText: 'slot-one' }).waitFor();
  await liveRow.getByRole('button', { name: 'Close invocation output' }).click();
  await page.getByRole('button', { name: 'Return to latest' }).click();
  await writeFile(join(dirname(config), 'qa-live-1.release'), 'release');
  // Completion occurs after browser subscription. It uses the production executor hook,
  // journal writer, shared Web collector, SSE endpoint and React renderer.
  await page.getByText('Agent invocation completed. Outcome is not confirmed.', { exact: true }).waitFor({ timeout: 20000 });
  results.native = { journal: startedRow.position.journal, runtime: startedRow.position.runtime, invocation: startedRow.invocation,
    startedCursor: idOf(startedRow), realCompletionAfterSubscription: true, exactInvocationOutput: true };
  await page.screenshot({ path: join(output, 'native-1440.png') });
  await page.getByRole('navigation', { name: 'Project navigation' }).getByRole('link', { name: 'Catch up', exact: true }).click();
  await page.getByRole('heading', { name: 'Catch up', level: 1, exact: true }).waitFor();

  // Presentation substitutions are separate from the native evidence above.
  let journal = '11111111-1111-1111-1111-111111111111';
  const runtime = '22222222-2222-2222-2222-222222222222';
  let records = [], unavailable = false, replaced = false;
  const phases = ['Planning', 'Delivery', 'Qa', 'Merger', 'Runtime'];
  const examples = [
    ['PlanningPublished', 'Confirmed', 'Plan publication confirmed.', 'None'],
    ['InvocationStarted', 'Observed', 'Agent invocation started.', 'None'],
    ['QaAdmitted', 'Confirmed', 'QA admission confirmed.', 'None'],
    ['MergeVerified', 'Verified', 'Merge verification passed.', 'None'],
    ['EvidenceUnavailable', 'Unknown', 'Merge evidence unavailable. Outcome is not confirmed.', 'MissingEvidence'],
    ['PauseObserved', 'Observed', 'Human pause observed. Safe reason: Environment blocked.', 'EnvironmentBlocked'],
    ['WaitStarted', 'Observed', 'Pool waits for capacity.', 'Capacity'],
    ['RecoveryCompleted', 'Confirmed', 'Startup recovery completed.', 'Recovered'],
    ['HealthChanged', 'Observed', 'Runtime health changed.', 'RuntimeFailed'],
  ];
  const row = offset => {
    const [kind, strength, message, reason] = examples[Math.max(0, offset - 1) % examples.length];
    return { schema: 1, position: { journal, runtime, offset, sequence: offset }, kind, strength, message, reason,
      phase: phases[(offset - 1) % phases.length], bead: offset % 2 ? 'qa-visible.1' : 'qa-visible.2', invocation: null,
      merge: kind === 'MergeVerified' ? { pullRequest: 'https://github.com/nightweaver-qa/fixture/pull/1' } : null,
      observedAt: '2026-11-25T12:00:10Z', sourceTime: '2026-11-25T12:00:00Z', source: 'Inventory', sourceReference: 'safe-reference',
      freshness: offset % 2 ? 'Fresh' : 'Stale', count: 3, limit: 3 };
  };
  records = Array.from({ length: 300 }, (_, i) => row(i + 1));
  records[298].message = '<script>literal untrusted text</script> ' + 'Long safe observation with a complete source identity. '.repeat(30);
  const levelOf = r => r.reason === 'RuntimeFailed' ? 'error' : ['EvidenceUnavailable', 'PauseObserved'].includes(r.kind) ? 'warning' : 'work';
  const detail = r => ({ id: idOf(r), availability: r.kind === 'EvidenceUnavailable' ? 'unavailable' : 'available',
    workspace: `/projects/qa/beads/${r.bead}?tab=history`, pullRequest: r.kind === 'MergeVerified' ? 'https://github.com/nightweaver-qa/fixture/pull/1' : null });
  const status = rows => ({ oldest: 1, newest: records.length, loadedThrough: records.length, loading: false, state: replaced ? 'Expired, Gap' : 'Ready',
    journalHealth: unavailable ? 'Unavailable' : 'Fresh', runtimeHealth: unavailable ? 'Unavailable' : 'Live',
    runtimeId: replaced ? journal : runtime, runtimeReplaced: replaced, observedAt: new Date().toISOString(), runtimeObservedAt: new Date().toISOString(), details: rows.map(detail) });
  const select = search => {
    const q = new URLSearchParams(search);
    const cursor = q.get('cursor')?.split('.');
    const after = cursor?.[1] === journal.replaceAll('-', '') ? Number(cursor.at(-1)) : 0;
    return records.filter(r => r.position.offset > after && (!q.get('bead') || r.bead === q.get('bead')) && (!q.get('level') || levelOf(r) === q.get('level'))).slice(0, 100);
  };
  await page.route('**/api/activity/v1/projects/qa/history?*', async route => {
    const rows = unavailable ? [] : select(new URL(route.request().url()).search);
    await route.fulfill({ json: { ...status(rows), records: rows, cursor: idOf(rows.at(-1) ?? row(records.length)) } });
  });
  await page.addInitScript(() => {
    const Original = window.EventSource;
    window.__activitySources = []; window.__activityRequests = [];
    window.EventSource = class {
      constructor(url) {
        if (!String(url).includes('/api/activity/')) return new Original(url);
        this.url = url; this.listeners = {}; this.closed = false;
        window.__activitySources.push(this); window.__activityRequests.push(url);
      }
      addEventListener(name, listener) { (this.listeners[name] ??= []).push(listener); }
      emit(name, data, id = '') { if (!this.closed) for (const listener of this.listeners[name] ?? []) listener({ data: JSON.stringify(data), lastEventId: id }); }
      close() { this.closed = true; }
    };
  });
  await page.reload();
  await page.getByRole('navigation', { name: 'Project navigation' }).getByRole('link', { name: 'Activity', exact: true }).click();
  await page.locator('.activity-row').first().waitFor();
  async function emit(rows, extra = {}) {
    await until(() => page.evaluate(() => window.__activitySources.some(s => !s.closed)), 'Missing presentation stream.');
    await page.evaluate(({ rows, status }) => {
      const stream = window.__activitySources.findLast(s => !s.closed);
      stream.emit('open', {}); stream.emit('status', status);
      for (const r of rows) { const id = `1.${r.position.journal.replaceAll('-', '')}.${r.position.offset}`; stream.emit('activity', r, id); }
      if (rows.length) stream.emit('cursor', {}, `1.${rows.at(-1).position.journal.replaceAll('-', '')}.${rows.at(-1).position.offset}`);
    }, { rows, status: { ...status(rows), ...extra } });
  }
  await emit([]);
  assert.equal(await page.locator('.activity-row').count(), 100);
  const first = page.locator('.activity-row').first();
  const anchor = await first.getAttribute('data-activity-id');
  await page.getByLabel('Activity records', { exact: true }).evaluate(node => { node.scrollTop = 0; });
  await first.getByRole('button', { name: 'Show evidence' }).focus();
  await page.keyboard.press('Enter');
  const before = await first.evaluate(node => node.getBoundingClientRect().top - node.parentElement.parentElement.getBoundingClientRect().top);
  await page.getByRole('button', { name: 'Load older records' }).click();
  await until(async () => await page.locator('.activity-row').count() === 200, 'Older rows not loaded.');
  const retained = page.locator(`[data-activity-id="${anchor}"]`);
  const after = await retained.evaluate(node => node.getBoundingClientRect().top - node.parentElement.parentElement.getBoundingClientRect().top);
  assert(Math.abs(before - after) < 3, `Older loading moved anchor by ${after - before}px`);
  assert.equal(await retained.getByRole('button', { name: 'Hide evidence' }).getAttribute('aria-expanded'), 'true');
  await retained.getByRole('button', { name: 'Hide evidence' }).focus();
  records.push(...Array.from({ length: 900 }, (_, i) => row(i + 301)));
  for (let start = 300; start < records.length; start += 100) await emit(records.slice(start, start + 100));
  assert.equal(await page.locator('.activity-row').count(), 200);
  assert.equal(await retained.getByRole('button', { name: 'Hide evidence' }).evaluate(node => node === document.activeElement), true);
  await page.getByText('500+ new matching records', { exact: true }).waitFor();
  for (const width of [1440, 720, 375]) {
    await page.setViewportSize({ width, height: 1000 });
    await retained.scrollIntoViewIfNeeded();
    await page.evaluate(() => window.scrollTo(0, 0));
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `Page overflow at ${width}`);
    assert(await page.locator('.activity-scroll').evaluate(node => node.scrollWidth <= node.clientWidth), `Feed overflow at ${width}`);
    const name = `activity-expanded-${width}.png`;
    await page.screenshot({ path: join(output, name), fullPage: true }); results.captures.push(name);
  }
  await emit([], { journalHealth: 'Unavailable', runtimeHealth: 'Stale', runtimeReplaced: true, state: 'Expired, Gap' });
  await page.getByText(/Runtime replaced\. Prior records/).waitFor();
  await page.getByText(/History gap\./).waitFor();
  assert.match(await page.locator('.activity-health').innerText(), /Journal: Unavailable · Runtime: Stale/);
  await page.getByRole('button', { name: 'Return to latest' }).click();
  await until(() => page.evaluate(() => window.__activityRequests.length >= 3), 'Return did not reconnect from a cursor.');
  for (let start = 400; start < records.length; start += 100) await emit(records.slice(start, start + 100));
  await until(async () => await page.locator('.activity-row').count() === 500, 'Catch-up did not retain bounded rows.');
  assert.equal(await page.locator('.activity-evidence').count(), 0);
  assert.match(await page.locator('.activity-row').last().innerText(), /qa-visible/);
  // Reconnect keeps the last examined identity and never creates two active streams.
  const requestCount = await page.evaluate(() => window.__activityRequests.length);
  await page.evaluate(() => window.__activitySources.findLast(s => !s.closed).onerror());
  await until(() => page.evaluate(count => window.__activityRequests.length > count, requestCount), 'Reconnect did not occur.');
  assert.equal(await page.evaluate(() => window.__activitySources.filter(s => !s.closed).length), 1);
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: true }); document.dispatchEvent(new Event('visibilitychange')); });
  assert.equal(await page.evaluate(() => window.__activitySources.filter(s => !s.closed).length), 0);
  await page.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: false }); document.dispatchEvent(new Event('visibilitychange')); });
  await page.getByText('Browsing paused', { exact: true }).waitFor();
  await emit([]);
  // Exact URL filters apply to history and reconnect. Back and reload retain them.
  await page.getByLabel('Record level', { exact: true }).selectOption('error');
  await page.getByLabel('Exact Bead ID', { exact: true }).fill('qa-visible.1');
  await page.getByRole('button', { name: 'Apply filters' }).click();
  await page.waitForURL('**?level=error&bead=qa-visible.1');
  await until(async () => {
    const rows = await page.locator('.activity-row').allTextContents();
    return rows.length > 0 && rows.every(text => text.includes('qa-visible.1') && text.includes('error'));
  }, 'Filtered route did not settle to its selected rows.');
  assert((await page.locator('.activity-row').allTextContents()).every(text => text.includes('qa-visible.1') && text.includes('error')));
  await page.reload();
  await page.getByLabel('Exact Bead ID', { exact: true }).waitFor();
  assert.equal(await page.getByLabel('Exact Bead ID', { exact: true }).inputValue(), 'qa-visible.1');
  await page.getByLabel('Exact Bead ID', { exact: true }).fill('qa-no-matches');
  await page.getByRole('button', { name: 'Apply filters' }).click();
  await page.getByText('No matching records in the loaded history.', { exact: true }).waitFor();
  unavailable = true;
  await page.reload();
  await page.getByText('Activity source unavailable. Other console views remain available.', { exact: true }).waitFor();
  unavailable = false;
  await page.goto(`${url}${base}/activity`);
  await page.locator('.activity-row').first().waitFor();
  const verified = page.locator('.activity-row').filter({ hasText: 'Merge verification passed.' }).first();
  await verified.getByRole('button', { name: 'Show evidence' }).click();
  assert.equal(await verified.getByRole('link', { name: 'Open verified pull request' }).getAttribute('href'), 'https://github.com/nightweaver-qa/fixture/pull/1');
  const unsupported = page.locator('.activity-row').filter({ hasText: 'Merge evidence unavailable.' }).first();
  await unsupported.getByRole('button', { name: 'Show evidence' }).click();
  assert.equal(await unsupported.getByRole('link', { name: 'Open verified pull request' }).count(), 0);
  assert.equal(await page.locator('.activity-row script').count(), 0);
  await verified.getByRole('link', { name: 'Open source in Workspace' }).click();
  await page.waitForURL('**/beads/qa-visible.*?tab=history');
  await until(() => page.evaluate(() => window.__activitySources.every(s => s.closed)), 'Activity stream survived route disposal.');
  assert.equal(await page.evaluate(() => window.__activitySources.filter(s => !s.closed).length), 0);
  await page.goBack();
  await page.getByRole('heading', { name: 'Activity', exact: true, level: 1 }).waitFor();
  // Repair regression: 450 displayed rows plus a 100-offset older window.
  replaced = false; records = Array.from({ length: 200 }, (_, i) => row(i + 1));
  await page.goto(`${url}${base}/activity`);
  await until(async () => await page.locator('.activity-row').count() === 100, 'Initial older regression rows missing.');
  const arrivals = Array.from({ length: 350 }, (_, i) => row(i + 201));
  records.push(...arrivals);
  for (let i = 0; i < arrivals.length; i += 100) await emit(arrivals.slice(i, i + 100));
  await until(async () => await page.locator('.activity-row').count() === 450, '450-row setup did not settle.');
  const newestId = idOf(row(550));
  const newest = page.locator(`[data-activity-id="${newestId}"]`);
  await newest.getByRole('button', { name: 'Show evidence' }).click();
  await newest.scrollIntoViewIfNeeded();
  const visibleBefore = await page.getByLabel('Activity records', { exact: true }).evaluate(host =>
    [...host.querySelectorAll('[data-activity-id]')].find(node => node.getBoundingClientRect().bottom > host.getBoundingClientRect().top).dataset.activityId);
  const visible = page.locator(`[data-activity-id="${visibleBefore}"]`);
  const visibleTop = await visible.evaluate(node => node.getBoundingClientRect().top);
  // A programmatic click keeps the viewport on its newest visible row.
  await page.getByRole('button', { name: 'Load older records' }).evaluate(button => button.click());
  await until(async () => await page.locator('.activity-row').count() === 500, 'Capacity-limited older load failed.');
  const capacityAnchorDelta = await visible.evaluate(node => node.getBoundingClientRect().top) - visibleTop;
  assert(Math.abs(capacityAnchorDelta) < 3, `Capacity load moved anchor by ${capacityAnchorDelta}px`);
  assert.equal(await newest.getByRole('button', { name: 'Hide evidence' }).count(), 1);
  assert.equal(await page.locator('.activity-row').first().getAttribute('data-activity-id'), idOf(row(51)));
  assert.equal(await page.locator('.activity-row').last().getAttribute('data-activity-id'), newestId);
  assert(await page.getByRole('button', { name: 'Load older records' }).isDisabled());
  await page.screenshot({ path: join(output, 'repair-older-1440.png') });
  // Empty replacement scans must not hide the first later matching record.
  const replacements = [];
  for (const transport of ['SSE', 'history']) for (const paused of [false, true]) {
    records = Array.from({ length: 200 }, (_, i) => row(i + 1));
    await page.goto(`${url}${base}/activity`);
    await until(async () => await page.locator('.activity-row').count() === 100, 'Replacement setup missing.');
    const held = page.locator('.activity-row').last();
    const heldId = await held.getAttribute('data-activity-id');
    if (paused) await held.getByRole('button', { name: 'Show evidence' }).click();
    journal = `${String(3 + replacements.length).repeat(8)}-3333-3333-3333-333333333333`;
    records = []; replaced = true;
    if (transport === 'SSE') {
      await emit([]);
      await page.evaluate(cursor => window.__activitySources.findLast(s => !s.closed).emit('cursor', {}, cursor), idOf(row(0)));
      if (paused) {
        assert.equal(await page.locator('.activity-row').last().getAttribute('data-activity-id'), heldId);
        assert.equal(await held.getByRole('button', { name: 'Hide evidence' }).count(), 1);
        assert(await page.getByRole('button', { name: 'Load older records' }).isDisabled());
      } else await until(async () => await page.locator('.activity-row').count() === 0, 'Empty SSE replacement retained old rows while following.');
    } else {
      await page.getByRole('button', { name: 'Return to latest' }).click();
      await until(async () => await page.locator('.activity-row').count() === 0, 'Empty history replacement retained old rows.');
    }
    const matching = row(1); records.push(matching); await emit([matching]);
    if (transport === 'SSE' && paused) {
      assert.equal(await page.locator('.activity-row').last().getAttribute('data-activity-id'), heldId);
      await page.getByRole('button', { name: 'Return to latest' }).click();
    }
    await page.locator(`[data-activity-id="${idOf(matching)}"]`).waitFor();
    assert.equal(await page.locator('.activity-row').count(), 1);
    replacements.push({ transport, paused, laterMatchingVisible: true });
  }
  results.presentation = { boundedRows: 500, pendingCount: '500+', olderAnchorDelta: after - before, focusHeld: true,
    keyboard: true, filtersAndDirectReload: true, emptyAndUnavailable: true, sourceAndRuntimeHealth: true,
    verifiedAndUnsupportedMerge: true, reconnectAndSuspension: true, streamDisposal: true,
    olderCapacity: { rows: 500, firstOffset: 51, lastOffset: 550, openEvidenceHeld: true, capacityAnchorDelta }, replacements };
  assert.deepEqual(errors, []); assert.deepEqual(mutations, []);
  results.complete = true;
} finally {
  results.wallSeconds = (performance.now() - started) / 1000;
  results.errors = errors; results.mutations = mutations;
  await writeFile(join(output, 'result.json'), JSON.stringify(results, null, 2));
  await browser.close();
}
console.log(JSON.stringify(results, null, 2));
