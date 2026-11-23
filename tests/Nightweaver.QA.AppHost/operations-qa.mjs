// Run only on a fresh, exact isolated QA AppHost (README.md).
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import { chromium } from 'playwright';

const base = process.env.NIGHTWEAVER_QA_URL;
const configPath = process.env.NIGHTWEAVER_QA_CONFIG;
assert(base && configPath, 'Use the exact AppHost-discovered URL and config');
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
const output = path.resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/operations');
fs.mkdirSync(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
const errors = []; const posts = [];
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (request.method() === 'POST') posts.push(new URL(request.url()).pathname); });
  await page.addInitScript(() => {
    const Native = window.EventSource;
    window.streamEvidence = { active: 0, maximum: 0, opened: 0, hello: 0, invalidation: 0, drop: false };
    window.EventSource = class extends Native {
      constructor(...args) {
        super(...args); if (!String(args[0]).includes('/operations/v1/')) return;
        const evidence = window.streamEvidence;
        evidence.active++; evidence.opened++; evidence.maximum = Math.max(evidence.maximum, evidence.active);
        window.currentOperationStream = this;
        super.addEventListener('hello', () => evidence.hello++);
        super.addEventListener('invalidation', () => evidence.invalidation++);
        const close = this.close.bind(this); let closed = false;
        this.close = () => { if (!closed) { closed = true; evidence.active--; } close(); };
      }
      addEventListener(name, callback, options) {
        super.addEventListener(name, event => {
          if (name === 'invalidation' && window.streamEvidence.drop) return;
          callback(event);
        }, options);
      }
    };
  });
  await page.goto(base + '/studio');
  await page.waitForFunction(() => /^\d+/.test(document.querySelector('#attention-navigation-count')?.textContent.trim() ?? ''), null, { timeout: 90000 });
  const initial = parseInt(await page.locator('#attention-navigation-count').innerText(), 10);
  for (const route of ['attention', 'beads', 'delivery', 'overview']) {
    await page.locator(`nav a[href="/projects/qa/${route}"]`).click();
    await page.waitForURL(`**/projects/qa/${route}`);
  }
  const mounted = await page.evaluate(() => window.streamEvidence);
  assert.equal(mounted.maximum, 1, 'route subscriptions share one active connection');
  assert.equal(mounted.active, 1);
  const helloBeforeReconnect = mounted.hello;
  // Deliberately miss notifications while leaving the real stream and source alive.
  await page.evaluate(() => { window.streamEvidence.drop = true; });
  const created = JSON.parse(execFileSync('bd', ['--sandbox', 'create', '--title', 'Operational stream QA pause', '--type', 'task', '--labels', 'needs-human', '--json'],
    { cwd: repo, env, encoding: 'utf8', timeout: 30000 }));
  assert(created.id);
  await page.waitForFunction(expected => parseInt(document.querySelector('#attention-navigation-count')?.textContent ?? '', 10) === expected, initial + 1, { timeout: 90000 });
  await page.locator(`#attention-navigation-count[aria-label="${initial + 1} paused Beads"]`).waitFor();
  // Reuse this native journey for the additive section transport. No extra
  // fixture, AppHost or browser is needed.
  const sectionKeys = [`base:workspace:${created.id}`, `section:${created.id}:text`, `section:${created.id}:history`];
  async function waitForSections(keys, ready) {
    const deadline = Date.now() + 90000;
    while (Date.now() < deadline) {
      const observations = await page.evaluate(async keys => Promise.all(keys.map(async key => {
        const response = await fetch(`/api/operations/v1/projects/qa/resources?key=${encodeURIComponent(key)}`);
        return { status: response.status, ...(await response.json()) };
      })), keys);
      fs.writeFileSync(path.join(output, 'section-attempt.json'), JSON.stringify(observations, null, 2));
      assert.ok(observations.every(item => item.status === 200), 'Section resources are admitted');
      if (ready(observations)) return observations;
      await page.waitForTimeout(250);
    }
    assert.fail('Section observation did not converge; see section-attempt.json');
  }
  const coldSections = await waitForSections(sectionKeys,
    observations => observations.every(item => item.health === 'fresh' && item.data && !item.synchronization?.pending));
  assert.equal(coldSections[1].data.schema, 'nightweaver-section-v1');
  assert.equal(coldSections[2].data.state, 'empty');
  assert.deepEqual(coldSections[2].data.value.comments, []);
  execFileSync('bd', ['--sandbox', 'comments', 'add', created.id, 'Shared section history QA'],
    { cwd: repo, env, encoding: 'utf8', timeout: 30000 });
  const [changedSection] = await waitForSections([sectionKeys[2]], ([observation]) =>
    observation.health === 'fresh' && observation.data?.value?.comments.some(item => item.body === 'Shared section history QA'));
  assert.equal(changedSection.data.state, 'available');
  assert.equal(changedSection.data.beadId, created.id);
  assert.equal(changedSection.data.baseIdentity.updatedAt, coldSections[0].data.bead.updatedAt);
  fs.writeFileSync(path.join(output, 'sections.json'), JSON.stringify({ coldSections, changedSection }, null, 2));
  // Force a real transport replacement; this must hello/resync, not replay commands.
  await page.evaluate(() => {
    window.streamEvidence.drop = false;
    window.currentOperationStream.close();
    window.currentOperationStream.dispatchEvent(new Event('error'));
  });
  await page.waitForFunction(before => window.streamEvidence.hello > before && document.querySelector('.synchronization')?.textContent.includes('Live connection'), helloBeforeReconnect, { timeout: 90000 });
  const refresh = page.waitForResponse(response => response.url().endsWith('/operations/v1/projects/qa/refresh') && response.request().method() === 'POST');
  await page.locator('.synchronization').getByRole('button', { name: /^Refresh/ }).click();
  assert.equal((await refresh).status(), 202);
  for (const width of [1440, 375, 720]) {
    await page.setViewportSize({ width, height: 1000 });
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), `No overflow at ${width}`);
    await page.screenshot({ path: path.join(output, `live-${width}.png`), fullPage: true });
  }
  assert.deepEqual(errors, []);
  assert.ok(posts.every(url => url.endsWith('/operations/v1/projects/qa/refresh')), 'No commands replayed');
  const stream = await page.evaluate(() => window.streamEvidence);
  assert.equal(stream.maximum, 1); assert.equal(stream.active, 1); assert.ok(stream.invalidation > 0);
  await page.goto(base + '/projects/foreign/overview');
  await page.getByRole('heading', { name: 'Project unavailable' }).waitFor();
  assert.equal((await page.evaluate(() => window.streamEvidence)).opened, 0, 'foreign project opens no stream');
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify({ initial, converged: initial + 1, stream, posts, errors }, null, 2));
  console.log(JSON.stringify({ initial, converged: initial + 1, stream, posts, errors }));
} finally { await browser.close(); }
