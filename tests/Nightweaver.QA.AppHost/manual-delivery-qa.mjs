import { existsSync } from 'node:fs';
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { chromium } from 'playwright';

const baseUrl = process.env.NIGHTWEAVER_QA_URL;
if (!baseUrl) throw new Error('Set NIGHTWEAVER_QA_URL to the http URL reported by aspire describe.');

const pointerPath = join(process.cwd(), '.manual-delivery-manifest.json');
const pointer = JSON.parse(await readFile(pointerPath, 'utf8'));
const manifest = JSON.parse(await readFile(pointer.manifest, 'utf8'));
const gateRelease = join(manifest.gateDirectory, `${manifest.tasks.parentless}.release`);
const retryRelease = join(manifest.gateDirectory, `${manifest.tasks.paused}.release`);
const errors = [];
let transientWorkspaceReadConflicts = 0;
let expectedWorkspaceRead409Responses = 0;
let workspaceRead409ConsoleErrors = 0;
const responseChecks = [];
const browser = await chromium.launch({ headless: true });
let page = null;
const observeBrowserDiagnostics = observedPage => {
  observedPage.on('pageerror', error => errors.push(error.message));
  observedPage.on('console', message => {
    if (message.type() !== 'error') return;
    if (message.text().includes('server responded with a status of 409 (Conflict)'))
      workspaceRead409ConsoleErrors++;
    else errors.push(message.text());
  });
  observedPage.on('response', response => {
    if (response.status() !== 409) return;
    responseChecks.push((async () => {
      const url = new URL(response.url());
      const problem = await response.json().catch(() => null);
      const transientWorkspaceReadConflict = response.request().method() === 'GET'
        && url.pathname === `/api/beads/${encodeURIComponent(manifest.tasks.paused)}`
        && problem?.title === 'Bead changed' && problem.status === 409
        && ['The Bead workflow changed while its workspace was being read. Refresh and try again.',
          'The Bead disappeared or its workflow changed while its workspace was being read. Refresh and try again.']
          .includes(problem.detail);
      if (transientWorkspaceReadConflict) expectedWorkspaceRead409Responses++;
      else errors.push(`Unexpected 409 response from ${response.request().method()} ${url.pathname}: ${JSON.stringify(problem)}.`);
      if (expectedWorkspaceRead409Responses > 10)
        errors.push('Workspace received more than 10 exact workflow-change 409 responses during the browser flow.');
    })());
  });
};
const getDetail = async (request, id) => {
  const response = await request.get(new URL(`/api/beads/${encodeURIComponent(id)}`, baseUrl).href);
  if (!response.ok()) {
    const problem = await response.json().catch(() => null);
    const error = new Error(`Bead detail ${id} returned ${response.status()}: ${JSON.stringify(problem)}.`);
    error.status = response.status();
    error.problem = problem;
    throw error;
  }
  return response.json();
};
const factoryState = async () => {
  const response = await fetch(new URL('/api/factory/state', baseUrl));
  if (!response.ok) throw new Error(`Factory state returned ${response.status()}.`);
  return response.json();
};
const waitForLiveFactoryState = async (timeoutMs = 20000) => {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    try {
      last = await factoryState();
      if (last.availability === 'live' && last.snapshot?.runtime_id) return last;
    } catch (error) { last = { reason: error.message }; }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error(`Factory telemetry did not become live: ${JSON.stringify(last)}.`);
};
const waitForFile = async (path, timeoutMs = 120000) => {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (existsSync(path)) return;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Fixture gate '${path}' was not reached within ${timeoutMs}ms.`);
};
const waitForClosed = async (request, id, timeoutMs = 300000) => {
  const deadline = Date.now() + timeoutMs;
  let consecutiveReadConflicts = 0;
  while (Date.now() < deadline) {
    let detail;
    try { detail = await getDetail(request, id); }
    catch (error) {
      const transientWorkspaceReadConflict = error?.status === 409
        && error.problem?.title === 'Bead changed'
        && ['The Bead workflow changed while its workspace was being read. Refresh and try again.',
          'The Bead disappeared or its workflow changed while its workspace was being read. Refresh and try again.']
          .includes(error.problem?.detail);
      if (!transientWorkspaceReadConflict) throw error;
      consecutiveReadConflicts++;
      transientWorkspaceReadConflicts++;
      if (consecutiveReadConflicts > 20)
        throw new Error(`Bead ${id} had more than 20 consecutive workflow-change conflicts while reading its workspace.`);
      await new Promise(resolve => setTimeout(resolve, 100));
      continue;
    }
    consecutiveReadConflicts = 0;
    if (detail.bead.status === 'closed') return detail;
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error(`Manual pilot Bead ${id} did not close before the browser QA timeout.`);
};

try {
  page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  observeBrowserDiagnostics(page);
  const mutationRequests = [];
  page.on('request', request => {
    const url = new URL(request.url());
    if (request.method() === 'POST' && /^\/api\/beads\/[^/]+\/(decide|retry|abort)$/.test(url.pathname))
      mutationRequests.push({ url, request });
  });

  await page.goto(baseUrl, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Nightweaver console' }).waitFor();
  const state = await waitForLiveFactoryState();
  const slot = state.snapshot.delivery.slots.find(candidate => candidate.bead_id === manifest.tasks.parentless);
  if (slot?.status !== 'running' || !(slot.invocation?.process_id > 0))
    throw new Error(`The gated parentless task is not running: ${JSON.stringify(slot)}.`);
  const runtimeId = state.snapshot.runtime_id;
  const agentPid = slot.invocation.process_id;

  const card = page.locator('#delivery-slots .factory-slot').filter({ hasText: manifest.tasks.parentless });
  await card.locator('[data-open-telemetry-slot]').click();
  const dialog = page.getByRole('dialog', { name: new RegExp(manifest.tasks.parentless) });
  await dialog.waitFor({ state: 'visible' });
  const liveLine = `manual-delivery-live bead=${manifest.tasks.parentless} role=implementer stage=stage:implement`;
  await page.waitForFunction(line => document.querySelector('#invocation-output')?.textContent.includes(line), liveLine,
    { timeout: 20000 });
  if (!(await page.locator('#invocation-metadata').textContent()).includes(manifest.tasks.parentless))
    throw new Error('The live invocation drawer metadata does not identify the gated task.');

  // Close the real EventSource and page while its native Agent is still held at the gate.
  await page.getByRole('button', { name: 'Close invocation telemetry' }).click();
  await dialog.waitFor({ state: 'hidden' });
  await page.close();
  page = null;
  const disconnectedState = await factoryState();
  if (disconnectedState.availability !== 'live' || disconnectedState.snapshot?.runtime_id !== runtimeId)
    throw new Error('The runtime stopped or changed identity when the browser disconnected.');
  const heldResponse = await fetch(new URL(`/api/beads/${encodeURIComponent(manifest.tasks.parentless)}`, baseUrl));
  if (!heldResponse.ok) throw new Error(`Held task detail returned ${heldResponse.status()}.`);
  const held = await heldResponse.json();
  if (held.bead.status !== 'in_progress' || FileExists(gateRelease))
    throw new Error(`The native Agent was not still held after browser disconnect: status=${held.bead.status}.`);
  await writeFile(gateRelease, 'release', 'utf8');

  page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  observeBrowserDiagnostics(page);
  page.on('request', request => {
    const url = new URL(request.url());
    if (request.method() === 'POST' && /^\/api\/beads\/[^/]+\/(decide|retry|abort)$/.test(url.pathname))
      mutationRequests.push({ url, request });
  });
  await page.goto(baseUrl, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Nightweaver console' }).waitFor();

  await waitForClosed(page.request, manifest.tasks.parentless);
  await waitForClosed(page.request, manifest.tasks.productTask);
  await waitForClosed(page.request, manifest.tasks.product);

  const routeTo = async id => page.evaluate(value => {
    window.location.hash = `#bead/${encodeURIComponent(value)}`;
  }, id);
  const waitForTitle = async title => page.getByRole('heading', { name: title, exact: true }).waitFor({ timeout: 15000 });

  await routeTo(manifest.tasks.decision);
  await waitForTitle('QA manual pilot product decision');
  await page.locator('#workspace-guidance-input').fill('Keep the compatibility boundary unchanged.');
  const decisionResponsePromise = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname === `/api/beads/${encodeURIComponent(manifest.tasks.decision)}/decide`);
  await page.getByRole('button', { name: 'Send & Resume' }).click();
  const decisionResponse = await decisionResponsePromise;
  const decisionAction = decisionResponse.ok() ? await decisionResponse.json() : null;
  if (!decisionResponse.ok() || decisionAction?.beadId !== manifest.tasks.decision
      || decisionAction.action !== 'resume' || decisionAction.resolved !== true
      || decisionAction.commentRecorded !== true || decisionAction.stateChanged !== true)
    throw new Error(`Send & Resume response did not confirm the guarded browser action: ${JSON.stringify(decisionAction)}.`);
  await page.locator('#workspace-guidance-context').getByText('No active human pause is available for this Bead.', { exact: true }).waitFor();
  const decision = await getDetail(page.request, manifest.tasks.decision);
  if (decision.bead.labels.includes('product-decision-required') || decision.attention !== null
      || !decision.comments.some(comment => comment.kind === 'human-guidance' && comment.body.includes('Keep the compatibility boundary unchanged.')))
    throw new Error('The same-origin Send & Resume action did not resolve and record the Product decision.');

  await routeTo(manifest.tasks.paused);
  await waitForTitle('QA manual pilot needs-human retry');
  const pausedBeforeRetry = await getDetail(page.request, manifest.tasks.paused);
  if (pausedBeforeRetry.bead.repairCycles !== 2 || !pausedBeforeRetry.bead.labels.includes('needs-human')
      || !pausedBeforeRetry.bead.labels.includes('stage:implement'))
    throw new Error(`Retry fixture did not begin at an exhausted two-cycle budget: ${JSON.stringify(pausedBeforeRetry.bead)}.`);
  const retryResponsePromise = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname === `/api/beads/${encodeURIComponent(manifest.tasks.paused)}/retry`);
  await page.getByRole('button', { name: 'Reset Budget & Retry' }).click();
  const retryResponse = await retryResponsePromise;
  const retryAction = retryResponse.ok() ? await retryResponse.json() : null;
  if (!retryResponse.ok() || retryAction?.beadId !== manifest.tasks.paused
      || retryAction.action !== 'retry' || retryAction.resolved !== true
      || retryAction.commentRecorded !== true || retryAction.stateChanged !== true)
    throw new Error(`Reset Budget & Retry response did not confirm the guarded browser action: ${JSON.stringify(retryAction)}.`);
  await waitForFile(join(manifest.gateDirectory, `${manifest.tasks.paused}.started`));
  const retryAgentPid = Number(await readFile(join(manifest.gateDirectory, `${manifest.tasks.paused}.started`), 'utf8'));
  const retryFactory = await waitForLiveFactoryState();
  const retrySlot = retryFactory.snapshot.delivery.slots.find(slot => slot.bead_id === manifest.tasks.paused);
  if (retrySlot?.status !== 'running' || retrySlot.invocation?.process_id !== retryAgentPid)
    throw new Error(`Guarded Retry did not launch the same native Agent recorded at its gate: ${JSON.stringify(retrySlot)}.`);
  const retry = await getDetail(page.request, manifest.tasks.paused);
  if (retry.bead.labels.includes('needs-human') || !retry.bead.labels.includes('stage:implement')
      || retry.bead.repairCycles !== 0 || retry.bead.status !== 'in_progress' || retry.bead.assignee !== 'nightweaver:manualqa'
      || !retry.comments.some(comment => comment.kind === 'human-action' && comment.body.includes('retry')))
    throw new Error(`The guarded same-origin Retry did not reset and dispatch the Plan task: ${JSON.stringify(retry.bead)}.`);
  await page.goto(baseUrl, { waitUntil: 'networkidle' });
  await page.getByRole('heading', { name: 'Nightweaver console' }).waitFor();
  const retryCard = page.locator('#delivery-slots .factory-slot').filter({ hasText: manifest.tasks.paused });
  await retryCard.locator('[data-open-telemetry-slot]').click();
  const retryDialog = page.getByRole('dialog', { name: new RegExp(manifest.tasks.paused) });
  await retryDialog.waitFor({ state: 'visible' });
  const retryLiveLine = `manual-delivery-live bead=${manifest.tasks.paused} role=implementer stage=stage:implement`;
  await page.waitForFunction(line => document.querySelector('#invocation-output')?.textContent.includes(line), retryLiveLine,
    { timeout: 20000 });
  await page.getByRole('button', { name: 'Close invocation telemetry' }).click();
  await retryDialog.waitFor({ state: 'hidden' });
  await writeFile(retryRelease, 'release', 'utf8');
  await waitForClosed(page.request, manifest.tasks.paused);
  await Promise.all(responseChecks);
  if (workspaceRead409ConsoleErrors > expectedWorkspaceRead409Responses)
    errors.push(`Observed ${workspaceRead409ConsoleErrors} browser 409 console errors but only ${expectedWorkspaceRead409Responses} exact documented workspace-read conflicts.`);
  if (mutationRequests.length !== 2 || mutationRequests.some(({ url }) => new URL(baseUrl).origin !== url.origin))
    throw new Error(`Expected exactly two same-origin browser mutation POSTs, got ${mutationRequests.length}.`);
  if (errors.length) throw new Error(errors.join('\n'));

  console.log(`Manual Delivery Playwright passed: native live log before exit (PID ${agentPid}), runtime identity ${runtimeId} survived browser disconnect, parentless/Product linkage completed and merged, same-origin guarded Resume and Retry verified; workspace-read 409s recovered=${transientWorkspaceReadConflicts}, browser-observed=${expectedWorkspaceRead409Responses}.`);
} finally {
  for (const path of [gateRelease, retryRelease]) {
    try { await writeFile(path, 'release', 'utf8'); } catch { /* AppHost teardown also releases both gates. */ }
  }
  if (page) await page.close();
  await browser.close();
}

function FileExists(path) {
  return existsSync(path);
}
