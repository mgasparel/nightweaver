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
  const duplicates = items.filter(i => /^Schema messages: duplicate /.test(i.title));
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
  assert.equal(duplicates.length, 2);
  assert.equal(await page.locator('.semantic-message img, .semantic-message script').count(), 0);
  const prUrl = 'https://github.com/mgasparel/nightweaver/pull/28';
  const prLink = history.locator(`.semantic-message a[href="${prUrl}"]`);
  assert.equal(await prLink.count(), 1);
  const evidence = prLink.locator('xpath=ancestor::details[1]');
  await evidence.locator(':scope > summary').click();
  await evidence.locator(':scope > summary').focus();
  await page.keyboard.press('Tab');
  assert(await prLink.evaluate(node => node === document.activeElement), 'PR link is keyboard reachable');
  await page.evaluate(() => loadWorkspace(workspaceCurrentId, { manual: true }));
  await page.waitForFunction(href => document.activeElement?.getAttribute('href') === href, prUrl);
  assert.equal(await prLink.getAttribute('rel'), 'noopener noreferrer');
  const linkSafety = await page.evaluate(() => ({
    allowed: ['https://github.com/mgasparel/nightweaver/pull/28', 'http://example.invalid/evidence'].map(value => !!messageEvidenceLink(value, 'test')),
    rejected: ['javascript:alert(1)', 'file:///H:/logs/check.log', 'data:text/html,hello', '//example.invalid/evidence',
      '/api/attention/act', 'https://user:secret@example.invalid/', 'https://example.invalid/\npath', 'https:\\example.invalid/'].map(value => !!messageEvidenceLink(value, 'test'))
  }));
  assert.deepEqual(linkSafety, { allowed: [true, true], rejected: Array(8).fill(false) });
  assert.equal(await history.locator('a[href^="javascript:"], a[href^="file:"], a[href^="data:"]').count(), 0);
  assert.match(await history.textContent(), /javascript:window.__schemaInjected=true/);
  assert.equal(await active.locator('a').count(), 0, 'Local log paths remain literal');
  assert.deepEqual(external, [], 'Rendering and expansion do not fetch evidence');
  await prLink.scrollIntoViewIfNeeded();
  await prLink.locator('xpath=ancestor::section[1]').screenshot({ path: `${output}/evidence-link-desktop.png` });

  // Intercept the explicitly activated destination, so QA never contacts GitHub.
  const navigations = [];
  await page.context().route(prUrl, async route => {
    navigations.push(route.request().url());
    await route.fulfill({ contentType: 'text/html', body: '<title>Evidence navigation fixture</title>' });
  });
  const popupPromise = page.waitForEvent('popup');
  await page.keyboard.press('Enter');
  const popup = await popupPromise;
  await popup.waitForLoadState();
  assert.equal(popup.url(), prUrl);
  assert.deepEqual(navigations, [prUrl]);
  await popup.close();
  await evidence.locator(':scope > summary').click();
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
  for (const item of duplicates) {
    assert.equal(item.message.state, 'fallback');
    assert.match(item.message.original, /"schema":false/);
    await workspace(item);
    assert.match(await active.innerText(), /Malformed Nightweaver message/);
    assert.doesNotMatch(await active.innerText(), /Older supported/);
    const current = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), item.beadId);
    assert.equal(current.history.activeCommentId, item.sourceCommentId);
    assert.deepEqual(current.attention.message, item.message);
    await page.locator('.workspace-older-history > summary').click();
    assert.match(await history.innerText(), /Older supported/);
    assert.match(await history.innerText(), /Later ordinary note/);
  }

  // Mixed delivery/audit/merge history remains attributable without changing the pause.
  await workspace(primary);
  await page.locator('.workspace-older-history').evaluateAll(nodes => nodes.forEach(node => { node.open = true; }));
  const mixed = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), primary.beadId);
  for (const comment of mixed.comments) if (comment.message) assert.equal(comment.message.original, comment.body, 'Exact original survives projection');
  assert.equal(mixed.comments.find(c => c.body.includes('"question":"merger_outcome"')).message.state, 'fallback');
  for (const title of ['Host-recorded delivery outcome', 'Human note', 'Human resume request', 'Human retry request', 'Human abort request',
    'Agent-reported merge', 'Host-recorded verified merge', 'Post-merge finalization diagnostic'])
    assert(await history.getByRole('heading', { name: title, exact: true }).count(), title);
  assert.match(await history.innerText(), /did not clear this pause/);
  assert.equal(mixed.attention.canRetry, true);
  assert.equal(mixed.history.activeCommentId, primary.sourceCommentId);
  const verified = history.locator('.semantic-message').filter({ has: page.getByRole('heading', { name: 'Host-recorded verified merge', exact: true }) });
  await verified.getByText('Inspect evidence references', { exact: true }).click();
  const sourceLink = verified.locator('a[href^="#workspace-comment-"]');
  assert.equal(await sourceLink.count(), 1, 'Source merge comment resolves within current history');
  await sourceLink.focus();
  await page.keyboard.press('Enter');
  assert(await page.evaluate(() => document.activeElement?.matches('.workspace-comment') && document.activeElement.textContent.includes('Agent-reported merge')));
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 1000 });
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
    await verified.screenshot({ path: `${output}/verified-merge-${width}.png` });
    await history.locator('.semantic-message').filter({ has: page.getByRole('heading', { name: 'Human resume request', exact: true }) })
      .screenshot({ path: `${output}/human-resume-${width}.png` });
  }
  await workspace(question);
  await page.locator('.workspace-older-history').evaluateAll(nodes => nodes.forEach(node => { node.open = true; }));
  const candidate = history.locator('.semantic-message').filter({ has: page.getByRole('heading', { name: 'Planning candidate', exact: true }) });
  assert.match(await candidate.innerText(), /40 tasks/);
  assert.doesNotMatch(await candidate.innerText(), /Advisory implementation approach/);
  const planningData = await page.evaluate(async id => (await fetch(`/api/beads/${id}`)).json(), question.beadId);
  for (const comment of planningData.comments) if (comment.message) assert.equal(comment.message.original, comment.body);
  for (const schema of ['nightweaver-plan-v2', 'nightweaver-planner-outcome-v1', 'nightweaver-plan-challenger-outcome-v1'])
    assert.equal(planningData.comments.find(c => c.body.includes(`"schema":"${schema}"`)).message.state, 'fallback');
  const review = history.locator('.semantic-message').filter({ has: page.getByRole('heading', { name: 'Planning review assessment', exact: true }) });
  assert.match(await review.innerText(), /Changes required/);
  await review.getByText('Inspect findings, questions & evidence', { exact: true }).click();
  assert.match(await review.innerText(), /Coverage gap/);
  assert.match(await review.innerText(), /Recommended answer/);
  assert.match(await review.innerText(), /Storage cost/);
  const host = history.locator('.semantic-message').filter({ has: page.getByRole('heading', { name: 'Host-recorded Planning outcome', exact: true }) });
  assert.match(await host.innerText(), /does not validate freshness/);
  await host.getByText('Inspect evidence references', { exact: true }).click();
  assert.match(await host.innerText(), /Source reference is not resolved/);
  assert.equal(await host.locator('a[href^="#workspace-comment-"]').count(), 1);
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 1000 });
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
    await candidate.screenshot({ path: `${output}/planning-candidate-${width}.png` });
    await review.screenshot({ path: `${output}/planning-review-${width}.png` });
  }
  await candidate.getByText('Inspect tasks, constraints & coverage', { exact: true }).click();
  assert.match(await candidate.innerText(), /Readable task 40/);
  assert.match(await candidate.innerText(), /Advisory implementation approach/);
  assert.match(await candidate.innerText(), /Product requirement/);
  const candidateDisclosure = candidate.locator(':scope > details').first();
  await candidateDisclosure.locator(':scope > summary').focus();
  await page.evaluate(() => loadWorkspace(workspaceCurrentId, { manual: true }));
  await page.waitForFunction(() => document.activeElement?.matches('[data-message-focus]'));
  assert.equal(await candidateDisclosure.getAttribute('open'), '');
  assert.equal(planningData.history.activeCommentId, question.sourceCommentId);

  // Desk uses the exact same selected source and renderer; inventory remains bounded.
  await page.goto(url, { waitUntil: 'networkidle' });
  const showAll = page.locator('#attention-show-all:not([hidden])');
  if (await showAll.count()) await showAll.first().click();
  for (const item of [primary, question, uncertain, uncertainQuestion, ...duplicates]) {
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
  await writeFile(`${output}/result.json`, JSON.stringify({ surfaces: ['history', 'workspace', 'desk'], widths: [1440, 375, 720], errors, mutations, external, explicitEvidenceNavigations: navigations, duplicateSources: duplicates.map(i => i.sourceCommentId) }, null, 2));
  console.log('Schema messages QA passed: all three surfaces, source selection, exact originals, keyboard refresh, responsive reflow, zero mutations/fetches.');
} finally { await browser.close(); }
