import { chromium } from 'playwright';

const baseUrl = process.env.NIGHTWEAVER_QA_URL;
if (!baseUrl) throw new Error('Set NIGHTWEAVER_QA_URL to the http URL reported by aspire describe.');

const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
const failures = [];
const consoleFailures = [];
const browserErrorResponses = [];
const externalRequests = [];
const actionRequests = [];
const geometryEvidence = {};
page.on('pageerror', error => failures.push(`pageerror: ${error.message}`));
page.on('console', message => { if (message.type() === 'error') consoleFailures.push({ text: message.text(), location: message.location().url }); });
page.on('response', response => {
  if (response.status() >= 400) browserErrorResponses.push({
    method: response.request().method(),
    path: new URL(response.url()).pathname,
    status: response.status()
  });
});
page.on('request', request => {
  const url = new URL(request.url());
  if (url.hostname === 'example.invalid') externalRequests.push(url.href);
  const match = url.pathname.match(/^\/api\/beads\/([^/]+)\/(decide|retry|abort)$/);
  if (request.method() === 'POST' && match) {
    let body = null;
    try { body = request.postDataJSON(); } catch { /* Abort has no semantic body. */ }
    actionRequests.push({ beadId: decodeURIComponent(match[1]), action: match[2], body });
  }
});

let failDetailId = null;
let failActionId = null;
let failAttentionReads = 0;
let delayedDetailId = null;
let delayedActionId = null;
let releaseDetail;
let releaseAction;
let delayedDetailSeen;
let delayedActionSeen;
let interceptedActionCount = 0;
const delayedDetailGate = new Promise(resolve => { releaseDetail = resolve; });
const delayedActionGate = new Promise(resolve => { releaseAction = resolve; });
const detailSeen = new Promise(resolve => { delayedDetailSeen = resolve; });
const actionSeen = new Promise(resolve => { delayedActionSeen = resolve; });

await page.route('**/api/beads/**', async route => {
  const request = route.request();
  const url = new URL(request.url());
  const match = url.pathname.match(/^\/api\/beads\/([^/]+)(?:\/(decide|retry|abort))?$/);
  if (!match) return route.continue();
  const beadId = decodeURIComponent(match[1]);

  if (request.method() === 'GET' && failDetailId === beadId) {
    await route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Fixture unavailable', detail: 'The QA fixture is unavailable. Refresh to try again.' }) });
    return;
  }
  if (request.method() === 'POST' && failActionId === beadId) {
    interceptedActionCount++;
    await route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Fixture action failure', detail: 'The QA action could not be confirmed.' }) });
    return;
  }
  if (request.method() === 'GET' && delayedDetailId === beadId) {
    const response = await route.fetch();
    delayedDetailSeen();
    await delayedDetailGate;
    try { await route.fulfill({ response }); } catch { /* The browser may cancel this response after navigation. */ }
    return;
  }
  if (request.method() === 'POST' && delayedActionId === beadId) {
    interceptedActionCount++;
    const response = await route.fetch();
    delayedActionSeen();
    await delayedActionGate;
    try { await route.fulfill({ response }); } catch { /* The page can move on while the real write is pending. */ }
    return;
  }
  return route.continue();
});

await page.route('**/api/attention', async route => {
  if (failAttentionReads > 0) {
    failAttentionReads--;
    await route.fulfill({ status: 503, contentType: 'application/problem+json', body: JSON.stringify({ title: 'Fixture unavailable', detail: 'The Attention Desk could not be refreshed. Retry to try again.' }) });
    return;
  }
  return route.continue();
});

