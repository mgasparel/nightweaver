// Real UI and native CLI only. The runner supplies a fresh exact isolated stack.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';
import { createHash } from 'node:crypto';
import { assertSample } from './shared-read-assertions.mjs';
import { resourceKey, sampleReceipt } from './shared-read-receipts.mjs';

const base = process.env.NIGHTWEAVER_QA_URL;
const configPath = process.env.NIGHTWEAVER_QA_CONFIG;
const output = process.env.NIGHTWEAVER_QA_OUTPUT;
assert(base && configPath && output, 'Use the isolated runner');
const root = path.dirname(path.resolve(configPath));
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
assert(path.basename(root).startsWith('nightweaver-qa-'));
assert.equal(config.name, 'qa'); assert.equal(path.resolve(config.target_repository.path), path.join(root, 'repo'));
const manifest = JSON.parse(fs.readFileSync(path.join(root, 'shared-read-manifest.json'), 'utf8'));
fs.mkdirSync(output, { recursive: true });
fs.copyFileSync(path.join(root, 'shared-read.jsonl'), path.join(output, 'seed.jsonl'));
const env = { ...process.env };
for (const name of Object.keys(env)) if (/^(BD_|BEADS_|GIT_)/.test(name)) delete env[name];
for (const name of ['HOME', 'USERPROFILE', 'APPDATA', 'XDG_CONFIG_HOME']) env[name] = path.join(root, 'home');
Object.assign(env, { GIT_CONFIG_NOSYSTEM: '1', GIT_CONFIG_GLOBAL: path.join(root, 'home', 'gitconfig'),
  BD_NO_HOOKS: 'true', BD_DISABLE_METRICS: '1', BD_DOLT_AUTO_START: 'false', BD_BACKUP_ENABLED: 'false',
  BD_IMPORT_AUTO: 'false', BD_EXPORT_AUTO: 'false', BD_DOLT_AUTO_PUSH: 'false', BD_EVENTS_EXPORT: 'false' });
const external = [];
function bd(args) {
  const startMs = performance.now(), startedUtc = new Date().toISOString();
  const text = execFileSync(process.env.NIGHTWEAVER_PROBE_BD, ['--sandbox', ...args],
    { cwd: config.target_repository.path, env, encoding: 'utf8', timeout: 30000, maxBuffer: 16 * 1024 * 1024 });
  external.push({ args, startedUtc, startMs, settledMs: performance.now(), bytes: Buffer.byteLength(text) });
  return args.includes('--json') ? JSON.parse(text) : text;
}
const rows = bd(['--readonly', 'list', '--all', '--limit', '0', '--json']);
assert.equal(rows.length, manifest.inventory);
fs.writeFileSync(path.join(output, 'native-inventory.json'), JSON.stringify(rows, null, 2));
const nativeReady = bd(['--readonly', 'ready', '--limit', '0', '--json']).map(row => row.id);
const nativeBlocked = bd(['--readonly', 'blocked', '--json']).map(row => row.id);
assert(nativeBlocked.includes('qa-read-009'));
const active = process.env.NIGHTWEAVER_PROBE_ACTIVITY === 'active';
let current;
const activity = ['qa-live-1', 'qa-live-2', 'qa-live-planning'].map(id => ({ id,
  ready: fs.readFileSync(path.join(root, id + '.ready'), 'utf8'),
  held: !fs.existsSync(path.join(root, id + '.release')) }));
