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
    data.relations = Array.from({ length: 40 }, (_, i) => ({ id: `qa-related-${i}`, title: `Related <script>literal</script> ${i}`,
      type: 'parent-child', direction: 'incoming', role: 'Outside Nightweaver lifecycle', issueType: 'chore', status: 'open' }));
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
  assert.equal(await relations.getByRole('link').count(), 15);
  await relations.getByRole('button', { name: 'Show 15 more relations' }).click();
  assert.equal(await relations.getByRole('link').count(), 30);
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
  const result = { passed: true, checks: ['native workflow/source facts', 'bounded typed relation links', 'partial directional state', 'supported/fallback evidence', 'original source', 'safe keyboard links and focus', '1440/375/720 reflow'], errors, external, posts };
  await writeFile(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
} finally { await browser.close(); }