const check = (condition, message) => { if (!condition) throw new Error(message); };
const waitForWorkspace = async title => {
  await page.getByRole('heading', { name: title, exact: true }).waitFor({ timeout: 15000 });
  await page.locator('#workspace-status').waitFor();
};
const readId = async title => {
  const titleLink = page.locator('#beads-table-body').getByRole('link', { name: title, exact: true });
  await titleLink.waitFor({ timeout: 15000 });
  const row = titleLink.locator('xpath=../..');
  const href = await row.locator('td.bead-id a').getAttribute('href');
  check(typeof href === 'string' && href.startsWith('#bead/'), `Explorer has no workspace link for ${title}.`);
  return decodeURIComponent(href.slice('#bead/'.length));
};
const assertGuidanceStudio = async mode => {
  const selectors = [
    '#workspace-guidance-input',
    'label[for="workspace-guidance-input"]',
    '#workspace-guidance-help',
    '.workspace-template-chips'
  ];
  const states = await Promise.all(selectors.map(async selector => {
    const element = page.locator(selector);
    return {
      selector,
      hidden: await element.evaluate(node => node.hidden),
      display: await element.evaluate(node => getComputedStyle(node).display),
      visible: await element.isVisible()
    };
  }));
  const shouldBeVisible = mode === 'decision';
  check(states.every(state => shouldBeVisible
    ? !state.hidden && state.display !== 'none' && state.visible
    : state.hidden && state.display === 'none' && !state.visible),
  `The ${mode} guidance controls do not match their expected rendered visibility: ${JSON.stringify(states)}.`);

  const actionNames = shouldBeVisible ? ['Save Note', 'Send & Resume'] : ['Reset Budget & Retry', 'Abort & Close'];
  for (const name of actionNames)
    check(await page.getByRole('button', { name, exact: true }).isVisible(), `The ${mode} action ${name} is not visible.`);
  console.log(`PASS: ${mode} workspace guidance presentation ${JSON.stringify(states)}; actions ${actionNames.join(', ')} remain visible.`);
};
const routeTo = async id => {
  await page.evaluate(value => { window.location.hash = `#bead/${encodeURIComponent(value)}`; }, id);
};
const waitForTitle = async title => waitForWorkspace(title);
const getDetail = async id => {
  const response = await page.request.get(new URL(`/api/beads/${encodeURIComponent(id)}`, baseUrl).href);
  check(response.ok(), `Detail request for ${id} returned ${response.status()}.`);
  return response.json();
};