const factoryPath = path.join(root, 'telemetry', 'qa', 'factory.json');
const factoryBefore = JSON.parse(fs.readFileSync(factoryPath, 'utf8'));
for (const item of activity) {
  const slot = [...factoryBefore.delivery.slots, ...factoryBefore.planning.slots].find(slot => slot.bead_id === item.id);
  assert(slot?.invocation?.process_id > 0, 'Missing native process identity');
  item.invocation = slot.invocation;
  process.kill(slot.invocation.process_id, 0);
  item.aliveBefore = true;
}
if (!active) {
  for (const item of activity) fs.writeFileSync(path.join(root, item.id + '.release'), 'release');
  // Factory settlement is observed separately below, before measured navigation.
  await new Promise(resolve => setTimeout(resolve, 1500));
  await until(() => activity.every(item => {
    try { process.kill(item.invocation.process_id, 0); return false; } catch (error) {
      if (error.code !== 'ESRCH') throw error;
      return true;
    }
  }), 'Native fixture settlement', 10000);
}
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
page.setDefaultTimeout(90000);
const errors = [], resources = [], requests = [], samples = [], changes = [];
const requestTimes = new WeakMap(), receipts = [];
page.on('pageerror', error => errors.push(String(error)));
page.on('request', request => {
  const atMs = performance.now();
  requestTimes.set(request, atMs);
  if (request.url().includes('/api/')) requests.push({ atMs, url: request.url(), method: request.method() });
});
page.on('response', async response => {
  const atMs = performance.now();
  const key = resourceKey(response.url());
  if (!key) return;
  receipts.push({ key, atMs, requestAtMs: requestTimes.get(response.request()) ?? null, status: response.status() });
  try {
    const value = await response.json();
    resources.push({ key, atMs, status: response.status(), epoch: value.epoch, revision: value.revision,
      health: value.health, sources: value.sources, synchronization: value.synchronization,
      dataPresent: value.data !== null && value.data !== undefined, data: value.data });
  } catch { /* Retain incomplete receipt. It cannot pass source assertions. */ }
});
const latest = key => resources.findLast(row => row.key === key);
const id = manifest.selected;
const surfacePaths = { overview: '/overview', explorer: '/beads?view=all', attention: '/attention',
  workspace: `/beads/${id}`, digest: '/catch-up?window=24h' };
const keys = { overview: ['attention', 'digest:auto', 'factory'], explorer: ['explorer:all::1'],
  attention: ['attention'], workspace: [`workspace:${id}`], digest: ['digest:24h'] };
const baseKeys = { overview: 'base:overview', explorer: 'base:explorer:all::1', attention: 'base:attention',
  workspace: `base:workspace:${id}`, digest: 'base:digest:24h' };
const keysFor = surface => latest(baseKeys[surface])?.dataPresent
  ? [baseKeys[surface], ...(surface === 'overview' ? ['factory'] : [])] : keys[surface];
const requiredSources = key => key === 'factory' ? ['runtime'] : key.includes('digest:') || key === 'base:overview'
  ? ['inventory', 'checkpoint'] : ['inventory'];
