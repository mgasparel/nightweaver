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
const selected = paused[0].id;
for (const row of paused.slice(1)) bd(['update', row.id, '--remove-label', 'needs-human', '--remove-label', 'product-decision-required']);
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
      observedAt: value.observedAt, sources: value.sources, error: value.error?.code });
  });
  await page.goto(`${base}/projects/qa/beads/${selected}?tab=history`);
  await page.waitForFunction(() => document.querySelector('.synchronization [role=status]')?.textContent === 'Live connection · Tracker fresh'
    && [...document.querySelectorAll('.status')].some(element => element.textContent === 'fresh'));
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
  await page.waitForTimeout(16000);
  const states = await page.evaluate(() => window.sourceStates);
  assert(responses.length >= 8, 'Observe multiple real collection cycles');
  assert(responses.every(value => value.health === 'fresh'), 'Successful unchanged source reads must not age-flash stale between collections');
  assert(states.every(state => state.header === 'Live connection · Tracker fresh' && !state.badges.includes('stale')));
  steady = { responses: responses.slice(), states };
  const comment = 'Source freshness external edit ' + Date.now();
  bd(['comments', 'add', selected, comment]);
  const added = page.getByText(comment, { exact: true });
  await added.waitFor({state:'attached'});
  await page.locator('.bead-comment').filter({has:added}).locator('summary').click();
  await added.waitFor();
  for (const width of [1440,375,720]) {
    await page.setViewportSize({width,height:1000});
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({path:path.join(output,`history-${width}.png`),fullPage:true});
  }
  assert.deepEqual(errors, []);
  console.log(JSON.stringify({checks:{stableNativeCollections:true,externalCommentVisible:true}, responses:responses.length, states}));
} finally {
  fs.writeFileSync(path.join(output,'evidence.json'),JSON.stringify({steady,responses,errors,states:page ? await page.evaluate(() => window.sourceStates).catch(() => null) : null},null,2));
  await browser.close();
}