try {
  await page.goto(baseUrl, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Nightweaver console' }).waitFor();
  await page.locator('[data-view="all"]').click();
  await page.locator('#beads-table-body tr').first().waitFor({ timeout: 15000 });

  const decisionId = await readId('QA product decision');
  const secondDecisionId = await readId('QA second decision');
  const markdownId = await readId('QA workspace markdown');
  const needsHumanId = await readId('QA needs human');
  const abortId = await readId('QA needs human abort');

  // Explorer links, desktop layout, chronological history, focus return and browser navigation.
  const decisionRow = page.locator('#beads-table-body tr').filter({ hasText: 'QA product decision' });
  const originLink = decisionRow.locator('td.bead-title a');
  await originLink.click();
  await waitForTitle('QA product decision');
  await assertGuidanceStudio('decision');
  check(new URL(page.url()).hash === `#bead/${encodeURIComponent(decisionId)}`, 'Explorer link did not produce the direct workspace route.');
  const desktopLayout = await page.locator('.workspace-layout').evaluate(element => {
    const style = getComputedStyle(element);
    return { display: style.display, columns: style.gridTemplateColumns.split(' ').map(Number.parseFloat) };
  });
  check(desktopLayout.display === 'grid' && desktopLayout.columns.length === 2, `Desktop workspace is not a two-column grid: ${JSON.stringify(desktopLayout)}.`);
  check(desktopLayout.columns[0] > desktopLayout.columns[1] * 1.4 && desktopLayout.columns[0] < desktopLayout.columns[1] * 2.8,
    `Desktop workspace columns are not close to the planned 2:1 ratio: ${JSON.stringify(desktopLayout.columns)}.`);
  geometryEvidence.desktop = desktopLayout;
  const older = page.locator('.workspace-older-history');
  check(await older.count() === 1 && !(await older.evaluate(element => element.open)), 'Older history was not collapsed by default.');
  const activeComment = page.locator('.workspace-comment-active');
  check(await activeComment.count() === 1, 'The current clarification comment is not marked active.');
  const activeStyle = await activeComment.evaluate(element => {
    const style = getComputedStyle(element);
    return { accent: style.borderInlineStartWidth, color: style.borderInlineStartColor };
  });
  check(activeStyle.accent !== '0px' && activeStyle.color !== 'rgba(0, 0, 0, 0)', `The active comment lacks a visible accent: ${JSON.stringify(activeStyle)}.`);
  await page.getByRole('button', { name: 'Expand All' }).click();
  check(await older.evaluate(element => element.open), 'Expand All did not open older history.');
  await page.getByRole('button', { name: 'Collapse Older' }).click();
  check(!(await older.evaluate(element => element.open)), 'Collapse Older did not close older history.');
  await page.getByRole('link', { name: 'Back to Console' }).click();
  await page.getByRole('heading', { name: 'Nightweaver console' }).waitFor();
  await page.waitForFunction(() => document.activeElement?.matches('#beads-table-body td.bead-title a'));
  check((await page.evaluate(() => document.activeElement?.getAttribute('href'))) === `#bead/${encodeURIComponent(decisionId)}`,
    'Back to Console did not restore focus to the originating Explorer link.');
  await page.goBack();
  await waitForTitle('QA product decision');
  await page.goForward();
  await page.getByRole('heading', { name: 'Nightweaver console' }).waitFor();
  await page.locator('a[href="#beads"]').first().click();
  check(await page.locator('#bead-workspace').isHidden(), 'An ordinary console section anchor blanked or retained the workspace route.');
  console.log('PASS: Explorer/Attention/direct/back-forward navigation, 2:1 desktop layout, active history accent/collapse controls, and keyboard focus restoration.');

  // Attention Desk route and per-Bead navigation remain available.
  await page.locator('a[href="#attention"]').first().click();
  if (await page.locator('#attention-show-all').isVisible()) await page.locator('#attention-show-all').click();
  const attentionCard = page.locator('#attention-cards .attention-card').filter({ hasText: 'QA product decision' });
  await attentionCard.waitFor({ timeout: 15000 });

  // Failed action plus failed automatic/manual Attention reads keeps the stale Desk card blocked.
  await attentionCard.locator('.attention-compose > summary').click();
  const attentionGuidance = attentionCard.locator('textarea');
  await attentionGuidance.fill('Keep this Attention Desk draft.');
  failActionId = decisionId;
  interceptedActionCount = 0;
  failAttentionReads = 1;
  await attentionCard.getByRole('button', { name: 'Send & Resume' }).click();
  await attentionCard.locator('.attention-card-message').getByText('The QA action could not be confirmed.', { exact: true }).waitFor();
  await page.locator('#attention-error').waitFor({ state: 'visible' });
  check(interceptedActionCount === 1 && await attentionGuidance.inputValue() === 'Keep this Attention Desk draft.'
    && await attentionGuidance.isDisabled()
    && await attentionCard.getByRole('button', { name: 'Send & Resume' }).isDisabled()
    && await attentionCard.getByRole('button', { name: 'Save Note' }).isDisabled(),
  'The Attention Desk left stale actions enabled or lost the draft/message after its automatic refresh failed.');

  failAttentionReads = 1;
  await page.locator('#attention-refresh').click();
  await page.locator('#attention-error').waitFor({ state: 'visible' });
  check(await attentionGuidance.isDisabled() && await attentionCard.getByRole('button', { name: 'Send & Resume' }).isDisabled()
    && interceptedActionCount === 1, 'A failed manual Attention refresh re-enabled a stale action or reposted it.');

  await page.locator('#attention-refresh').click();
  await page.waitForFunction(id => {
    const card = Array.from(document.querySelectorAll('#attention-cards .attention-card'))
      .find(element => element.dataset.beadId === id);
    return card && !card.querySelector('textarea')?.disabled
      && Array.from(card.querySelectorAll('button')).every(button => !button.disabled);
  }, decisionId);
  check(await attentionGuidance.inputValue() === 'Keep this Attention Desk draft.'
    && await attentionCard.locator('.attention-card-message').getByText('The QA action could not be confirmed.', { exact: true }).isVisible()
    && interceptedActionCount === 1,
  'A successful Attention refresh did not preserve the draft/failure message or the failed action was reposted.');
  failActionId = null;
  console.log('PASS: Attention Desk actions stay blocked across failed automatic/manual refreshes and unblock after a successful current read without reposting or losing draft/message.');

  await attentionCard.getByRole('link', { name: 'Open Full Bead Workspace' }).click();
  await waitForTitle('QA product decision');

  // Markdown security, local anchor handling, source view and long-content layout.
  await page.setViewportSize({ width: 1440, height: 1000 });
  await routeTo(markdownId);
  await waitForTitle('QA workspace markdown');
  check(await page.locator('#workspace-specs script, #workspace-specs img, #workspace-history script, #workspace-history img').count() === 0,
    'Untrusted specification or comment markup created executable/image elements.');
  check(await page.evaluate(() => window.__workspaceInjected) === undefined, 'Untrusted raw HTML executed in the workspace.');
  const markdownAnchors = await page.locator('#workspace-specs a').evaluateAll(elements => elements.map(element => ({ href: element.getAttribute('href'), target: element.getAttribute('target'), rel: element.getAttribute('rel') })));
  check(markdownAnchors.some(anchor => anchor.href === 'https://example.com/reference' && anchor.target === '_blank' && anchor.rel?.includes('noopener') && anchor.rel?.includes('noreferrer')),
    `The explicitly allowed HTTPS link is not safely external: ${JSON.stringify(markdownAnchors)}.`);
  check(markdownAnchors.some(anchor => anchor.href === '#workspace-comment-0'), 'The current workspace comment anchor was not retained.');
  check(!markdownAnchors.some(anchor => /^(?:notes\.txt|\/notes\.txt|\/\/|javascript:|data:)/i.test(anchor.href ?? '')),
    `An unsupported relative/root/dangerous Markdown path remained actionable: ${JSON.stringify(markdownAnchors)}.`);
  const sourceButton = page.locator('.workspace-spec-section').first().getByRole('button', { name: 'View source' });
  await sourceButton.click();
  check((await page.locator('.workspace-source-text').textContent()).includes('https://example.invalid/markdown-image.png'), 'View source did not preserve the exact Markdown text.');
  await page.getByRole('button', { name: 'View formatted' }).click();
  check(await page.locator('.workspace-markdown-fallback').count() === 0, 'Formatted Markdown failed after toggling back from source.');
  check((await page.locator('#workspace-history .workspace-comment-body').textContent()).includes('<script>window.__workspaceInjected = true</script>'),
    'The ordinary comment body was not retained as text.');
  const desktopCode = await page.locator('#workspace-specs pre').first().evaluate(element => ({ width: element.getBoundingClientRect().width, parent: element.parentElement.getBoundingClientRect().width, whiteSpace: getComputedStyle(element).whiteSpace }));
  check(desktopCode.width <= desktopCode.parent + 1 && ['pre-wrap', 'normal'].includes(desktopCode.whiteSpace), `Long code is not confined on desktop: ${JSON.stringify(desktopCode)}.`);
  await page.locator('#workspace-specs a[href="#workspace-comment-0"]').click();
  check(await page.locator('#workspace-comment-0').isVisible(), 'The allowed app-owned comment anchor did not resolve within this workspace.');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.waitForTimeout(100);
  const mobile = await page.evaluate(() => ({ viewport: innerWidth, document: document.documentElement.scrollWidth, body: document.body.scrollWidth,
    columns: getComputedStyle(document.querySelector('.workspace-layout')).gridTemplateColumns }));
  check(mobile.document <= mobile.viewport && mobile.body <= mobile.viewport, `Workspace content overflows a 390px viewport: ${JSON.stringify(mobile)}.`);
  check(mobile.columns.trim().split(/\s+/).length === 1, `Workspace did not stack to one column on mobile: ${JSON.stringify(mobile)}.`);
  geometryEvidence.mobile = mobile;
  check(externalRequests.length === 0, `Markdown caused unexpected remote network requests: ${externalRequests.join(', ')}.`);
  console.log(`PASS: Markdown sanitizer/source/local anchors/no external requests; desktop and mobile geometry ${JSON.stringify(geometryEvidence)}.`);

  // Fake action failure preserves the draft and disables actions until a successful refresh.
  await page.setViewportSize({ width: 1440, height: 1000 });
  await routeTo(decisionId);
  await waitForTitle('QA product decision');
  const guidance = page.locator('#workspace-guidance-input');
  await guidance.fill('Failure draft must stay.');
  failActionId = decisionId;
  interceptedActionCount = 0;
  failAttentionReads = 1;
  await page.getByRole('button', { name: 'Save Note' }).click();
  await page.locator('#workspace-action-message').getByText('The QA action could not be confirmed.', { exact: true }).waitFor();
  check(interceptedActionCount === 1 && await guidance.inputValue() === 'Failure draft must stay.', 'A failed action lost its draft or was submitted more than once.');
  check(await page.getByRole('button', { name: 'Save Note' }).isDisabled(), 'Actions remained enabled after an unknown outcome.');
  failActionId = null;
  await page.getByRole('button', { name: 'Refresh' }).click();
  await page.getByText('The QA action could not be confirmed.', { exact: true }).waitFor().catch(() => {});
  await page.waitForFunction(() => document.querySelector('#workspace-guidance-input')?.disabled === false);
  check(await guidance.inputValue() === 'Failure draft must stay.' && interceptedActionCount === 1,
    'A successful refresh did not preserve the decision draft or the failed action was reposted.');

  await page.locator('a[href="#attention"]').first().click();
  const retainedDeskCard = page.locator('#attention-cards .attention-card').filter({ hasText: 'QA product decision' });
  await retainedDeskCard.waitFor({ timeout: 15000 });
  check(await retainedDeskCard.getByRole('button', { name: 'Send & Resume' }).isDisabled(),
    'A successful Workspace refresh incorrectly cleared the pinned Desk stale-snapshot block.');
  await page.locator('#attention-refresh').click();
  await page.waitForFunction(id => {
    const card = Array.from(document.querySelectorAll('#attention-cards .attention-card'))
      .find(element => element.dataset.beadId === id);
    return card && !card.querySelector('button.primary-action')?.disabled;
  }, decisionId);
  check(interceptedActionCount === 1, 'Refreshing the Attention Desk reposted the failed Workspace action.');
  console.log('PASS: Workspace Refresh recovers workspace controls after an Attention read failure while the pinned Desk stays blocked until its own successful read.');
  await routeTo(decisionId);
  await waitForTitle('QA product decision');

  // A delayed real action response cannot overwrite a different Bead's draft.
  delayedActionId = decisionId;
  interceptedActionCount = 0;
  const delayedActionRequest = page.getByRole('button', { name: 'Save Note' }).click();
  await Promise.race([actionSeen, new Promise((_, reject) => setTimeout(() => reject(new Error('Timed out waiting for the real note action response.')), 15000))]);
  check(interceptedActionCount === 1 && await page.getByRole('button', { name: 'Save Note' }).isDisabled(), 'The pending action was not guarded against duplicate submission.');
  await routeTo(secondDecisionId);
  await waitForTitle('QA second decision');
  const secondGuidance = page.locator('#workspace-guidance-input');
  await secondGuidance.fill('B draft survives A response.');
  releaseAction();
  await delayedActionRequest;
  await page.waitForTimeout(100);
  check(await page.locator('#workspace-id').textContent() === secondDecisionId, 'A delayed A action response replaced Bead B.');
  check(await secondGuidance.inputValue() === 'B draft survives A response.', 'A delayed A action response changed Bead B draft state.');
  delayedActionId = null;

  // A delayed A detail read cannot replace B; invalid/missing B clears A metadata.
  await routeTo(decisionId);
  await waitForTitle('QA product decision');
  delayedDetailId = decisionId;
  await routeTo(decisionId + '-again');
  await page.locator('#workspace-error').waitFor({ state: 'visible' });
  // Return to the real A through the delayed request, then switch to B before its response is released.
  await routeTo(decisionId);
  await Promise.race([detailSeen, new Promise((_, reject) => setTimeout(() => reject(new Error('Timed out waiting for delayed detail response.')), 15000))]);
  await routeTo(secondDecisionId);
  await waitForTitle('QA second decision');
  releaseDetail();
  await page.waitForTimeout(100);
  check(await page.locator('#workspace-id').textContent() === secondDecisionId, 'A delayed A detail response replaced Bead B.');
  delayedDetailId = null;

  await routeTo(decisionId);
  await waitForTitle('QA product decision');
  await routeTo('qa-missing-workspace-fixture');
  await page.locator('#workspace-error').waitFor({ state: 'visible' });
  check(await page.locator('#workspace-meta').textContent() === '', 'A missing Bead route retained the previous Bead metadata.');
  check(await page.locator('#workspace-id').textContent() === 'qa-missing-workspace-fixture', 'A missing route displayed the previous Bead ID.');
  await page.evaluate(() => { window.location.hash = '#bead/%25'; });
  await page.getByText('This Bead ID contains unsupported characters.', { exact: true }).waitFor();
  check(await page.locator('#workspace-meta').textContent() === '', 'An invalid Bead route retained previous metadata.');

  // Same-Bead failed refresh is explicitly stale and disables actions until recovery.
  await routeTo(secondDecisionId);
  await waitForTitle('QA second decision');
  const retainedDraft = 'Keep this draft across a stale refresh.';
  await page.locator('#workspace-guidance-input').fill(retainedDraft);
  failDetailId = secondDecisionId;
  await page.getByRole('button', { name: 'Refresh' }).click();
  await page.locator('#workspace-stale').waitFor({ state: 'visible' });
  check(await page.locator('#workspace-guidance-input').isDisabled(), 'Actions were not disabled on a stale snapshot.');
  check(await page.locator('#workspace-guidance-input').inputValue() === retainedDraft, 'A stale refresh lost the current draft.');
  failDetailId = null;
  await page.getByRole('button', { name: 'Refresh' }).click();
  await page.locator('#workspace-stale').waitFor({ state: 'hidden' });
  check(await page.locator('#workspace-guidance-input').inputValue() === retainedDraft, 'A successful refresh lost the retained draft.');
  console.log('PASS: fake action failure, unknown outcome, retained draft, A-to-B delayed action/read races, A-to-missing/invalid B clearing, and stale refresh recovery.');

  // Template insertion edits at the caret; note, resume, retry and confirmed abort use shared actions.
  await routeTo(decisionId);
  await waitForTitle('QA product decision');
  await guidance.fill('Start end');
  await guidance.evaluate(element => element.setSelectionRange(6, 6));
  await page.getByRole('button', { name: 'Constraints', exact: true }).click();
  check(await guidance.inputValue() === 'Start Constraints: end', 'A guidance chip overwrote content instead of inserting at the caret.');
  await page.getByRole('button', { name: 'Save Note' }).click();
  await page.locator('#workspace-action-message').getByText('Note saved. This Bead remains paused.', { exact: true }).waitFor();
  let decisionDetail = await getDetail(decisionId);
  check(decisionDetail.bead.labels.includes('product-decision-required') && decisionDetail.attention?.kind === 'product-decision-required',
    'Save Note incorrectly resolved the product decision pause.');
  check(decisionDetail.comments.some(comment => comment.kind === 'human-guidance' && comment.body.includes('Constraints:')),
    'Save Note did not appear in the chronological history.');
  await guidance.fill('Resume with the existing endpoint and retain the current shape.');
  await page.getByRole('button', { name: 'Send & Resume' }).click();
  await page.locator('#workspace-guidance-context').getByText('No active human pause is available for this Bead.', { exact: true }).waitFor();
  decisionDetail = await getDetail(decisionId);
  check(!decisionDetail.bead.labels.includes('product-decision-required') && decisionDetail.attention === null,
    'Send & Resume did not resolve only the product decision pause.');
  check(decisionDetail.comments.some(comment => comment.kind === 'human-guidance' && comment.body.includes('Resume with the existing endpoint')),
    'Send & Resume guidance was not retained in history.');
  const resumeRequest = actionRequests.findLast(request => request.beadId === decisionId
    && request.action === 'decide' && request.body?.action === 'resume');
  check(JSON.stringify(resumeRequest?.body?.expected) === JSON.stringify({
    stage: null, pauseLabels: ['product-decision-required'], repairCycles: 0
  }), `Send & Resume did not submit the displayed workflow snapshot: ${JSON.stringify(resumeRequest)}.`);
  console.log('PASS: caret template insertion, note-only pause retention, and Send & Resume state/history refresh.');

  await routeTo(needsHumanId);
  await waitForTitle('QA needs human');
  await assertGuidanceStudio('needs-human');
  await page.getByRole('button', { name: 'Reset Budget & Retry' }).click();
  await page.locator('#workspace-guidance-context').getByText('No active human pause is available for this Bead.', { exact: true }).waitFor();
  const retryDetail = await getDetail(needsHumanId);
  check(!retryDetail.bead.labels.includes('needs-human') && retryDetail.bead.repairCycles === 0,
    `Retry did not remove the needs-human pause and reset its repair budget: ${JSON.stringify({ labels: retryDetail.bead.labels, repairCycles: retryDetail.bead.repairCycles, status: retryDetail.bead.status })}.`);
  check(retryDetail.comments.some(comment => comment.kind === 'human-action' && comment.body.includes('retry')),
    'Retry action was not retained in history.');
  const retryRequest = actionRequests.findLast(request => request.beadId === needsHumanId && request.action === 'retry');
  check(JSON.stringify(retryRequest?.body?.expected) === JSON.stringify({
    stage: 'stage:qa', pauseLabels: ['needs-human'], repairCycles: 2
  }), `Retry did not submit the displayed workflow snapshot: ${JSON.stringify(retryRequest)}.`);
  console.log('PASS: Reset Budget & Retry removes the active pause, resets repairCycles, and records the action.');

  await routeTo(abortId);
  await waitForTitle('QA needs human abort');
  await assertGuidanceStudio('needs-human');
  await page.getByRole('button', { name: 'Abort & Close' }).click();
  await page.locator('.workspace-abort-confirmation').waitFor();
  check(await page.getByRole('button', { name: 'Confirm Abort & Close' }).isVisible(), 'Abort did not require explicit confirmation.');
  await page.getByRole('button', { name: 'Confirm Abort & Close' }).click();
  await page.locator('#workspace-status').getByText(`Workspace refreshed for ${abortId}.`, { exact: true }).waitFor();
  await page.locator('#workspace-guidance-context').getByText('A pause label is present, but no current question or diagnostic could be attributed to a comment. Review the history before acting.', { exact: true }).waitFor();
  const abortDetail = await getDetail(abortId);
  check(abortDetail.bead.status === 'closed' && abortDetail.bead.labels.includes('needs-human') && abortDetail.attention === null
    && abortDetail.comments.some(comment => comment.kind === 'human-action' && comment.body.includes('abort')),
    'Confirmed abort did not close the Bead while retaining its history.');
  check(await page.getByRole('button', { name: 'Reset Budget & Retry' }).count() === 0
    && await page.getByRole('button', { name: 'Abort & Close' }).count() === 0, 'A closed Bead retained actionable pause controls.');
  console.log('PASS: Abort requires confirmation, closes the Bead, retains its needs-human label/history, and removes actionable controls.');

  check(failures.length === 0, `Browser reported page exceptions:\n${failures.join('\n')}`);
  const expectedBrowserErrors = [
    { method: 'POST', path: `/api/beads/${decisionId}/decide`, status: 503 },
    { method: 'POST', path: `/api/beads/${decisionId}/decide`, status: 503 },
    { method: 'GET', path: '/api/attention', status: 503 },
    { method: 'GET', path: '/api/attention', status: 503 },
    { method: 'GET', path: '/api/attention', status: 503 },
    { method: 'GET', path: `/api/beads/${decisionId}-again`, status: 404 },
    { method: 'GET', path: '/api/beads/qa-missing-workspace-fixture', status: 404 },
    { method: 'GET', path: `/api/beads/${secondDecisionId}`, status: 503 }
  ];
  const sortedSignature = entries => entries.map(({ method, path, status }) => `${method} ${path} ${status}`).sort();
  check(JSON.stringify(sortedSignature(browserErrorResponses)) === JSON.stringify(sortedSignature(expectedBrowserErrors)),
    `Unexpected browser error responses: ${JSON.stringify(browserErrorResponses)}.`);
  check(consoleFailures.length === expectedBrowserErrors.length
    && consoleFailures.every(error => /Failed to load resource: the server responded with a status of (404|503) \(/.test(error.text)),
    `Unexpected browser console errors (the expected 404/503 responses are retained as evidence): ${JSON.stringify(consoleFailures)}.`);
  check(externalRequests.length === 0, `Browser attempted unexpected remote requests: ${externalRequests.join(', ')}.`);
  console.log(`Expected browser error evidence: ${JSON.stringify({ responses: browserErrorResponses, console: consoleFailures })}.`);
  console.log(`Workspace Playwright QA passed: Explorer/Attention/direct/back-forward navigation, stale Attention action blocking/recovery, desktop/mobile layout, active history/source controls, Markdown security and no remote loads, route/action race guards, route metadata clearing, stale/error recovery, draft/chip handling, note/resume/retry/confirmed-abort. Geometry: ${JSON.stringify(geometryEvidence)}. IDs: ${JSON.stringify({ decisionId, secondDecisionId, markdownId, needsHumanId, abortId })}.`);
} finally {
  releaseDetail();
  releaseAction();
  await browser.close();
}
