// Only the disposable AppHost discovered by studio-cutover.ps1 is eligible.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import path from 'node:path';
import { chromium } from 'playwright';
const base = process.env.NIGHTWEAVER_QA_URL;
const configPath = process.env.NIGHTWEAVER_QA_CONFIG;
assert(base && configPath);
const root = path.dirname(configPath);
assert(path.basename(root).startsWith('nightweaver-qa-'));
const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));
assert.equal(config.name, 'qa');
assert.equal(path.resolve(config.target_repository.path), path.join(root, 'repo'));
const env = { ...process.env };
for (const key of Object.keys(env)) if (/^(BEADS_|BD_|GIT_)/.test(key)) delete env[key];
for (const key of ['HOME','USERPROFILE','APPDATA','XDG_CONFIG_HOME']) env[key] = path.join(root, 'home');
Object.assign(env, { GIT_CONFIG_GLOBAL: path.join(root,'home','gitconfig'), GIT_CONFIG_NOSYSTEM: '1', BD_DISABLE_METRICS: '1', BD_NO_HOOKS: 'true', BD_DOLT_AUTO_START: 'false', OTEL_SDK_DISABLED: 'true' });
for (const key of ['BD_IMPORT_AUTO','BD_EXPORT_AUTO','BD_BACKUP_ENABLED','BD_DOLT_AUTO_PUSH','BD_EVENTS_EXPORT']) env[key] = 'false';
const bd = args => JSON.parse(execFileSync('bd', ['--sandbox', ...args, '--json'], { cwd: config.target_repository.path, env, encoding: 'utf8', timeout: 30000 }));
const output = path.resolve(process.env.NIGHTWEAVER_QA_OUTPUT || 'TestResults/studio-recovery');
fs.mkdirSync(output, { recursive: true });
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  page.setDefaultTimeout(90000);
  const errors = [], commands = [];
  page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => { if (r.method() === 'POST') commands.push(r.url()); });
  const rows = ['first', 'second'].map(name => bd(['create', '--title', `Recovery ${name}`, '--type', 'feature', '--labels', 'product-decision-required']));
  await page.goto(base);
  await page.getByRole('navigation').getByRole('link', { name: 'Attention', exact: true }).click();
  const select = async row => { await page.getByRole('button', { name: `Review ${row.id}`, exact: true }).click(); return page.getByRole('textbox', { name: `Guidance for ${row.id}` }); };
  for (const row of rows) await (await select(row)).fill(`Draft bound to ${row.id}`);
  const input = await select(rows[0]);
  await input.focus();
  const before = await (await page.request.get(base + '/api/operations/v1/projects/qa/resources?key=attention')).json();
  const apphost = path.resolve('tests/Nightweaver.QA.AppHost/Nightweaver.QA.AppHost.csproj');
  assert(fs.existsSync(apphost));
  // Exact resource restart keeps the disposable tracker and direct binding.
  execFileSync('pwsh', ['-NoProfile', '-Command', `& aspire resource web restart --non-interactive --apphost '${apphost.replaceAll("'", "''")}'; if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }; & aspire wait web --non-interactive --apphost '${apphost.replaceAll("'", "''")}'; exit $LASTEXITCODE`], { encoding: 'utf8', timeout: 60000, windowsHide: true });
  let after;
  await assert.doesNotReject(async () => {
    await page.waitForFunction(() => document.querySelector('.synchronization')?.textContent.includes('Live connection'), null, { timeout: 90000 });
    await page.waitForFunction(() => ![...document.querySelectorAll('button')].find(n => n.textContent === 'Send & Resume')?.disabled, null, { timeout: 90000 });
    after = await (await page.request.get(base + '/api/operations/v1/projects/qa/resources?key=attention')).json();
    assert.notEqual(after.epoch, before.epoch);
  });
  assert.equal(await input.inputValue(), `Draft bound to ${rows[0].id}`);
  assert(await input.evaluate(n => n === document.activeElement));
  // Simulate browser suspension/wake lifecycle without reloading memory-only drafts.
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pagehide')));
  for (let index = 0; index < 3; index++) {
    bd(['update', rows[0].id, '--title', `Burst edit ${index}`]);
    bd(['comments', 'add', rows[0].id, `External overnight evidence ${index}`]);
  }
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pageshow')));
  await page.locator('h2[tabindex="-1"]').filter({ hasText: /^Burst edit 2$/ }).waitFor();
  assert.equal(await input.inputValue(), `Draft bound to ${rows[0].id}`);
  assert(await input.evaluate(n => n === document.activeElement));
  assert.equal(await (await select(rows[1])).inputValue(), `Draft bound to ${rows[1].id}`);
  for (const width of [1440,375,720]) {
    await page.setViewportSize({ width, height: 900 });
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    await page.screenshot({ path: path.join(output, `recovered-${width}.png`), fullPage: true });
  }
  assert.deepEqual(errors, []); assert.deepEqual(commands, []);
  const result = { passed: true, epochChanged: before.epoch !== after.epoch, concurrentDrafts: 2, burstEdits: 3, lifecycleWake: true, commands, errors };
  fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify(result));
} finally { await browser.close(); }