async function until(predicate, reason, cutoff = 180000) {
  const start = performance.now();
  const deadline = Math.min(start + cutoff, current?.deadlineMs ?? Infinity);
  while (performance.now() < deadline) {
    if (await predicate()) return;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw Error(`Observation cutoff: ${reason}`);
}
async function rendered(surface) {
  return page.evaluate(({ surface, id }) => {
    const text = document.querySelector('main')?.innerText ?? '';
    if (surface === 'overview') return {
      attention: /8 paused Beads/.test(document.querySelector('.overview-summary')?.innerText ?? '')
        && document.querySelector('.attention-panel')?.innerText.includes(id),
      digest: /16 completed Beads/.test(document.querySelector('.digest-preview')?.innerText ?? ''),
      runtime: /Runtime.*live/.test(document.querySelector('.overview-summary')?.innerText ?? '')
        && document.querySelector('.floor-panel')?.innerText.length > 0
    };
    if (surface === 'explorer') return /50 matching Beads/.test(text)
      && document.querySelectorAll('.explorer-list > li').length === 40 && text.includes('Shared read fixture');
    if (surface === 'attention') return document.querySelectorAll('.attention-row').length === 8
      && text.includes(id) && text.includes('Shared read fixture 000');
    if (surface === 'workspace') return document.querySelector('h1')?.textContent === 'Shared read fixture 000'
      && !!document.querySelector('.bead-facts') && text.includes(id) && text.includes('stage:implement');
    return document.querySelector('#digest-metric-completed')?.textContent === '16'
      && document.querySelector('#digest-metric-open')?.textContent === '34'
      && !!document.querySelector('[data-bead-id]');
  }, { surface, id });
}
async function trial(surface, mode, cycle) {
  const sample = { surface, mode, cycle, activity: active ? 'active' : 'settled',
    startMs: performance.now(), startedUtc: new Date().toISOString(), httpMs: null, usefulMs: null, detailMs: null, error: null };
  sample.deadlineMs = sample.startMs + 180000;
  current = sample; samples.push(sample);
  const resourceStart = resources.length;
  try {
    const target = `${base}/projects/qa${surfacePaths[surface]}`;
    if (mode === 'cold') await page.goto(target);
    else {
      // Use the actual router. A document reload would discard the tab cache.
      await page.evaluate(target => {
        // React Router cannot handle an arbitrary anchor. Use an existing shell link.
        const pathname = new URL(target).pathname;
        const link = [...document.querySelectorAll('a')].find(node => new URL(node.href).pathname === pathname);
        if (!link) throw Error('No route link for ' + pathname);
        link.click();
      }, target);
      if (surface === 'explorer') {
        await page.getByRole('combobox', { name: 'View', exact: true }).selectOption('all');
        await page.getByRole('button', { name: 'Search project', exact: true }).click();
      }
    }
    if (surface === 'digest') {
      if (await page.locator('#digest-toggle').getAttribute('aria-expanded') === 'false') await page.locator('#digest-toggle').click();
      await page.locator('[data-digest-window="24h"]').click();
    }
    await until(async () => {
      const facts = await rendered(surface);
      if (surface === 'overview') {
        sample.panels ??= {};
        for (const [name, visible] of Object.entries(facts)) {
          if (visible && !sample.panels[name]) sample.panels[name] = { firstRenderedMs: performance.now() - sample.startMs };
        }
      }
      return (typeof facts === 'object' ? Object.values(facts).every(Boolean) : facts) && keysFor(surface).every(key => {
      const row = latest(key); return row?.dataPresent
        && requiredSources(key).every(source => row.sources[source]?.health === 'fresh');
      });
    }, surface);
    sample.usefulMs = performance.now() - sample.startMs;
    sample.proof = { expected: true,
      rendered: [await page.locator('main').innerText()],
      resources: keysFor(surface).map(key => ({ ...latest(key), required: requiredSources(key) })) };
    await until(() => keysFor(surface).every(key => latest(key)?.health === 'fresh'), `${surface} full response`);
    sample.fullResponseMs = performance.now() - sample.startMs;
    sample.detailSources = keysFor(surface).map(key => latest(key));
    if (surface === 'workspace') {
      sample.sectionRequestedMs = performance.now() - sample.startMs;
      await page.locator('.workspace-relations > summary').click();
      await page.locator('summary').filter({ hasText: 'Retained environment' }).click();
      await page.getByRole('link', { name: 'History', exact: true }).click();
      await until(() => page.locator('main').innerText().then(text => text.includes('Shared read history 000/7')), 'history');
      sample.history = { requestedMs: sample.sectionRequestedMs, completedMs: performance.now() - sample.startMs };
      sample.sections = { history: { state: 'available', requestedMs: sample.sectionRequestedMs,
        completedMs: performance.now() - sample.startMs },
        fullResponse: { state: 'available', completedMs: sample.fullResponseMs },
        output: { state: 'unmeasured', reason: 'Live output range is not fixed by this manifest.' } };
      await page.getByRole('link', { name: 'Summary', exact: true }).click();
      if (keysFor(surface)[0].startsWith('base:')) {
        const environment = page.locator('summary').filter({ hasText: 'Retained environment' });
        if (await environment.count()) await environment.click();
        const sectionKeys = ['text', 'history', 'relations', 'environment'].map(name => `section:${id}:${name}`);
        await until(() => sectionKeys.every(key => latest(key)?.health === 'fresh'
          && ['available', 'empty'].includes(latest(key)?.data?.state)
          && latest(key)?.data?.baseIdentity?.inventoryRevision), 'Independent section completion');
        sample.detailSources = sectionKeys.map(key => latest(key));
        sample.sections = Object.fromEntries(sectionKeys.map(key => [key, latest(key)]));
      }
      sample.detailMs = performance.now() - sample.startMs;
      const outputRequestedMs = performance.now() - sample.startMs;
      await page.getByRole('link', { name: 'Output', exact: true }).click();
      await page.getByRole('heading', { name: 'Invocation output', exact: true }).waitFor();
      sample.output = { requestedMs: outputRequestedMs, panelRenderedMs: performance.now() - sample.startMs,
        range: null, reason: 'Panel render only. This manifest does not select an invocation output range.' };
      await page.getByRole('link', { name: 'Summary', exact: true }).click();
    }
    sample.detailMs ??= performance.now() - sample.startMs;
    sample.requestedSections = surface === 'workspace' ? manifest.requested : ['visible panels'];
    sample.sectionTimingReason = keysFor(surface)[0].startsWith('base:')
      ? 'Independent section receipts and validated states are retained. Admission time remains unavailable.'
      : 'Legacy responses publish the full resource. Independent section admission and completion are not exposed.';
    // A warm tab can render validated retained facts without another receipt.
    sample.endMs = performance.now();
    Object.assign(sample, sampleReceipt(sample, receipts, sample.endMs));
    sample.completedUtc = new Date().toISOString();
    sample.cutoffUtc = sample.completedUtc;
    current = null;
    sample.requests = requests.filter(row => row.atMs >= sample.startMs);
    sample.newResourceCount = resources.length - resourceStart;
    assertSample(sample);
    await page.screenshot({ path: path.join(output, `${mode}-${cycle}-${surface}.png`), fullPage: true });
    console.log(`${mode} ${surface}: useful ${sample.usefulMs.toFixed(0)}ms; detail ${sample.detailMs.toFixed(0)}ms`);
  } catch (error) {
    sample.error = String(error);
    sample.failureRendered = await page.locator('main').innerText().catch(() => null);
    await page.screenshot({ path: path.join(output, `${mode}-${cycle}-${surface}-failed.png`), fullPage: true }).catch(() => {});
    throw error;
  }
  finally { sample.cutoffUtc ??= new Date().toISOString(); current = null; }
}
try {
  const coldSurface = process.env.NIGHTWEAVER_PROBE_SURFACE;
  assert(Object.hasOwn(surfacePaths, coldSurface));
  await trial(coldSurface, 'cold', 0);
  for (const surface of Object.keys(surfacePaths)) await trial(surface, 'conditioning', 0);
  for (let cycle = 0; cycle < Number(process.env.NIGHTWEAVER_PROBE_WARM_CYCLES ?? 1); cycle++) {
    // Establish all interests once, then retain the same validated tab state.
    for (const surface of Object.keys(surfacePaths)) await trial(surface, 'warm', cycle);
  }
  for (const width of [720, 375]) {
    await page.setViewportSize({ width, height: 900 });
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `Overflow at ${width}`);
    await page.screenshot({ path: path.join(output, `digest-${width}.png`), fullPage: true });
  }
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto(`${base}/projects/qa/beads/${id}`);
  await page.locator('.bead-facts').waitFor();
  const marker = 'Shared read external comment';
  const startMs = performance.now(), startedUtc = new Date().toISOString();
  bd(['comments', 'add', id, marker]);
  const settledMs = performance.now();
  await page.getByRole('link', { name: 'History', exact: true }).click();
  await until(() => page.locator('main').innerText().then(text => text.includes(marker)), marker);
  changes.push({ kind: 'comment', startedUtc, startMs, settledMs, renderedMs: performance.now(),
    startToRenderMs: performance.now() - startMs, settledToRenderMs: performance.now() - settledMs,
    source: latest(`section:${id}:history`) ?? latest(`workspace:${id}`) });
  const editor = page.getByRole('textbox', { name: `Guidance for ${id}` });
  await editor.fill('Shared read guarded note');
  const response = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname === `/api/beads/${id}/decide`);
  response.catch(() => {});
  const guardedStart = performance.now();
  await page.getByRole('button', { name: 'Save Note', exact: true }).click();
  const receipt = await response;
  assert.equal(receipt.status(), 200);
  const body = await receipt.json(); assert.equal(body.commentRecorded, true); assert.equal(body.stateChanged, false);
  changes.push({ kind: 'guarded-note', startMs: guardedStart, settledMs: performance.now(), body });
  assert(bd(['--readonly', 'comments', id, '--json']).some(row => (row.text ?? '').includes('Shared read guarded note')));
  const dependencyStart = performance.now(), dependencyStartedUtc = new Date().toISOString();
  bd(['dep', 'add', id, 'qa-read-010']);
  const dependencySettled = performance.now();
  await page.getByRole('link', { name: 'Summary', exact: true }).click();
  await page.locator('.workspace-relations > summary').click();
  await until(() => page.locator('main').innerText().then(text => text.includes('qa-read-010')), 'Relation visibility');
  const relationRendered = performance.now();
  const blocked = bd(['--readonly', 'blocked', '--json']);
  assert(blocked.some(row => row.id === id));
  changes.push({ kind: 'dependency', startedUtc: dependencyStartedUtc, startMs: dependencyStart,
    settledMs: dependencySettled, relationRenderedMs: relationRendered,
    startToRenderMs: relationRendered - dependencyStart, settledToRenderMs: relationRendered - dependencySettled,
    nativeBlocked: blocked.map(row => row.id), source: latest(`section:${id}:relations`) ?? latest(`workspace:${id}`) });
  const closeStart = performance.now();
  bd(['close', 'qa-read-010']);
  const closeSettled = performance.now();
  await page.goto(`${base}/projects/qa/beads?view=ready`);
  await until(() => page.locator('.explorer-list').innerText().then(text => text.includes('Shared read fixture 009')), 'Native Ready visibility');
  const readyRendered = performance.now();
  const ready = bd(['--readonly', 'ready', '--limit', '0', '--json']);
  assert(ready.some(row => row.id === 'qa-read-009'));
  changes.push({ kind: 'blocker-close', startMs: closeStart, settledMs: closeSettled,
    readyRenderedMs: readyRendered, startToRenderMs: readyRendered - closeStart,
    settledToRenderMs: readyRendered - closeSettled, nativeReady: ready.map(row => row.id), source: latest('explorer:ready::1') });
  if (active) for (const item of activity) process.kill(item.invocation.process_id, 0);
  assert.deepEqual(errors, []);
} finally {
  const factoryAfter = JSON.parse(fs.readFileSync(factoryPath, 'utf8'));
  for (const item of activity) {
    try { process.kill(item.invocation.process_id, 0); item.aliveAfter = true; }
    catch (error) { if (error.code !== 'ESRCH') throw error; item.aliveAfter = false; }
  }
  fs.writeFileSync(path.join(output, 'browser.json'), JSON.stringify({
    schema: 'nightweaver-shared-read-browser-v1', manifest, activity, active, factoryBefore, factoryAfter,
    clock: 'Node performance.now; launch-to-render includes Playwright transport and 100ms sampling.',
    clockIdentity: `${process.pid}/${performance.timeOrigin}`, clockOriginUtc: new Date(performance.timeOrigin).toISOString(),
    fixtureConfigSha256: createHash('sha256').update(fs.readFileSync(configPath)).digest('hex'),
    processId: process.pid, nativeReady, nativeBlocked, samples, resources, requests, receipts, changes, external, errors,
    missing: [...manifest.missing, 'Per-section publication and admission timestamps on the legacy full-response contract',
      'Separate gate, queue, projection and browser receive-to-render attribution',
      'Backup-audit render visibility', 'Fixed Output range', 'Cold/warm Ready and Blocked Explorer views and All page 3',
      'Second competing Workspace and refresh contention', 'Empty selected history and structured pause report controls',
      'Matching/malformed merge controls and 129-task evidence cap',
      'Five warm cycles and three cold repetitions unless explicitly requested']
  }, null, 2));
  await browser.close();
}
