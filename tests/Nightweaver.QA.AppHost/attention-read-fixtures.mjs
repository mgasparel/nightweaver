import { readFileSync } from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
import assert from 'node:assert/strict';

// Reuse the exact disposable stack. An external native write must remove a row
// through the real journal and shared base collector, without a browser refresh.
export async function liveAttentionRemoval(page, url, base, projectId) {
  const configPath = process.env.NIGHTWEAVER_QA_CONFIG;
  const root = path.dirname(path.resolve(configPath));
  const config = JSON.parse(readFileSync(configPath, 'utf8'));
  assert(path.basename(root).startsWith('nightweaver-qa-'));
  assert.equal(path.resolve(config.target_repository.path), path.join(root, 'repo'));
  assert.equal(config.name, projectId);
  await page.goto(url + base + '/attention');
  await page.locator('.attention-row').first().waitFor();
  const observed = await (await page.request.get(`${url}/api/operations/v1/projects/${projectId}/resources?key=base:attention`)).json();
  const rows = observed.data.items;
  assert(rows.length > 1);
  const id = rows.at(-1).id;
  const member = page.getByRole('button', { name: `Review ${id}`, exact: true });
  await member.waitFor();
  const env = { ...process.env };
  for (const key of Object.keys(env)) if (/^(BEADS_|BD_|GIT_)/.test(key)) delete env[key];
  for (const key of ['HOME', 'USERPROFILE', 'APPDATA', 'XDG_CONFIG_HOME']) env[key] = path.join(root, 'home');
  Object.assign(env, { GIT_CONFIG_GLOBAL: path.join(root, 'home', 'gitconfig'), GIT_CONFIG_NOSYSTEM: '1',
    BD_DISABLE_METRICS: '1', BD_NO_HOOKS: 'true', BD_DOLT_AUTO_START: 'false', OTEL_SDK_DISABLED: 'true' });
  for (const key of ['BD_IMPORT_AUTO', 'BD_EXPORT_AUTO', 'BD_BACKUP_ENABLED', 'BD_DOLT_AUTO_PUSH', 'BD_EVENTS_EXPORT']) env[key] = 'false';
  execFileSync('bd', ['--sandbox', 'update', id, '--remove-label', 'product-decision-required', '--remove-label', 'needs-human'],
    { cwd: config.target_repository.path, env, encoding: 'utf8', timeout: 30000 });
  await member.waitFor({ state: 'detached', timeout: 90000 });
  await page.goto(url + base + '/overview');
  await page.waitForFunction(count => document.querySelector('.overview-summary')?.textContent.includes(`${count} paused Beads`), rows.length - 1);
  console.log(JSON.stringify({ nativePauseRemoval: id, before: rows.length, after: rows.length - 1 }));
}

// Deterministic read fixtures keep the additive wire shapes separate.
export async function readAttentionFixture(page, url, projectId) {
  let value;
  for (let attempt = 0; attempt < 90; attempt++) {
    value = await (await page.request.get(`${url}/api/operations/v1/projects/${projectId}/resources?key=attention`)).json();
    if (value.data) return value;
    await page.waitForTimeout(200);
  }
  throw Error(`Legacy Attention fixture did not complete: ${JSON.stringify(value)}`);
}
export function attentionBase(items, maxRepairCycles = 3) {
  return { schema: 'nightweaver-base-v1', inventoryRevision: '1', completeness: 'complete', absence: null,
    maxRepairCycles, items: items.map(item => ({ id: item.beadId, title: item.title, type: item.type,
      status: item.status, priority: item.priority, stage: item.stage, labels: [item.kind], repairCycles: item.repairCycles ?? 0,
      updatedAt: '2026-01-01T00:00:00Z', actions: { canDecide: item.canDecide, canRetry: item.canRetry,
        canAbort: item.canAbort, unavailableReason: item.unavailableReason,
        expectedWorkflow: { stage: item.stage, pauseLabels: [item.kind], repairCycles: item.repairCycles ?? 0 } } })) };
}
export function attentionHistory(key, items) {
  const id = key.split(':')[1];
  const item = items.find(item => item.beadId === id);
  if (!item) return null;
  const row = attentionBase([item]).items[0];
  return { schema: 'nightweaver-section-v1', beadId: id, name: 'history', state: 'available',
    baseIdentity: { inventoryRevision: '1', updatedAt: row.updatedAt, expectedWorkflow: row.actions.expectedWorkflow },
    value: { comments: [], history: { activeCommentId: item.sourceCommentId, selectionSource: 'structured' }, attention: item } };
}
