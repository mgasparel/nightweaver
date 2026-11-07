import { existsSync } from 'node:fs';
import { readFile, unlink, writeFile } from 'node:fs/promises';
import { execFileSync, spawnSync } from 'node:child_process';
import { join } from 'node:path';
import { chromium } from 'playwright';
import { findNewDirtyWorktreeDiagnostic, isTransientWorkspaceReadConflict, matchConsole409Errors, requireForeignClaimId } from './manual-delivery-qa-helpers.mjs';

const baseUrl = process.env.NIGHTWEAVER_QA_URL;
if (!baseUrl) throw new Error('Set NIGHTWEAVER_QA_URL to the http URL reported by aspire describe.');

const pointerPath = join(process.cwd(), '.manual-delivery-manifest.json');
const pointer = JSON.parse(await readFile(pointerPath, 'utf8'));
const manifest = JSON.parse(await readFile(pointer.manifest, 'utf8'));
const foreignClaimId = requireForeignClaimId(manifest);
const gateRelease = join(manifest.gateDirectory, `${manifest.tasks.parentless}.release`);
const retryRelease = join(manifest.gateDirectory, `${manifest.tasks.paused}.release`);
const sentinelPath = join(manifest.nightweaverRoot, 'worktrees', 'manualqa', manifest.tasks.paused,
  'manual-pilot-retained-sentinel.txt');
