// Representative shared presentation smoke. Detailed native type/schema decisions
// remain in BeadPresentationTests, CommentPresentationTests and WorkspaceRelationsWebTests.
import assert from 'node:assert/strict';
import { chromium } from 'playwright';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
const base = process.env.NIGHTWEAVER_QA_URL;
assert(base, 'Use the isolated discovered direct endpoint');
const output = path.resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/studio-presentation');
await mkdir(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.setDefaultTimeout(90000);
  const errors = [], external = [], posts = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => {
    if (new URL(r.url()).origin !== new URL(base).origin) external.push(r.url());
    if (r.method() === 'POST') posts.push(r.url());
  });
  const { projectId } = await (await page.request.get(base + '/api/studio/context')).json();
  const rows = await (await page.request.get(base + '/api/beads')).json();
  const bead = rows.find(row => row.type === 'feature') || rows[0];
  assert(bead);
  const detail = await (await page.request.get(`${base}/api/beads/${bead.id}`)).json();
  assert(detail.presentation, 'native Workspace carries authoritative presentation');
  let mode = 'supported';
  const message = { state: 'supported', title: 'Reported verification failure', summary: '**Inspect retained evidence**',
    role: 'QA', stage: 'stage:qa', sourceId: 'active', original: '{"schema":"read-fixture","literal":"<script>unsafe</script>"}',
    sections: [{ label: 'Evidence', details: true, commentId: null,
      text: '[Allowed](https://example.com/evidence) [Unsafe](javascript:alert(1)) [Local](file:///H:/private) [Credentials](https://user:secret@example.invalid/) ![Remote](https://example.invalid/pixel)' }] };
  await page.route('**/api/operations/v1/projects/*/resources?**', async route => {
    if (new URL(route.request().url()).searchParams.get('key') !== `workspace:${bead.id}`) return route.continue();
    const actual = await route.fetch(); const envelope = await actual.json();
    const data = structuredClone(detail);
    data.sourceMetadata = '{"literal":"<script>source</script>","long":"' + 'evidence '.repeat(150) + '"}';
    data.comments = [{ id: 'active', author: 'Fixture', createdAt: new Date().toISOString(), kind: 'ordinary', body: message.original,
      message: mode === 'fallback' ? { ...message, state: 'fallback', title: 'Inspect message', summary: 'Unsupported contract; current explanation is uncertain.', sections: [] } : message }];
    data.history.activeCommentId = 'active';
    const relation = (type, direction, i) => ({ id: `qa-${type}-${direction}-${i}`, title: `Related <script>literal</script> ${i}`,
      type, direction, role: 'Outside Nightweaver lifecycle', issueType: 'chore', status: 'open' });
    const childCount = mode === 'shrink' ? 16 : mode === 'tiny' ? 1 : mode === 'removed' ? 0 : 43;
    data.relations = [
      ...Array.from({ length: childCount }, (_, i) => relation('parent-child', 'incoming', i)),
      relation('parent-child', 'outgoing', 0),
      ...Array.from({ length: 17 }, (_, i) => relation('blocks', 'incoming', i)),
      relation('blocks', 'outgoing', 0), relation('discovered-from', 'incoming', 0), relation('discovered-from', 'outgoing', 0),
      relation('custom-kind', 'incoming', 0), relation('custom-kind', 'outgoing', 0)
    ];
    data.relationObservation = mode === 'partial' ? { incoming: 'unavailable', outgoing: 'complete' } : { incoming: 'complete', outgoing: 'complete' };
    if (mode === 'partial') data.relations = [];
    envelope.data = data; envelope.health = 'fresh'; envelope.error = null; envelope.observedAt = new Date().toISOString();
    for (const source of Object.values(envelope.sources)) { source.health = 'fresh'; source.error = null; source.observedAt = envelope.observedAt; }
    await route.fulfill({ response: actual, json: envelope });
  });
  const workspace = `${base}/projects/${encodeURIComponent(projectId)}/beads/${encodeURIComponent(bead.id)}`;
  await page.goto(workspace);
  const facts = page.locator('details').filter({ has: page.locator('summary', { hasText: 'Workflow and source facts' }) });
  await facts.locator('summary').click(); await facts.locator('summary').focus();
  assert((await facts.innerText()).includes(detail.presentation.ownership));
  assert((await facts.locator('pre').textContent()).includes('<script>source</script>'));
  await page.waitForTimeout(2300);
  assert(await facts.locator('summary').evaluate(n => n === document.activeElement && n.parentElement.open));
  const relations = page.locator('details').filter({ has: page.locator('summary').filter({ hasText: /^Relations ·/ }) });
  await relations.locator('summary').click();
  const children = relations.getByRole('region', { name: 'Children', exact: true });
  const blocks = relations.getByRole('region', { name: 'Blocks', exact: true });
  const range = group => group.locator('[data-relation-range]');
  const next = group => group.getByRole('button', { name: 'Next', exact: true });
  const previous = group => group.getByRole('button', { name: 'Previous', exact: true });
  const keyboard = async control => { await control.focus(); await page.keyboard.press('Enter'); };
  assert.equal(await relations.getByRole('region').count(), 8);
  for (const name of ['Parent', 'Blocked by', 'Discovered from', 'Discovered work', 'custom-kind · incoming', 'custom-kind · outgoing'])
    assert.equal(await relations.getByRole('region', { name, exact: true }).getByRole('link').count(), 1);
  assert.equal(await children.getByRole('link').count(), 15);
  assert(await previous(children).isDisabled());
  await keyboard(next(children));
  await range(children).filter({ hasText: 'Showing 16–30 of 43' }).waitFor();
  assert.equal(await children.getByRole('link').first().getAttribute('href'), `/projects/${encodeURIComponent(projectId)}/beads/qa-parent-child-incoming-15`);
  await keyboard(next(blocks));
  await range(blocks).filter({ hasText: 'Showing 16–17 of 17' }).waitFor();
  assert(await previous(blocks).evaluate(n => n === document.activeElement), 'last-page Next moves focus to Previous');
  await keyboard(next(children));
  await range(children).filter({ hasText: 'Showing 31–43 of 43' }).waitFor();
  assert(await next(children).isDisabled());
  assert(await previous(children).evaluate(n => n === document.activeElement));
  await page.waitForTimeout(2300);
  assert.equal(await range(children).textContent(), 'Showing 31–43 of 43');
  assert.equal(await range(blocks).textContent(), 'Showing 16–17 of 17');
  assert(await previous(children).evaluate(n => n === document.activeElement), 'refresh preserves local focus and pages');
  await children.getByRole('link').last().focus();
  mode = 'shrink';
  await range(children).filter({ hasText: 'Showing 16–16 of 16' }).waitFor();
  assert(await previous(children).evaluate(n => n === document.activeElement), 'removed row restores focus inside its group');
  mode = 'tiny';
  await range(children).filter({ hasText: 'Showing 1–1 of 1' }).waitFor();
  assert(await range(children).evaluate(n => n === document.activeElement), 'no enabled control falls back to range');
  assert(await previous(children).isDisabled()); assert(await next(children).isDisabled());
  mode = 'removed';
  await children.waitFor({ state: 'detached' });
  assert(await relations.locator(':scope > summary').evaluate(n => n === document.activeElement), 'removed group falls back to Relations summary');
  mode = 'supported';
  await range(children).filter({ hasText: 'Showing 1–15 of 43' }).waitFor();
  assert.equal(await range(blocks).textContent(), 'Showing 16–17 of 17', 'other group page survives shrink/removal');
  await keyboard(next(children));
  await keyboard(previous(children));
  assert(await previous(children).isDisabled());
  assert(await next(children).evaluate(n => n === document.activeElement), 'first-page Previous moves focus to Next');
  const ids = new Set();
  for (let pageIndex = 0; pageIndex < 3; pageIndex++) {
    for (const href of await children.getByRole('link').evaluateAll(nodes => nodes.map(n => n.getAttribute('href')))) ids.add(href);
    assert(await children.getByRole('link').count() <= 15);
    if (pageIndex < 2) await keyboard(next(children));
  }
  assert.equal(ids.size, 43, 'every child is reachable without exceeding the page bound');
  assert((await relations.innerText()).includes('Type: chore'));
  for (const width of [1440,375,720]) {
    await page.setViewportSize({ width, height: 900 });
    await page.evaluate(() => window.scrollTo(0, 0));
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({ path: path.join(output, `facts-${width}.png`), fullPage: true });
  }
  await page.getByRole('link', { name: 'History', exact: true }).click();
  const active = page.locator('.bead-comment').first();
  await active.locator(':scope > summary').click();
  await active.getByText('Evidence', { exact: true }).click();
  const allowed = active.getByRole('link', { name: 'Allowed', exact: true });
  assert.equal(await allowed.getAttribute('rel'), 'noopener noreferrer');
  await allowed.focus();
  await page.waitForTimeout(2300);
  assert(await allowed.evaluate(n => n === document.activeElement));
  assert.equal(await page.locator('.bead-evidence img, .bead-evidence script, .bead-evidence a[href^="javascript:"], .bead-evidence a[href^="file:"]').count(), 0);
  await active.getByText('Original source message', { exact: true }).click();
  assert.equal(await active.locator('pre').last().textContent(), message.original);
  mode = 'fallback';
  await active.getByRole('heading', { name: 'Inspect message', exact: true }).waitFor();
  assert((await active.innerText()).includes('current explanation is uncertain'));
  mode = 'partial';
  await page.getByRole('link', { name: 'Summary', exact: true }).click();
  await relations.getByText('Incoming unavailable · Outgoing complete', { exact: true }).waitFor();
  assert.equal(await relations.getByRole('link').count(), 0);
  assert.deepEqual(errors, []); assert.deepEqual(external, []); assert.deepEqual(posts, []);
  const result = { passed: true, checks: ['native workflow/source facts', 'eight kind/direction groups', '43 children reachable in at most 15 rows per page', 'independent retained pages', 'shrink/removal/regrowth clamping', 'keyboard first/last boundary and removed-row/group focus', 'partial directional state', 'supported/fallback evidence', 'original source', 'safe keyboard links and focus', '1440/375/720 reflow'], errors, external, posts };
  await writeFile(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
} finally { await browser.close(); }
