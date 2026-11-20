// Deterministic PromQL evaluation; no server, native Agent journey or hosted access.
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { spawnSync } from 'node:child_process';
import assert from 'node:assert/strict';

const evidence = resolve(process.argv[2]);
mkdirSync(evidence, { recursive: true });
const dashboard = JSON.parse(readFileSync(process.argv[3] ?? new URL('../../observability/first-path-dashboard.json', import.meta.url)));
const panel = dashboard.panels.find(p => p.title === 'Known-history first-pass approvals');
assert.ok(panel, 'Approval panel must exist in the generated dashboard');
const expr = panel.targets[0].expr.replaceAll('$__range', '5m');
const series = (coverage, values, role = 'Reviewer') => ({
  series: `nightweaver_approvals_total{role="${role}",coverage="${coverage}"}`, values
});
const sample = (value, role = 'Reviewer') => ({ labels: `{role="${role}"}`, value });
const cases = [
  ['later-pass only', [series('LaterPass', '0+1x5')], [sample(0)]],
  ['mixed known approvals', [series('FirstPass', '0+1x5'), series('LaterPass', '0+3x5')], [sample(0.25)]],
  ['first-pass only', [series('FirstPass', '0+1x5')], [sample(1)]],
  ['unknown only', [series('Unknown', '0+1x5')], []],
  ['no approvals', [], []],
  ['zero known denominator', [series('FirstPass', '4+0x5'), series('LaterPass', '2+0x5')], []],
  ['unknown excluded', [series('FirstPass', '0+1x5'), series('LaterPass', '0+3x5'), series('Unknown', '0+20x5')], [sample(0.25)]],
  ['role isolation', [series('LaterPass', '0+1x5'), series('FirstPass', '0+1x5', 'QA'), series('Unknown', '0+1x5', 'Planner')], [sample(0), sample(1, 'QA')]]
];
const fixture = { evaluation_interval: '1m', tests: cases.map(([name, input_series, exp_samples]) => ({
  name, interval: '1m', input_series,
  promql_expr_test: [{ expr, eval_time: '5m', exp_samples }]
})) };
// JSON is a YAML subset accepted by promtool, avoiding a test-only YAML dependency.
writeFileSync(join(evidence, 'approval-ratio.yml'), JSON.stringify(fixture, null, 2) + '\n');
const { image } = JSON.parse(readFileSync(new URL('./fixture.json', import.meta.url)));
const started = performance.now();
const result = spawnSync('docker', ['run', '--rm', '--network', 'none', '--entrypoint', '/otel-lgtm/prometheus/promtool',
  '--mount', `type=bind,source=${evidence},target=/proof,readonly`, image, 'test', 'rules', '/proof/approval-ratio.yml'],
{ encoding: 'utf8' });
const output = (result.stdout ?? '') + (result.stderr ?? '');
writeFileSync(join(evidence, 'promtool.log'), output);
writeFileSync(join(evidence, 'result.json'), JSON.stringify({ image, cases: cases.length, elapsedSeconds: (performance.now() - started) / 1000,
  exitCode: result.status, error: result.error?.message ?? null, expr }, null, 2) + '\n');
process.stdout.write(output);
assert.ifError(result.error);
assert.equal(result.status, 0, 'Approval ratio must satisfy every real-engine query fixture');
console.log(`Passed ${cases.length} approval-ratio cases.`);
