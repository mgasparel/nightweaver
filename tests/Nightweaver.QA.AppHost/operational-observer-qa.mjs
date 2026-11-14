// Requires a fresh isolated QA AppHost; see README.md. Never accepts a target repository.
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
assert.equal(repo, path.join(root, 'repo'));
assert.equal(config.name, 'qa');
const env = { ...process.env };
for (const key of Object.keys(env)) if (/^(BEADS_|BD_|GIT_)/.test(key)) delete env[key];
for (const key of ['HOME', 'USERPROFILE', 'APPDATA', 'XDG_CONFIG_HOME']) env[key] = path.join(root, 'home');
Object.assign(env, { GIT_CONFIG_GLOBAL: path.join(root, 'home', 'gitconfig'), GIT_CONFIG_NOSYSTEM: '1',
  BD_DISABLE_METRICS: '1', BD_NO_HOOKS: 'true', BD_DOLT_AUTO_START: 'false', OTEL_SDK_DISABLED: 'true' });
for (const key of ['BD_IMPORT_AUTO', 'BD_EXPORT_AUTO', 'BD_BACKUP_ENABLED', 'BD_DOLT_AUTO_PUSH', 'BD_EVENTS_EXPORT']) env[key] = 'false';
const commands = [];
function bd(args, read = true) {
  const started = performance.now();
  const output = execFileSync('bd', [...(read ? ['--readonly', '--sandbox'] : ['--sandbox']), ...args],
    { cwd: repo, env, encoding: 'utf8', timeout: 30000, maxBuffer: 16 * 1024 * 1024 });
  commands.push({ command: args[0], ms: +(performance.now() - started).toFixed(2) });
  return args.includes('--json') ? JSON.parse(output) : output;
}
const inventory = () => bd(['list', '--all', '--limit', '0', '--include-ephemeral', '--include-gates', '--include-infra', '--include-templates', '--json']);
const output = process.argv[2];
assert(output, 'Supply evidence output path');
const browser = await chromium.launch({ headless: true });
const page = await browser.newPage();
const errors = [];
page.on('pageerror', error => errors.push(String(error)));
const pollIntervalMs = 1000;
const evidence = { measuredAt: new Date().toISOString(), schema: 'nightweaver-observation-v1', pollIntervalMs, fixtures: [], runtime: [], commands };
const start = performance.now();
try {
  await page.goto(base + '/studio');
  async function read(key) {
    return page.evaluate(async key => {
      const response = await fetch('/api/operations/v1/projects/qa/resources?key=' + encodeURIComponent(key));
      if (!response.ok) throw new Error('Resource read ' + response.status);
      const value = await response.json();
      if (response.headers.get('cache-control') !== 'no-store') throw new Error('Missing no-store');
      return value;
    }, key);
  }
  async function until(keys, predicate, timeout = 90000) {
    const began = performance.now();
    let state;
    while (performance.now() - began < timeout) {
      state = Object.fromEntries(await Promise.all(keys.map(async key => [key, await read(key)])));
      if (predicate(state)) return { state, ms: +(performance.now() - began).toFixed(2) };
      await new Promise(resolve => setTimeout(resolve, pollIntervalMs));
    }
    throw new Error('Observation timed out: ' + JSON.stringify(Object.fromEntries(Object.entries(state).map(([key, value]) =>
      [key, { health: value.health, error: value.error?.code, kind: typeof value.data,
        rows: Array.isArray(value.data) ? value.data.length : null, sources: Object.keys(value.sources).length }]))));
  }
  const factory = (await until(['factory'], state => state.factory.data?.snapshot)).state.factory;
  assert.equal(factory.projectId, 'qa');
  assert.match(factory.revision, /^[1-9][0-9]*$/);
  assert.match(factory.observedAt, /Z$/);
  const runtimeBefore = factory.data.snapshot.observed_at;
  const runtimeChanged = await until(['factory'], state => state.factory.data?.snapshot?.observed_at !== runtimeBefore, 10000);
  const runtimeAfter = runtimeChanged.state.factory;
  evidence.runtime.push({ pollVisibilityMs: runtimeChanged.ms,
    snapshotToHttpMs: Date.now() - Date.parse(runtimeAfter.data.snapshot.observed_at),
    revisionStableAcrossHeartbeat: runtimeAfter.revision === factory.revision,
    health: runtimeAfter.health });

  for (const [size, pausedCount] of [[50, 4], [2000, 32]]) {
    const before = inventory();
    const added = size - before.length;
    assert(added > pausedCount, 'Start a fresh QA AppHost');
    const records = Array.from({ length: added }, (_, index) => ({ id: `qa-observe-${size}-${index}`, title: `Observer ${size} item ${index}`,
      description: 'Representative specification. '.repeat(32), issue_type: 'task', status: 'open', priority: 2,
      labels: index < pausedCount ? ['needs-human'] : [], created_at: '2026-11-14T00:00:00Z', updated_at: '2026-11-14T00:00:00Z' }));
    const input = path.join(root, `observer-${size}.jsonl`);
    fs.writeFileSync(input, records.map(record => JSON.stringify(record)).join('\n'), 'utf8');
    bd(['import', input], false);
    const actual = inventory();
    assert.equal(actual.length, size);
    const paused = actual.filter(row => row.status !== 'closed' && row.labels?.some(label => ['needs-human', 'product-decision-required'].includes(label))).length;
    const selected = records.at(-1).id;
    const blocker = records.at(-2).id;
    const workspace = 'workspace:' + selected;
    const keys = ['factory', 'attention', 'explorer:all:', 'explorer:ready:', 'explorer:blocked:', 'digest:all', workspace];
    const baseline = await until(keys, state => state[workspace].data?.bead?.id === selected
      && state.attention.data?.length === paused && state['explorer:all:'].data?.length === size);
    for (const value of Object.values(baseline.state)) {
      assert.equal(value.epoch, factory.epoch);
      assert.equal(value.schema, evidence.schema);
    }
    const rowBefore = inventory().find(row => row.id === selected);
    const marker = `External comments-only marker ${size}`;
    bd(['comments', 'add', selected, marker], false);
    const comment = await until(keys, state => state[workspace].data?.comments?.some(value => value.body === marker));
    const rowAfter = inventory().find(row => row.id === selected);
    assert.notEqual(comment.state[workspace].revision, baseline.state[workspace].revision);
    bd(['dep', 'add', selected, blocker], false);
    const dependency = await until(keys, state => state[workspace].data?.relations?.some(value => value.id === blocker)
      && !state['explorer:ready:'].data?.some(value => value.id === selected)
      && state['explorer:blocked:'].data?.some(value => value.id === selected));
    bd(['close', blocker], false);
    const readiness = await until(keys, state => state['explorer:ready:'].data?.some(value => value.id === selected));
    const checkpoint = comment.state['digest:all'].data.generatedAt;
    const writeCheckpoint = await page.evaluate(async caughtUpAt => {
      const response = await fetch('/api/digest/catch-up', { method: 'POST', headers: {
        'Content-Type': 'application/json', 'X-Nightweaver-Request': 'console-v1' }, body: JSON.stringify({ caughtUpAt }) });
      return response.status;
    }, checkpoint);
    assert.equal(writeCheckpoint, 200);
    const digest = await until(keys, state => Date.parse(state['digest:all'].data?.lastCaughtUpAt) === Date.parse(checkpoint));
    evidence.fixtures.push({ inventoryRows: size, pausedRows: paused, initialCompleteMs: baseline.ms,
      commentsVisibilityMs: comment.ms, inventoryRowChangedByComment: JSON.stringify(rowBefore) !== JSON.stringify(rowAfter),
      dependencyAndMembershipVisibilityMs: dependency.ms, readinessVisibilityMs: readiness.ms,
      checkpointVisibilityMs: digest.ms, health: Object.fromEntries(keys.map(key => [key, digest.state[key].health])),
      fiveSecondTargetsMet: [comment.ms, dependency.ms, readiness.ms].every(value => value < 5000) });
    console.log(JSON.stringify(evidence.fixtures.at(-1)));
  }
  // Exercise runtime activity through the disposable native fixture, not a synthetic snapshot.
  fs.writeFileSync(path.join(root, 'qa-live-1.release'), 'release');
  const runtimeActivity = await until(['factory'], state => state.factory.data?.snapshot?.delivery?.slots
    ?.some(slot => slot.slot_id === 'delivery-1' && slot.invocation?.status === 'completed'), 15000);
  evidence.runtime.push({ activityVisibilityMs: runtimeActivity.ms, twoSecondTargetMet: runtimeActivity.ms < 2000 });
  assert.deepEqual(errors, []);
  console.log('Operational observer browser checks passed');
} catch (error) {
  evidence.failure = String(error);
  throw error;
} finally {
  evidence.totalMs = +(performance.now() - start).toFixed(2);
  fs.writeFileSync(output, JSON.stringify(evidence, null, 2) + '\n');
  await browser.close();
}
