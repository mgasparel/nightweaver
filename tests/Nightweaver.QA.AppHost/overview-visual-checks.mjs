import { readAttentionFixture, attentionBase, attentionHistory } from './attention-read-fixtures.mjs';
import assert from 'node:assert/strict';

// Same read-only fixture before and after the visual pass, on the existing stack.
export async function overviewVisualChecks(page, url, base, projectId, output) {
  const attention = await readAttentionFixture(page, url, projectId);
  const factory = await (await page.request.get(`${url}/api/factory/state`)).json();
  assert.ok(attention.data.length && factory.snapshot.delivery.slots[0].invocation);
  const decision = { ...attention.data[0], beadId: 'qa-context-0', title: 'Archived project navigation', kind: 'product-decision-required', question: 'Should archived projects stay in the sidebar?',
    message: { state: 'supported', role: 'Planner', sections: [{label: 'Recommended answer', text: 'Move them to a separate archive.', commentId: 'visual-source'}] } };
  const data = structuredClone(factory);
  data.availability = 'live';
  data.snapshot.runtime_status = 'running';
  const template = data.snapshot.delivery.slots[0];
  data.snapshot.delivery = {capacity: 3, slots: ['implement', 'review'].map((stage, i) => ({...structuredClone(template), slot_id: `delivery-${i + 1}`, bead_id: `ns-${241 + i}`, status: 'running', stage: `stage:${stage}`, role: i ? 'Reviewer' : 'Implementer', invocation: {...template.invocation, bead_id: `ns-${241 + i}`, status: 'running'}}))};
  data.snapshot.planning = {capacity: 0, slots: []}; data.snapshot.merger = {capacity: 1, slots: [{slot_id: 'merger-1', status: 'idle'}]};
  const completed = ['Keep catch-up summaries compact', 'Add keyboard navigation to collections'].map((title, i) => ({id: `ns-done-${i}`, title, type: 'task', priority: 2, status: 'closed', activityKind: 'completed', activityAt: new Date(Date.now() - (i + 1) * 32 * 60000).toISOString(), outcomeLabel: 'Completed', summary: '', commits: [], pullRequests: []}));
  let mode = 'controlled';
  const handler = async route => {
    const key = new URL(route.request().url()).searchParams.get('key');
    if (!['base:attention', 'factory', 'digest:auto'].includes(key) && !key?.startsWith('section:')) return route.continue();
    const actual = key.startsWith('section:') ? null : await route.fetch(); const value = actual ? await actual.json() : { ...attention, key, revision: "18446744073709551614" };
    value.health = 'fresh'; value.error = null; value.observedAt = new Date().toISOString();
    for (const source of Object.values(value.sources)) { source.health = 'fresh'; source.error = null; source.observedAt = value.observedAt; }
    value.data = key === 'base:attention' ? attentionBase([decision]) : key.startsWith('section:') ? attentionHistory(key, [decision]) : key === 'factory' ? data : {generatedAt: value.observedAt, window: '24h', lastCaughtUpAt: null, isCaughtUp: false, metrics: {completedTotal: 2, activeThreads: 0, openOrUpdatedTotal: 0, waitingForGuidance: 1}, threads: [], standaloneCompleted: completed, standaloneOpen: []};
    if (key === 'digest:auto') {
      if (mode === 'empty') { value.data.standaloneCompleted = []; value.data.metrics.completedTotal = 0; }
      if (mode === 'long') value.data.standaloneCompleted = completed.map(item => ({...item, title: item.title.repeat(15)}));
      if (mode === 'stale') { value.health = 'stale'; value.error = {code: 'fixture', message: 'Retained outcomes need verification', retryable: true}; }
      if (mode === 'unavailable') { value.health = 'unavailable'; value.data = null; }
    }
    await route.fulfill({response: actual, json: value});
  };
  await page.route('**/api/operations/v1/projects/*/resources?**', handler);
  try {
    for (const width of [1440, 720, 375]) {
      await page.setViewportSize({width, height: 900});
      await page.goto(url + base + '/overview');
      await page.locator('.attention-question').filter({hasText: decision.question}).waitFor();
      await page.locator('[data-slot-id="delivery-2"]').waitFor();
      await page.getByText(/2 completed/).first().waitFor();
      await page.waitForFunction(() => document.querySelector('.synchronization')?.textContent.includes('Live connection'));
      await page.locator('h1').evaluate(node => node.blur());
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({path: `${output}/overview-controlled-${width}.png`, fullPage: true});
    }
    if (await page.locator('.digest-outcomes').count()) {
      assert.equal(await page.locator('.digest-outcomes li').count(), 2);
      assert.equal(await page.locator('#digest-mark-caught-up, #digest-toggle').count(), 0);
      for (mode of ['empty', 'stale', 'unavailable', 'long']) {
        for (const width of [1440, 720, 375]) {
          await page.setViewportSize({width, height: 900});
          await page.goto(url + base + '/overview');
          if (mode === 'empty') await page.getByText('No completed outcomes in this observation.').waitFor();
          if (mode === 'stale') await page.getByRole('alert').filter({hasText: 'Retained outcomes need verification'}).waitFor();
          if (mode === 'unavailable') await page.locator('.digest-preview').getByText(/Digest unavailable/).waitFor();
          if (mode === 'long') await page.locator('.digest-outcomes h3').filter({hasText: completed[0].title.repeat(15)}).waitFor();
          assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
          await page.locator('.attention-row .primary').focus();
          assert.equal(await page.locator('.attention-row .primary').evaluate(node => node === document.activeElement), true);
          await page.screenshot({path: `${output}/overview-outcomes-${mode}-${width}.png`, fullPage: true});
        }
      }
    }
  } finally { await page.unroute('**/api/operations/v1/projects/*/resources?**', handler); }
}
