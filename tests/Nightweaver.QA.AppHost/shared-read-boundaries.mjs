import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';

const readTools = output => fs.readdirSync(path.join(output, 'tools')).filter(name => name.endsWith('.json'))
  .map(name => JSON.parse(fs.readFileSync(path.join(output, 'tools', name), 'utf8')));
async function until(read, accept, reason) {
  const cutoff = performance.now() + 95000;
  while (performance.now() < cutoff) {
    const value = await read();
    if (accept(value)) return value;
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  throw Error(`Boundary cutoff: ${reason}`);
}
async function observation(base, key) {
  const response = await fetch(`${base}/api/operations/v1/projects/qa/resources?key=${encodeURIComponent(key)}`);
  assert.equal(response.status, 200);
  return response.json();
}
export async function coldBase({ base, output, selected }) {
  const evidence = { startedUtc: new Date().toISOString(), reads: [] };
  const file = path.join(output, 'cold-base-boundary.json');
  try {
    for (const id of [selected, 'qa-read-missing']) {
      const key = `base:workspace:${id}`;
      const value = await until(async () => {
        const value = await observation(base, key);
        evidence.reads.push({ atUtc: new Date().toISOString(), value });
        return value;
      }, value => value.data?.inventoryRevision && value.sources.inventory?.health === 'fresh', key);
      if (id === selected) assert.equal(value.data.bead.id, selected);
      else {
        assert.equal(value.data.bead, null);
        assert.equal(value.data.absence.id, id);
        assert.equal(value.data.absence.inventoryRevision, value.data.inventoryRevision);
      }
    }
    evidence.finishedUtc = new Date().toISOString();
    const tools = readTools(output).filter(row => row.startedUtc >= evidence.startedUtc && row.startedUtc <= evidence.finishedUtc);
    evidence.tools = tools;
    evidence.starts = fs.readdirSync(path.join(output, 'tools')).filter(name => name.endsWith('.pending'))
      .map(name => JSON.parse(fs.readFileSync(path.join(output, 'tools', name), 'utf8')))
      .filter(row => row.startedUtc >= evidence.startedUtc && row.startedUtc <= evidence.finishedUtc);
    assert(tools.some(row => row.tool === 'bd' && row.args.includes('list') && row.exitCode === 0));
    assert(!evidence.starts.some(row => row.tool === 'bd' && ['show', 'comments', 'dep'].some(command => row.args.includes(command))),
      'Base-only cold direct interests must use shared inventory without targeted detail commands');
    evidence.passed = true;
  } finally { fs.writeFileSync(file, JSON.stringify(evidence, null, 2)); }
}
export async function auditRecovery({ page, base, output, selected, bd }) {
  const file = path.join(output, 'audit-boundary.json');
  const faultFile = path.join(output, 'faults.json');
  const fault = value => {
    fs.writeFileSync(faultFile + '.new', JSON.stringify(value));
    fs.renameSync(faultFile + '.new', faultFile);
  };
  const evidence = { startedUtc: new Date().toISOString(), reads: [] };
  const key = `section:${selected}:history`;
  try {
    await page.goto(`${base}/projects/qa/beads/${selected}?tab=history`);
    await page.getByText('Shared read external comment', { exact: true }).first().waitFor();
    const competing = 'section:qa-read-001:history';
    const editor = page.getByRole('textbox', { name: `Guidance for ${selected}` });
    await editor.fill('Shared read competing-detail note');
    evidence.competingBefore = await observation(base, competing);
    const refreshStatus = await page.evaluate(async key => (await fetch('/api/operations/v1/projects/qa/refresh', {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Nightweaver-Request': 'console-v1' },
      body: JSON.stringify({ keys: [key] })
    })).status, competing);
    assert.equal(refreshStatus, 202);
    const guarded = page.waitForResponse(response => response.request().method() === 'POST'
      && new URL(response.url()).pathname === `/api/beads/${selected}/decide`);
    guarded.catch(() => {});
    evidence.guardedStartedUtc = new Date().toISOString();
    const guardedStart = performance.now();
    await page.getByRole('button', { name: 'Save Note', exact: true }).click();
    const receipt = await guarded;
    evidence.guarded = { status: receipt.status(), body: await receipt.json(), elapsedMs: performance.now() - guardedStart };
    assert.equal(evidence.guarded.status, 200);
    assert.equal(evidence.guarded.body.commentRecorded, true);
    assert.equal(evidence.guarded.body.stateChanged, false);
    evidence.competingAfter = await until(() => observation(base, competing), value => value.health === 'fresh'
      && ['available','empty'].includes(value.data?.state), 'competing selected history');
    assert(bd(['--readonly', 'comments', selected, '--json']).some(row => (row.text ?? '').includes('Shared read competing-detail note')));
    fault({ events: true });
    await until(async () => readTools(output), rows => rows.some(row => row.injectedFailure && row.args.includes('events')), 'journal refusal');
    const marker = 'Shared read audit without journal hint';
    evidence.writeStartedUtc = new Date().toISOString();
    const start = performance.now();
    bd(['comments', 'add', selected, marker]);
    evidence.writeSettledMs = performance.now() - start;
    // Keep the same mounted interest. Do not request refresh or navigate after the write.
    await page.getByText(marker, { exact: true }).first().waitFor({ timeout: 95000 });
    evidence.renderedMs = performance.now() - start;
    evidence.history = await observation(base, key);
    assert(evidence.history.data.value.comments.some(row => row.body === marker));
    assert.equal(evidence.history.sources[`comments:${selected}`].health, 'fresh');
    // A failed complete inventory validation must retain facts, never assert absence.
    fault({ events: true, list: true });
    const baseKey = `base:workspace:${selected}`;
    const refresh = await page.evaluate(async key => (await fetch('/api/operations/v1/projects/qa/refresh', {
      method: 'POST', headers: { 'Content-Type': 'application/json', 'X-Nightweaver-Request': 'console-v1' },
      body: JSON.stringify({ keys: [key] })
    })).status, baseKey);
    assert.equal(refresh, 202);
    evidence.failedValidation = await until(async () => {
      const value = await observation(base, baseKey); evidence.reads.push(value); return value;
    }, value => value.sources.inventory?.error && value.data?.bead?.id === selected, 'failed inventory retention');
    assert.equal(evidence.failedValidation.data.absence, null);
    assert(await page.getByRole('heading', { name: 'Shared read fixture 000', exact: true }).isVisible());
    assert.equal(await page.getByText('This Bead no longer exists. Actions are unavailable.', { exact: true }).count(), 0);
    await page.screenshot({ path: path.join(output, 'native-failed-inventory.png'), fullPage: true });
    fault({});
    evidence.recovered = await until(() => observation(base, baseKey), value => value.sources.inventory?.health === 'fresh'
      && value.data?.bead?.id === selected, 'inventory audit recovery');
    evidence.passed = true;
  } finally {
    fault({});
    evidence.finishedUtc = new Date().toISOString();
    evidence.tools = readTools(output).filter(row => row.startedUtc >= evidence.startedUtc);
    fs.writeFileSync(file, JSON.stringify(evidence, null, 2));
  }
}