const errors = [];
let transientWorkspaceReadConflicts = 0;
let expectedWorkspaceRead409Responses = 0;
let expectedStaleRetry409Responses = 0;
const console409Errors = [];
const validated409Responses = [];
const responseChecks = [];
const browser = await chromium.launch({ headless: true });
let page = null;
const observeBrowserDiagnostics = observedPage => {
  observedPage.on('pageerror', error => errors.push(error.message));
  observedPage.on('console', message => {
    if (message.type() !== 'error') return;
    if (!message.text().includes('server responded with a status of 409 (Conflict)')) {
      errors.push(message.text());
      return;
    }
    console409Errors.push({ observedAt: Date.now(), text: message.text(), location: message.location() });
  });
  observedPage.on('response', response => {
    if (response.status() !== 409) return;
    const observedAt = Date.now();
    const method = response.request().method();
    const pathname = new URL(response.url()).pathname;
    responseChecks.push((async () => {
      const problem = await response.json().catch(() => null);
      const expectedStaleRetryConflict = method === 'POST'
        && pathname === `/api/beads/${encodeURIComponent(manifest.tasks.paused)}/retry`
        && problem?.title === 'Bead changed' && problem.status === 409
        && problem.detail === 'The Bead workflow changed since this action was displayed. Refresh the Attention Desk before retrying.';
      const transientWorkspaceReadConflict = method === 'GET'
        && pathname === `/api/beads/${encodeURIComponent(manifest.tasks.paused)}`
        && problem?.title === 'Bead changed' && problem.status === 409
        && ['The Bead workflow changed while its workspace was being read. Refresh and try again.',
          'The Bead disappeared or its workflow changed while its workspace was being read. Refresh and try again.']
          .includes(problem.detail);
      if (expectedStaleRetryConflict) {
        expectedStaleRetry409Responses++;
        validated409Responses.push({ observedAt, method, pathname, classification: 'stale-retry', problem });
      } else if (transientWorkspaceReadConflict) {
        expectedWorkspaceRead409Responses++;
        validated409Responses.push({ observedAt, method, pathname, classification: 'workspace-read', problem });
      } else errors.push(`Unexpected 409 response from ${method} ${pathname}: ${JSON.stringify(problem)}.`);
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
const getDetailWithWorkspaceReadRetry = async (request, id) => {
  let consecutiveReadConflicts = 0;
  while (true) {
    try { return await getDetail(request, id); }
    catch (error) {
      if (!isTransientWorkspaceReadConflict(error)) throw error;
      consecutiveReadConflicts++;
      transientWorkspaceReadConflicts++;
      if (consecutiveReadConflicts > 20)
        throw new Error(`Bead ${id} had more than 20 consecutive workflow-change conflicts while reading its workspace.`);
      await new Promise(resolve => setTimeout(resolve, 100));
    }
  }
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
const waitForUnavailableFactoryState = async (timeoutMs = 20000) => {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    try {
      last = await factoryState();
      if (last.availability !== 'live') return last;
    } catch (error) { last = { reason: error.message }; }
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error(`Factory telemetry stayed live after its runtime was killed: ${JSON.stringify(last)}.`);
};
const waitForRestartAck = async (oldPid, timeoutMs = 30000) => {
  const deadline = Date.now() + timeoutMs;
  let last = '';
  while (Date.now() < deadline) {
    try { last = (await readFile(manifest.runtimeRestartAck, 'utf8')).trim(); } catch { /* not restarted yet */ }
    if (last && Number(last) !== oldPid) return Number(last);
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`The fixture restart bridge did not acknowledge a new runtime PID: ${last}.`);
};
const waitForGatePidChange = async (path, oldPid, timeoutMs = 120000) => {
  const deadline = Date.now() + timeoutMs;
  let last = oldPid;
  while (Date.now() < deadline) {
    try { last = Number((await readFile(path, 'utf8')).trim()); } catch { /* gate not rewritten yet */ }
    if (Number.isInteger(last) && last > 0 && last !== oldPid) return last;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`The runtime did not relaunch the interrupted task at its native gate; last PID=${last}.`);
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
  while (Date.now() < deadline) {
    const detail = await getDetailWithWorkspaceReadRetry(request, id);
    if (detail.bead.status === 'closed') return detail;
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error(`Manual pilot Bead ${id} did not close before the browser QA timeout.`);
};
const waitForNeedsHuman = async (request, id, timeoutMs = 300000) => {
  const deadline = Date.now() + timeoutMs;
  let last;
  while (Date.now() < deadline) {
    try { last = await getDetail(request, id); }
    catch (error) {
      const transientWorkspaceReadConflict = error?.status === 409
        && error.problem?.title === 'Bead changed'
        && ['The Bead workflow changed while its workspace was being read. Refresh and try again.',
          'The Bead disappeared or its workflow changed while its workspace was being read. Refresh and try again.']
          .includes(error.problem?.detail);
      if (!transientWorkspaceReadConflict) throw error;
      transientWorkspaceReadConflicts++;
      await new Promise(resolve => setTimeout(resolve, 100));
      continue;
    }
    if (last.bead.status === 'open' && last.bead.labels.includes('needs-human')) return last;
    if (last.bead.status === 'closed') throw new Error(`Bead ${id} closed despite the retained dirty sentinel.`);
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw new Error(`Bead ${id} did not pause after the dirty-worktree recovery attempt: ${JSON.stringify(last?.bead)}.`);
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
  const staleRetryResponse = await page.evaluate(async beadId => {
    const response = await fetch(`/api/beads/${encodeURIComponent(beadId)}/retry`, {
      method: 'POST',
      headers: { Accept: 'application/json', 'Content-Type': 'application/json', 'X-Nightweaver-Request': 'console-v1' },
      body: JSON.stringify({ expected: { stage: 'stage:review', pauseLabels: ['needs-human'], repairCycles: 2 } })
    });
    return { status: response.status, problem: await response.json().catch(() => null) };
  }, manifest.tasks.paused);
  if (staleRetryResponse.status !== 409 || staleRetryResponse.problem?.title !== 'Bead changed')
    throw new Error(`A stale same-origin Retry snapshot was not rejected by the workflow guard: ${JSON.stringify(staleRetryResponse)}.`);
  const unchangedAfterStale = await getDetail(page.request, manifest.tasks.paused);
  if (unchangedAfterStale.bead.status !== pausedBeforeRetry.bead.status
      || unchangedAfterStale.bead.assignee !== pausedBeforeRetry.bead.assignee
      || unchangedAfterStale.bead.repairCycles !== pausedBeforeRetry.bead.repairCycles
      || !unchangedAfterStale.bead.labels.includes('needs-human')
      || unchangedAfterStale.comments.some(comment => comment.kind === 'human-action' && comment.body.includes('retry')))
    throw new Error('The stale guarded Retry request changed the paused Bead or recorded an action.');
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
  const worktree = join(manifest.nightweaverRoot, 'worktrees', 'manualqa', manifest.tasks.paused);
  const retainedBranch = execFileSync('git', ['-C', worktree, 'branch', '--show-current'], { encoding: 'utf8' }).trim();
  const sentinelContents = 'uncommitted manual pilot state must survive runtime crash and restart\n';
  await writeFile(sentinelPath, sentinelContents, 'utf8');
  const pausedBeforeCrash = await getDetail(page.request, manifest.tasks.paused);
  const retryActionBeforeCrash = [...pausedBeforeCrash.comments].reverse().find(comment => {
    try {
      const action = JSON.parse(comment.body);
      return action.schema === 'nightweaver-human-action-v1' && action.action === 'retry';
    } catch { return false; }
  });
  if (!retryActionBeforeCrash)
    throw new Error('The crash scenario did not record the guarded Retry action before restart.');
  const commentIdsBeforeCrash = new Set(pausedBeforeCrash.comments.map(comment => comment.id));
  console.log(`Crash recovery boundary is guarded Retry comment ${retryActionBeforeCrash.id}.`);
  const foreignBeforeCrash = await getDetail(page.request, foreignClaimId);
  if (foreignBeforeCrash.bead.status !== 'in_progress' || foreignBeforeCrash.bead.assignee !== 'foreign:operator')
    throw new Error(`The manifest foreign claim must begin as in_progress/foreign:operator: ${JSON.stringify(foreignBeforeCrash.bead)}.`);
  const oldRuntimePid = Number((await readFile(manifest.runtimeProcessId, 'utf8')).trim());
  const oldRuntimeId = retryFactory.snapshot.runtime_id;
  const kill = spawnSync('taskkill.exe', ['/PID', String(oldRuntimePid), '/T', '/F'], { encoding: 'utf8', windowsHide: true });
  if (kill.error || kill.status !== 0)
    throw new Error(`Could not crash the isolated runtime PID ${oldRuntimePid}: ${kill.error?.message ?? kill.stderr ?? kill.status}.`);
  const staleAfterCrash = await waitForUnavailableFactoryState();
  if (staleAfterCrash.snapshot?.runtime_id !== oldRuntimeId)
    throw new Error(`The stale HTTP snapshot did not retain the crashed runtime identity: ${JSON.stringify(staleAfterCrash)}.`);
  if (await readFile(sentinelPath, 'utf8') !== sentinelContents
      || execFileSync('git', ['-C', worktree, 'branch', '--show-current'], { encoding: 'utf8' }).trim() !== retainedBranch)
    throw new Error('The runtime crash changed the retained worktree sentinel or owned branch.');
  const foreignAfterCrash = await getDetail(page.request, foreignClaimId);
  if (foreignAfterCrash.bead.status !== foreignBeforeCrash.bead.status
      || foreignAfterCrash.bead.assignee !== foreignBeforeCrash.bead.assignee)
    throw new Error('The runtime crash changed the foreign-owned Bead claim.');
  console.log(`Foreign claim ${foreignClaimId} preserved across runtime crash: ${foreignAfterCrash.bead.status}/${foreignAfterCrash.bead.assignee}.`);
  await writeFile(manifest.runtimeRestartRequest, 'restart\n', 'utf8');
  const restartedPid = await waitForRestartAck(oldRuntimePid);
  const restarted = await waitForLiveFactoryState(60000);
  if (restarted.snapshot.runtime_id === oldRuntimeId || Number((await readFile(manifest.runtimeProcessId, 'utf8')).trim()) !== restartedPid)
    throw new Error(`The HTTP factory did not expose the new runtime identity and PID after restart: ${JSON.stringify(restarted)}.`);
  if (await readFile(sentinelPath, 'utf8') !== sentinelContents
      || execFileSync('git', ['-C', worktree, 'branch', '--show-current'], { encoding: 'utf8' }).trim() !== retainedBranch)
    throw new Error('The restarted runtime changed the retained worktree sentinel or owned branch.');
  const foreignAfterRestart = await getDetail(page.request, foreignClaimId);
  if (foreignAfterRestart.bead.status !== foreignBeforeCrash.bead.status
      || foreignAfterRestart.bead.assignee !== foreignBeforeCrash.bead.assignee)
    throw new Error('Runtime restart changed the foreign-owned Bead claim.');
  console.log(`Foreign claim ${foreignClaimId} preserved across runtime restart: ${foreignAfterRestart.bead.status}/${foreignAfterRestart.bead.assignee}.`);
  // Startup recovery may release the interrupted claim before normal discovery
  // claims it again. The transient Bead read is diagnostic only; assert the new
  // runtime's native Agent and the eventual fail-closed pause below.
  const startupRecoveryObservation = await getDetailWithWorkspaceReadRetry(page.request, manifest.tasks.paused);
  console.log(`Post-restart startup recovery observed ${startupRecoveryObservation.bead.status}/${startupRecoveryObservation.bead.assignee ?? 'unassigned'} before native rediscovery.`);
  const restartedAgentPid = await waitForGatePidChange(
    join(manifest.gateDirectory, `${manifest.tasks.paused}.started`), retryAgentPid);
  const restartedAgentFactory = await waitForLiveFactoryState();
  const restartedAgentSlot = restartedAgentFactory.snapshot.delivery.slots.find(slot => slot.bead_id === manifest.tasks.paused);
  if (restartedAgentSlot?.status !== 'running' || restartedAgentSlot.invocation?.process_id !== restartedAgentPid)
    throw new Error(`Restarted runtime did not expose the native Agent processing the retained task: ${JSON.stringify(restartedAgentSlot)}.`);
  await writeFile(retryRelease, 'release', 'utf8');
  const dirtyWorktreePause = await waitForNeedsHuman(page.request, manifest.tasks.paused);
  if (dirtyWorktreePause.bead.assignee != null || !existsSync(sentinelPath)
      || execFileSync('git', ['-C', worktree, 'branch', '--show-current'], { encoding: 'utf8' }).trim() !== retainedBranch)
    throw new Error(`Dirty-worktree recovery did not fail closed while retaining the sentinel and branch: ${JSON.stringify(dirtyWorktreePause.bead)}.`);
  const recoveryDiagnostic = findNewDirtyWorktreeDiagnostic(dirtyWorktreePause.comments, commentIdsBeforeCrash);
  if (!recoveryDiagnostic)
    throw new Error('Dirty-worktree recovery did not add a new exact needs-human diagnostic after the crash-boundary Retry.');
  await unlink(sentinelPath);
  await routeTo(manifest.tasks.paused);
  await waitForTitle('QA manual pilot needs-human retry');
  const pausedAfterCrash = await getDetail(page.request, manifest.tasks.paused);
  if (pausedAfterCrash.bead.status !== 'open' || !pausedAfterCrash.bead.labels.includes('needs-human'))
    throw new Error(`The dirty-worktree pause was not shown in the Workspace before Retry: ${JSON.stringify(pausedAfterCrash.bead)}.`);
  const resumedRetryResponsePromise = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname === `/api/beads/${encodeURIComponent(manifest.tasks.paused)}/retry`);
  await page.getByRole('button', { name: 'Reset Budget & Retry' }).click();
  const resumedRetryResponse = await resumedRetryResponsePromise;
  const resumedRetryAction = resumedRetryResponse.ok() ? await resumedRetryResponse.json() : null;
  if (!resumedRetryResponse.ok() || resumedRetryAction?.beadId !== manifest.tasks.paused
      || resumedRetryAction.action !== 'retry' || resumedRetryAction.resolved !== true
      || resumedRetryAction.commentRecorded !== true || resumedRetryAction.stateChanged !== true)
    throw new Error(`The post-cleanup same-origin Retry was not verified: ${JSON.stringify(resumedRetryAction)}.`);
  await waitForClosed(page.request, manifest.tasks.paused);
  const finalRetry = await getDetail(page.request, manifest.tasks.paused);
  if (finalRetry.bead.status !== 'closed' || existsSync(sentinelPath))
    throw new Error('The explicit cleanup and guarded Retry did not complete the retained task with a clean worktree.');
  await Promise.all(responseChecks);
  if (expectedStaleRetry409Responses !== 1)
    throw new Error(`Expected one exact same-origin stale Retry 409 response, got ${expectedStaleRetry409Responses}.`);
  const console409Matches = matchConsole409Errors(console409Errors, validated409Responses);
  if (console409Matches.unmatched.length)
    errors.push(`Browser 409 console errors without a one-to-one exact response match within 2 seconds: ${JSON.stringify(console409Matches.unmatched)}; validated responses=${JSON.stringify(validated409Responses)}.`);
  if (mutationRequests.length !== 4 || mutationRequests.some(({ url }) => new URL(baseUrl).origin !== url.origin))
    throw new Error(`Expected exactly four same-origin browser mutation POSTs including stale and recovery Retry actions, got ${mutationRequests.length}.`);
  if (errors.length) throw new Error(errors.join('\n'));

  console.log(`Manual Delivery Playwright passed: native live log before exit (Agent PID ${agentPid}); runtime ${runtimeId} survived browser disconnect, then HTTP reported it stale after crash and a new runtime ${restarted.snapshot.runtime_id} (PID ${restartedPid}) after restart; worktree sentinel/branch and foreign claim were preserved, dirty-worktree recovery paused with the claim released, and explicit sentinel cleanup plus a second guarded same-origin Retry merged the task; parentless/Product linkage merged; stale Retry was rejected without mutation and guarded Resume/Retry succeeded; exact 409 responses=${validated409Responses.length}, matched console events=${console409Matches.matches.length}, workspace-read responses=${expectedWorkspaceRead409Responses}, transient workspace read retries=${transientWorkspaceReadConflicts}.`);
  console.log(`Validated 409 evidence: ${JSON.stringify(validated409Responses.map(({ method, pathname, classification, problem }) => ({ method, pathname, classification, problem })))}`);
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
