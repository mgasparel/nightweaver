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
const context = await (await fetch(base + '/api/studio/context')).json();
const runtime = await (await fetch(base + '/api/factory/state')).json();
assert.equal(context.projectId, 'qa');
assert.equal(path.resolve(runtime.snapshot.target_repository), repo);
const env = { ...process.env };
for (const key of Object.keys(env)) if (/^(BEADS_|BD_|GIT_)/.test(key)) delete env[key];
for (const key of ['HOME', 'USERPROFILE', 'APPDATA', 'XDG_CONFIG_HOME']) env[key] = path.join(root, 'home');
Object.assign(env, { GIT_CONFIG_GLOBAL: path.join(root, 'home', 'gitconfig'), GIT_CONFIG_NOSYSTEM: '1',
  BD_DISABLE_METRICS: '1', BD_NO_HOOKS: 'true', BD_DOLT_AUTO_START: 'false', OTEL_SDK_DISABLED: 'true' });
for (const key of ['BD_IMPORT_AUTO', 'BD_EXPORT_AUTO', 'BD_BACKUP_ENABLED', 'BD_DOLT_AUTO_PUSH', 'BD_EVENTS_EXPORT']) env[key] = 'false';
const bd = args => execFileSync('bd', ['--sandbox', ...args], { cwd: repo, env, encoding: 'utf8', timeout: 30000 });
const paused = JSON.parse(bd(['--readonly', 'list', '--limit', '0', '--json']))
  .filter(row => !['closed', 'tombstone'].includes(row.status) && row.labels?.some(label => ['needs-human', 'product-decision-required'].includes(label)));
