// Exact isolated AppHost only. Real browser POSTs, disposable external bd changes,
// and deterministic read projections for bounded-history/source-state edge cases.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const base = process.env.NIGHTWEAVER_QA_URL;
const configPath = process.env.NIGHTWEAVER_QA_CONFIG;
assert(base && configPath, 'Use the exact discovered URL/config');
const root = path.dirname(path.resolve(configPath));
assert(path.basename(root).startsWith('nightweaver-qa-'));
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
const repo = path.resolve(config.target_repository.path);
assert.equal(repo, path.join(root, 'repo')); assert.equal(config.name, 'qa');
const env = { ...process.env };
for (const key of Object.keys(env)) if (/^(BEADS_|BD_|GIT_)/.test(key)) delete env[key];
for (const key of ['HOME', 'USERPROFILE', 'APPDATA', 'XDG_CONFIG_HOME']) env[key] = path.join(root, 'home');
Object.assign(env, { GIT_CONFIG_GLOBAL: path.join(root, 'home', 'gitconfig'), GIT_CONFIG_NOSYSTEM: '1',
  BD_DISABLE_METRICS: '1', BD_NO_HOOKS: 'true', BD_DOLT_AUTO_START: 'false', OTEL_SDK_DISABLED: 'true' });
for (const key of ['BD_IMPORT_AUTO', 'BD_EXPORT_AUTO', 'BD_BACKUP_ENABLED', 'BD_DOLT_AUTO_PUSH', 'BD_EVENTS_EXPORT']) env[key] = 'false';
const bd = args => execFileSync('bd', ['--sandbox', ...args], { cwd: repo, env, encoding: 'utf8', timeout: 30000 });
const output = path.resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/studio-attention');
fs.mkdirSync(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const errors = []; const posts = []; const checks = [];
const attentionObservations = [];
const mark = text => { checks.push(text); console.log(text); };
let page;
try {
  page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.setDefaultTimeout(30000);
  const waitForCommand = predicate => {
    const pending = page.waitForResponse(predicate);
    // A failed click must retain its own diagnostic when browser teardown cancels this waiter.
    pending.catch(() => {});
    return pending;
  };
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', async response => {
    const url = new URL(response.url());
    if (!url.pathname.endsWith('/resources') || url.searchParams.get('key') !== 'attention') return;
    try {
      const value = await response.json();
      attentionObservations.push({ at: new Date().toISOString(), health: value.health,
        error: value.error, sources: value.sources, observedAt: value.observedAt,
        lastAttemptAt: value.lastAttemptAt, beadIds: value.data?.map(row => row.beadId) });
      if (attentionObservations.length > 64) attentionObservations.shift();
      fs.writeFileSync(path.join(output, 'attention-observations.json'), JSON.stringify(attentionObservations, null, 2));
    } catch { /* Teardown may cancel a pending read; preserve the original failure. */ }
  });
  page.on('request', request => { if (request.method() === 'POST') posts.push({ path: new URL(request.url()).pathname, body: request.postDataJSON() }); });
  await page.addInitScript(() => {
    const Native = window.EventSource;
    window.streams = [];
    window.EventSource = class extends Native { constructor(...args) { super(...args); if (String(args[0]).includes('/operations/v1/')) window.streams.push(this); } };
  });
  const capture = async name => {
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 900 });
      await page.evaluate(async () => { scrollTo(0, 0); await new Promise(requestAnimationFrame); await new Promise(requestAnimationFrame); });
      assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
      await page.screenshot({ path: path.join(output, `${name}-${width}.png`), fullPage: true });
      if (name.startsWith('decision-')) {
        await page.getByRole('button', { name: 'Send & Resume', exact: true }).scrollIntoViewIfNeeded();
        await page.screenshot({ path: path.join(output, `${name}-response-${width}.png`) });
      }
    }
    await page.setViewportSize({ width: 1440, height: 900 });
  };
  const workspace = id => `/projects/qa/beads/${encodeURIComponent(id)}`;
  // The v1 observer deliberately holds two Workspace interests through a 30s
  // disconnect grace; a third selection can wait for grace, backoff and collection.
  const go = async id => { await page.goto(base + workspace(id)); await page.locator('.bead-facts').waitFor({ timeout: 90000 }); };
  const create = (title, type, labels) => { const row = JSON.parse(bd(['create', '--title', title, '--type', type, '--labels', labels, '--json'])); return { beadId: row.id, title }; };
  const decision = create('Studio product decision', 'feature', 'product-decision-required,stage:plan');
  const retry = create('Studio needs human', 'task', 'needs-human,stage:implement');
  const abort = create('Studio needs human abort', 'task', 'needs-human,stage:implement');
  const retentionWarning = 'Retained worktree contains human changes. Preserve this environment before recovery.';
  const cleanupWarning = 'Historical cleanup preflight was blocked. Inspect retained evidence before retry or abort.';
  bd(['comments', 'add', retry.beadId, JSON.stringify({ schema: 'nightweaver-needs-human-v1',
    bead_id: retry.beadId, bead_type: 'task', cause: 'environment-blocked', failed_role: 'implementer',
    stage: 'stage:implement', reason: 'Review the retained environment before recovery.',
    environment_retention: retentionWarning, cleanup_preflight: cleanupWarning })]);
  const discardWithKeyboard = async editor => {
    const discard = page.getByRole('button', { name: 'Discard draft', exact: true });
    await discard.focus();
    await page.keyboard.press('Enter');
    assert.equal(await editor.inputValue(), '');
    assert(await editor.evaluate(node => document.activeElement === node), 'discard returns focus to the same editor');
  };
  const checkRecoveryWarnings = async name => {
    const context = page.locator('.decision-context');
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 900 });
      assert.equal(await page.locator('.decision-evidence').getAttribute('open'), null);
      for (const text of [retentionWarning, cleanupWarning]) {
        const warning = context.getByText(text, { exact: true });
        assert(await warning.isVisible(), `material warning visible at ${width}`);
        assert(await warning.evaluate(node => !!(node.compareDocumentPosition(document.querySelector('.guidance')) & Node.DOCUMENT_POSITION_FOLLOWING)));
      }
      assert.equal(await page.getByRole('button', { name: 'Send & Resume', exact: true }).count(), 0);
    }
    await capture(name);
  };
  bd(['comments', 'add', decision.beadId, 'Which implementation direction should we take?']);
  await page.goto(base + '/projects/qa/attention');
  await page.getByRole('button', { name: `Review ${decision.beadId}`, exact: true }).click();
  const input = page.getByRole('textbox', { name: `Guidance for ${decision.beadId}` });
  await input.fill('Discard this Attention decision draft.');
  await discardWithKeyboard(input);
  await input.fill('Keep this exact Bead draft while live observations change.');
  // The same decision editor is first in the dedicated Workspace, ahead of generic content.
  await page.getByRole('link', { name: 'Open Full Bead Workspace and evidence', exact: true }).click();
  await page.locator('.bead-facts').waitFor();
  assert.equal(await input.inputValue(), 'Keep this exact Bead draft while live observations change.');
  await discardWithKeyboard(input);
  await input.fill('Keep this exact Bead draft while live observations change.');
  mark('keyboard discard clears product-decision drafts and restores textarea focus in Attention and Workspace');
  const guidance = page.locator('.workspace-guidance');
  assert(await guidance.evaluate(node => !!(node.compareDocumentPosition(document.querySelector('.bead-facts')) & Node.DOCUMENT_POSITION_FOLLOWING)));
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 900 });
    const editor = await input.boundingBox();
    const send = await page.getByRole('button', { name: 'Send & Resume', exact: true }).boundingBox();
    assert(send.y >= editor.y + editor.height && send.y - editor.y - editor.height < 180, `action adjacent to input at ${width}`);
    assert.equal(await page.locator('.decision-evidence').getAttribute('open'), null);
  }
  await capture('decision-first');
  await page.getByRole('link', { name: 'History', exact: true }).click();
  assert.equal(await input.inputValue(), 'Keep this exact Bead draft while live observations change.');
  await page.goBack();
  await page.goBack();
  await page.getByRole('button', { name: `Review ${decision.beadId}`, exact: true }).click();
  assert.equal(await input.inputValue(), 'Keep this exact Bead draft while live observations change.');
  mark('question and retained draft precede Summary; adjacent response controls at 1440/375/720; tabs and route changes preserve draft');
  await input.focus();
  await capture('attention-busy');
  await input.focus();
  bd(['comments', 'add', decision.beadId, 'External live comment while a human types.']);
  const created = JSON.parse(bd(['create', '--id', 'qa-studio-decision-' + 'identity-'.repeat(10), '--title', 'External Studio escalation', '--type', 'task', '--labels', 'needs-human', '--json']));
  await page.locator(`a[href="${workspace(created.id)}"]`).waitFor();
  assert.equal(await input.inputValue(), 'Keep this exact Bead draft while live observations change.');
  assert(await input.evaluate(node => document.activeElement === node));
  // Resolution must not select another row, erase the draft or move textarea focus.
  bd(['update', decision.beadId, '--remove-label', 'product-decision-required']);
  await page.getByRole('heading', { name: `Retained draft · ${decision.beadId}`, exact: true }).waitFor();
  assert.equal(await input.inputValue(), 'Keep this exact Bead draft while live observations change.');
  assert(await input.evaluate(node => document.activeElement === node));
  assert.equal(await page.getByRole('button', { name: 'Send & Resume', exact: true }).count(), 0);
  mark('external pause/comment/resolution converges without refresh; typing and focus preserved');
  bd(['update', decision.beadId, '--add-label', 'product-decision-required']);
  await page.getByRole('button', { name: 'Send & Resume', exact: true }).waitFor();
  // Reconnect the actual stream while retaining identity and draft.
  await page.evaluate(() => { const stream = window.streams.at(-1); stream.close(); stream.dispatchEvent(new Event('error')); });
  await page.waitForFunction(() => document.querySelector('.synchronization')?.textContent.includes('Live connection'));
  await page.waitForFunction(() => ![...document.querySelectorAll('button')].find(node => node.textContent === 'Send & Resume')?.disabled);
  assert.equal(await input.inputValue(), 'Keep this exact Bead draft while live observations change.');
  mark('real EventSource disconnect/reconnect retains editor and restores current state');
  // Failure-only component scenarios must never replay or clear a draft.
  let failStatus = 404;
  await page.route(`**/api/beads/${decision.beadId}/decide`, route => route.fulfill({ status: failStatus, json: { detail: 'Component failure fixture' } }));
  await page.getByRole('button', { name: 'Save Note', exact: true }).click();
  await page.getByText('Bead unavailable. Your draft is retained.', { exact: false }).waitFor();
  assert.equal(await input.inputValue(), 'Keep this exact Bead draft while live observations change.');
  failStatus = 503;
  await page.getByRole('button', { name: 'Save Note', exact: true }).click();
  await page.getByRole('button', { name: 'I reviewed current state and history' }).waitFor();
  assert(await page.getByRole('button', { name: 'Save Note', exact: true }).isDisabled());
  const commandCount = () => posts.filter(post => post.path.startsWith('/api/beads/')).length;
  const beforeReplay = commandCount(); await page.waitForTimeout(2500); assert.equal(commandCount(), beforeReplay);
  await page.getByRole('button', { name: 'I reviewed current state and history' }).click();
  await page.unroute(`**/api/beads/${decision.beadId}/decide`);
  mark('404 and uncertain POST keep draft, prevent submission and never replay');
  // Save Note is a real same-origin browser request and leaves pause intact.
  let response = waitForCommand(r => r.url().endsWith(`/${decision.beadId}/decide`) && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Save Note', exact: true }).click();
  assert.equal((await response).status(), 200);
  await page.getByText('Note saved.', { exact: false }).waitFor();
  assert(JSON.parse(bd(['show', decision.beadId, '--json']))[0].labels.includes('product-decision-required'));
  // Hold a successful real POST response, navigate/select another Bead, then release.
  let release; const gate = new Promise(resolve => release = resolve); let seen;
  const arrived = new Promise(resolve => seen = resolve);
  let settled; const routeSettled = new Promise(resolve => settled = resolve);
  await page.route(`**/api/beads/${decision.beadId}/decide`, async route => {
    try { const actual = await route.fetch(); seen(); await gate; await route.fulfill({ response: actual }); }
    finally { settled(); }
  });
  await page.getByRole('button', { name: 'Send & Resume', exact: true }).click();
  await arrived;
  assert(await page.getByRole('button', { name: 'Send & Resume', exact: true }).count() === 0 || await page.getByRole('button', { name: 'Send & Resume', exact: true }).isDisabled());
  await page.getByRole('button', { name: `Review ${retry.beadId}`, exact: true }).click();
  release(); await routeSettled; await page.unroute(`**/api/beads/${decision.beadId}/decide`);
  await page.locator('.panel-body > h2').filter({ hasText: retry.title }).waitFor();
  await checkRecoveryWarnings('recovery-warnings-attention');
  assert.equal(await page.getByText('Action confirmed.', { exact: false }).count(), 0);
  mark('real Save Note and Resume; duplicate prevention and late receipt cannot target new selection');
  // Return through the retained forward entry without reloading the tab's store.
  await page.goForward();
  await input.waitFor();
  assert.equal(await input.inputValue(), 'Keep this exact Bead draft while live observations change.');
  await discardWithKeyboard(input);
  await page.goBack();
  await page.getByRole('button', { name: `Review ${retry.beadId}`, exact: true }).click();
  mark('successful sends retain the exact Bead draft until explicit discard');
  // Real host 409 from an intentionally stale expected snapshot.
  await page.route(`**/api/beads/${retry.beadId}/retry`, route => {
    const body = route.request().postDataJSON(); body.expected.repairCycles += 10;
    return route.continue({ postData: JSON.stringify(body) });
  });
  response = waitForCommand(r => r.url().endsWith(`/${retry.beadId}/retry`) && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Reset Budget & Retry', exact: true }).click();
  assert.equal((await response).status(), 409);
  await page.getByText('Bead changed. Review current state', { exact: false }).waitFor();
  await page.unroute(`**/api/beads/${retry.beadId}/retry`);
  await go(retry.beadId);
  await checkRecoveryWarnings('recovery-warnings-workspace');
  mark('supported native recovery retention and cleanup warnings stay visible above actions in both views at 1440/375/720');
  response = waitForCommand(r => r.url().endsWith(`/${retry.beadId}/retry`) && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Reset Budget & Retry', exact: true }).click();
  assert.equal((await response).status(), 200);
  mark('real stale-snapshot 409 and explicit fresh Retry through Workspace');
  await go(abort.beadId);
  await capture('workspace-busy');
  await page.getByRole('button', { name: 'Abort & Close', exact: true }).click();
  await page.getByRole('button', { name: 'Cancel', exact: true }).waitFor();
  assert(await page.getByRole('button', { name: 'Cancel', exact: true }).evaluate(node => document.activeElement === node));
  await page.keyboard.press('Escape');
  assert(await page.getByRole('button', { name: 'Abort & Close', exact: true }).evaluate(node => document.activeElement === node));
  await page.getByRole('button', { name: 'Abort & Close', exact: true }).click();
  response = waitForCommand(r => r.url().endsWith(`/${abort.beadId}/abort`) && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Confirm Abort & Close', exact: true }).click();
  assert.equal((await response).status(), 200);
  assert.equal(JSON.parse(bd(['show', abort.beadId, '--json']))[0].status, 'closed');
  mark('two-step real Abort, Escape and focus restoration');
  // External comments appear in the dedicated route, query/history links remain native.
  await go(decision.beadId);
  await page.getByRole('link', { name: 'History', exact: true }).click();
  await page.getByRole('heading', { name: 'History and evidence' }).waitFor();
  await page.getByText('External live comment while a human types.', { exact: false }).waitFor({ state: 'attached' });
  await page.goBack(); assert(!new URL(page.url()).searchParams.has('tab'));
  await page.goForward(); assert.equal(new URL(page.url()).searchParams.get('tab'), 'history');
  await page.reload(); await page.getByRole('heading', { name: 'History and evidence' }).waitFor();
  mark('direct reload and Back/Forward preserve Workspace tab; external comment readable');
  // Deterministic long read projection; real shell, coordinator and React components.
  const detail = await (await page.request.get(base + `/api/beads/${created.id}`)).json();
  detail.bead.title = 'A long human decision title with attributable context and preserved editable guidance '.repeat(3);
  detail.attention = { ...detail.attention, kind: 'product-decision-required', canDecide: true, canRetry: false, canAbort: false,
    question: 'Which boundary should we verify?', sourceCommentId: '1',
    message: { state: 'supported', title: 'Planning question', summary: 'Which boundary should we verify?', role: 'planner', stage: 'stage:plan', sourceId: '1', original: 'Attributable fixture evidence',
      sections: [{ label: 'Question', text: 'Which boundary should we verify?', details: true, commentId: null },
        { label: 'Recommended answer', text: 'Verify the existing guarded command boundary.', details: true, commentId: null },
        { label: 'Tradeoffs', text: 'This keeps native setup shared while testing the rendered decision.', details: true, commentId: null }] } };
  detail.comments = Array.from({ length: 80 }, (_, index) => ({ id: String(index), author: `Author ${index}`, createdAt: new Date(1700000000000 + index * 1000).toISOString(), body: `**Evidence ${index}**\n\n<script>window.unsafe=true</script>\n\n![bad](https://example.invalid/pixel) [bad](javascript:alert(1)) [safe](https://example.com)`, kind: 'ordinary' }));
  detail.history = { activeCommentId: '1', selectionSource: 'structured' };
  detail.relations = Array.from({ length: 40 }, (_, index) => ({ id: `qa-related-${index}`, title: `Related Bead ${index}`,
    type: 'parent-child', direction: 'incoming', role: 'child' }));
  let mode = 'busy'; let removeFocused = false;
  await page.route('**/api/operations/v1/projects/qa/resources?**', async route => {
    const key = new URL(route.request().url()).searchParams.get('key');
    if (key !== `workspace:${created.id}` && key !== 'attention') return route.continue();
    const actual = await route.fetch(); const value = await actual.json();
    if (key === `workspace:${created.id}`) {
      value.data = structuredClone(detail);
      value.health = 'fresh'; value.error = null; value.observedAt = new Date().toISOString();
      for (const source of Object.values(value.sources)) {
        source.health = 'fresh'; source.error = null; source.observedAt = value.observedAt;
      }
      if (removeFocused) value.data.comments = value.data.comments.filter(row => row.id !== '79');
      if (mode === 'stale') { value.health = 'stale'; value.sources.inventory.health = 'stale'; value.error = value.sources.inventory.error = { code: 'fixture-stale', message: 'Retained tracker fixture', retryable: true }; }
      if (mode === 'unavailable') { value.data = null; value.observedAt = null; value.health = 'unavailable'; value.error = { code: 'resource-missing', message: 'The Bead no longer exists.', retryable: false }; }
    } else if (mode === 'empty') { value.data = []; value.health = 'fresh'; value.error = null; value.observedAt = new Date().toISOString(); }
    await route.fulfill({ response: actual, json: value });
  });
  await go(created.id);
  const projectedInput = page.getByRole('textbox', { name: `Guidance for ${created.id}` });
  await projectedInput.fill('A retained draft must not be replaced by updated advice.');
  const evidence = page.locator('.decision-evidence');
  await evidence.locator(':scope > summary').click();
  await projectedInput.focus();
  detail.attention.message.sections[1].text = 'Updated attributable recommendation without replacing the draft.';
  await page.getByText(detail.attention.message.sections[1].text, { exact: true }).first().waitFor();
  assert.equal(await projectedInput.inputValue(), 'A retained draft must not be replaced by updated advice.');
  assert(await projectedInput.evaluate(node => document.activeElement === node));
  assert(await evidence.evaluate(node => node.open));
  assert.equal(await page.locator('.decision-context button').count(), 0, 'Recommendations are not invented structured choices');
  await capture('decision-long-identity');
  await evidence.locator(':scope > summary').click();
  mark('attributable recommendation/tradeoffs precede editable guidance; long title/identity reflow and live evidence preserve draft, focus and disclosure');
  const relations = page.locator('details').filter({ has: page.locator('summary').filter({ hasText: /^Relations ·/ }) });
  await relations.locator('summary').click();
  assert.equal(await relations.getByRole('link').count(), 15);
  await relations.getByRole('button', { name: 'Next', exact: true }).click();
  assert.equal(await relations.getByRole('link').count(), 15);
  assert.equal(await relations.locator('[data-relation-range]').textContent(), 'Showing 16–30 of 40');
  mark('Workspace relations retain exact links and directional source state with bounded disclosure');
  await page.getByRole('link', { name: 'History', exact: true }).click();
  assert.equal(await page.locator('.bead-comment').count(), 9);
  const active = page.locator('.bead-comment').filter({ hasText: 'Active pause evidence' });
  await active.locator('summary').click();
  await capture('long-history');
  assert.equal(await active.locator('strong').innerText(), 'Evidence 1');
  assert.equal(await page.locator('.bead-markdown img, .bead-markdown script, .bead-markdown a[href^="javascript:"]').count(), 0);
  assert.equal(await page.evaluate(() => window.unsafe), undefined);
  assert.equal(await active.locator('a').getAttribute('rel'), 'noopener noreferrer');
  await page.getByRole('button', { name: 'Show 8 older comments', exact: true }).click();
  assert.equal(await page.locator('.bead-comment').count(), 17);
  const newest = page.locator('.bead-comment').filter({ hasText: 'Author 79' });
  await newest.locator('summary').click(); await newest.locator('summary').focus();
  await page.waitForTimeout(2500);
  assert(await newest.getAttribute('open') !== null);
  assert(await newest.locator('summary').evaluate(node => document.activeElement === node));
  removeFocused = true;
  await page.waitForFunction(() => ![...document.querySelectorAll('.bead-comment')].some(node => node.textContent.includes('Author 79')));
  assert.equal(await page.evaluate(() => document.activeElement.tagName), 'H1');
  mark('bounded history, pinned active evidence, safe Markdown, focused disclosures survive refresh and fall back on removal');
  for (const state of ['busy', 'stale', 'unavailable', 'empty']) {
    mode = state;
    if (state === 'empty') { await page.goto(base + '/projects/qa/attention'); await page.getByText('No paused Beads in this observation. Runtime activity is reported separately.', { exact: true }).waitFor(); }
    else {
      if (state === 'stale') await page.getByText('Retained tracker fixture', { exact: true }).waitFor();
      if (state === 'unavailable') await page.getByRole('heading', { name: 'Bead unavailable', exact: true }).waitFor();
    }
    for (const width of [1440, 375, 720]) {
      await page.setViewportSize({ width, height: 900 });
      assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `${state} no overflow at ${width}`);
      await page.screenshot({ path: path.join(output, `${state}-${width}.png`), fullPage: true });
    }
  }
  assert.deepEqual(errors, []);
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ checks, posts, errors }, null, 2));
  console.log(JSON.stringify({ checks, posts: posts.length, errors }));
} catch (error) { if (page) { await page.screenshot({ path: path.join(output, 'failure.png'), fullPage: true }); fs.writeFileSync(path.join(output, 'failure.txt'), await page.locator('body').innerText()); } throw error; } finally { await browser.close(); }