assert(paused.length);
const selected = (paused.find(row => row.labels.includes('product-decision-required')) ?? paused[0]).id;
for (const row of paused.filter(row => row.id !== selected)) bd(['update', row.id, '--remove-label', 'needs-human', '--remove-label', 'product-decision-required']);
const output = path.resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/source-freshness');
fs.mkdirSync(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const responses = [], errors = [];
let steady;
let page;
try {
  page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', async response => {
    if (!response.url().includes('/api/operations/v1/') || !response.url().includes('/resources?')) return;
    const value = await response.json().catch(() => null);
    if (value) responses.push({ at: Date.now(), key: value.key, health: value.health, revision: value.revision,
      observedAt: value.observedAt, sources: value.sources, error: value.error?.code,
      validatedAgeMs: value.validatedAgeMs, pendingAgeMs: value.pendingAgeMs, synchronization: value.synchronization });
  });
  await page.goto(`${base}/projects/qa/beads/${selected}?tab=history`);
  await page.waitForFunction(() => document.querySelector('.synchronization [role=status]')?.textContent === 'Live connection · Current'
    && [...document.querySelectorAll('.status')].some(element => element.textContent === 'Current'));
  const guidance = page.getByRole('textbox', { name: `Guidance for ${selected}` });
  await guidance.fill('Retained event-cache guidance draft');
  const originalGuidance = await guidance.elementHandle();
  responses.length = 0;
  await page.evaluate(() => {
    window.sourceStates = [];
    const capture = () => {
      const state = { header: document.querySelector('.synchronization [role=status]')?.textContent,
        badges: [...document.querySelectorAll('.status')].map(element => element.textContent) };
      if (JSON.stringify(window.sourceStates.at(-1)) !== JSON.stringify(state)) window.sourceStates.push(state);
    };
    capture(); new MutationObserver(capture).observe(document.body, {subtree:true,childList:true,attributes:true,characterData:true});
  });
  let states;
  if (process.env.NIGHTWEAVER_QA_REUSE_STEADY) {
    // Explicit affected-phase rerun only; retain the prior real-minute evidence.
    const retained = JSON.parse(fs.readFileSync(process.env.NIGHTWEAVER_QA_REUSE_STEADY, 'utf8')).steady;
    assert(retained?.wallSeconds >= 130 && retained.attentionValidations?.length >= 3);
    assert(retained.responses.length >= 100 && retained.responses.every(value => value.health === 'fresh'));
    steady = { ...retained, reusedFrom: process.env.NIGHTWEAVER_QA_REUSE_STEADY };
    states = await page.evaluate(() => window.sourceStates);
  } else {
  const sustainedStart = Date.now();
  await page.waitForTimeout(130000);
  states = await page.evaluate(() => window.sourceStates);
  assert(Date.now() - sustainedStart >= 130000, 'Cross two real audits and the 120-second age boundary');
  assert(responses.length >= 100, 'Observe sustained HTTP receipt ageing');
  const trackerResponses = responses.filter(value => value.key === 'attention' || value.key.startsWith('workspace:'));
  assert(trackerResponses.every(value => value.validatedAgeMs <= 120000), 'Accepted sources remain within the age allowance');
  const attentionTimes = [...new Set(trackerResponses.filter(value => value.key === 'attention').map(value => value.observedAt))];
  assert(attentionTimes.length >= 3, 'Bootstrap and two real authoritative audits must validate Attention');
  assert(trackerResponses.every(value => value.health === 'fresh'), 'Successful unchanged source reads must not age-flash stale between collections');
  assert(states.every(state => state.header === 'Live connection · Current' && !state.badges.includes('stale')));
  steady = { wallSeconds: (Date.now() - sustainedStart) / 1000, attentionValidations: attentionTimes, responses: responses.slice(), states };
  }
  const comment = 'Source freshness external edit ' + Date.now();
  bd(['comments', 'add', selected, comment]);
  const commentRow = page.locator('.bead-comment').filter({ hasText: comment });
  const added = commentRow.getByText(comment, { exact: true });
  await added.waitFor({state:'attached'});
  await commentRow.locator('summary').first().click();
  await added.waitFor();
  for (const width of [1440,375,720]) {
    await page.setViewportSize({width,height:1000});
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({path:path.join(output,`history-${width}.png`),fullPage:true});
  }
  assert.equal(await guidance.inputValue(), 'Retained event-cache guidance draft');
  assert(await originalGuidance.evaluate(element => element.isConnected), 'Source updates preserve the guidance input node');
  await guidance.focus();
  const hiddenAt = Date.now();
  await page.evaluate(() => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await page.waitForTimeout(31000); // Real interest grace; outside normal tests.
  const woke = page.waitForResponse(response => response.url().endsWith('/refresh') && response.request().method() === 'POST' && response.status() === 202);
  await page.evaluate(() => {
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => false });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await woke;
  await page.waitForFunction(() => document.querySelector('.synchronization [role=status]')?.textContent === 'Live connection · Current');
  assert.equal(await guidance.inputValue(), 'Retained event-cache guidance draft');
  assert(await originalGuidance.evaluate(element => element.isConnected && document.activeElement === element), 'Wake preserves draft control and focus');
  assert(responses.some(value => value.key === 'attention' && Date.parse(value.observedAt) >= hiddenAt), 'Wake revalidates authoritative sources');
  // Workspace interests survive navigation briefly. Digest must remain fresh
  // while its checkpoint and the serial Workspace sources are both active.
  const digestReady = page.waitForResponse(async response => response.url().includes('/resources?')
    && new URL(response.url()).searchParams.get('key')?.startsWith('digest:') && (await response.json()).health === 'fresh');
  await page.goto(`${base}/projects/qa/overview`);
  await digestReady;
  await page.waitForFunction(() => document.querySelector('.synchronization [role=status]')?.textContent === 'Live connection · Current');
  responses.length = 0;
  await page.waitForTimeout(10000);
  const digest = responses.filter(value => value.key.startsWith('digest:'));
  assert(digest.length >= 3, 'Observe Digest during retained Workspace interest');
  assert(digest.every(value => value.health === 'fresh'), 'Successful checkpoint reads must revalidate retained Digest data');
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({checks:{stableNativeCollections:true,externalCommentVisible:true,digestOverlapFresh:true,draftFocusAndWake:true}, responses:responses.length, states}));
} finally {
  fs.writeFileSync(path.join(output,'evidence.json'),JSON.stringify({steady,responses,errors,states:page ? await page.evaluate(() => window.sourceStates).catch(() => null) : null},null,2));
  await browser.close();
}
