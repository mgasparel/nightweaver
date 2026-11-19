import assert from 'node:assert/strict';
import { readFile, writeFile } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { chromium } from 'playwright';

const output = resolve(process.argv[2]);
const signals = JSON.parse(await readFile(join(output, 'signals.json'), 'utf8'));
const envUrl = name => {
  const url = new URL(process.env[name]);
  assert.equal(url.hostname, '127.0.0.1');
  assert.equal(url.protocol, 'http:');
  assert.equal(url.username + url.password, '');
  return url.origin;
};
const grafana = envUrl('NIGHTWEAVER_GRAFANA_URL');
const prometheus = envUrl('NIGHTWEAVER_METRICS_URL');
const loki = envUrl('NIGHTWEAVER_LOKI_URL');
const tempo = envUrl('NIGHTWEAVER_TEMPO_URL');
async function json(url, options) {
  const response = await fetch(url, { ...options, signal: AbortSignal.timeout(10_000) });
  assert(response.ok, `Fixture request status ${response.status}`);
  return response.json();
}
async function eventually(probe) {
  const deadline = Date.now() + 60_000;
  let last;
  while (Date.now() < deadline) {
    try { return await probe(); } catch (error) { last = error; }
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw last;
}
const metric = expr => json(`${prometheus}/api/v1/query?query=${encodeURIComponent(expr)}`);
const merge = await eventually(async () => {
  const result = await metric('nightweaver_verified_merge_total');
  assert.equal(result.data.result.length, 1);
  assert.equal(Number(result.data.result[0].value[1]), 1);
  return result;
});
console.log('Confirmed local verified-merge metric.');
const latency = await metric('nightweaver_invocation_duration_seconds_sum / nightweaver_invocation_duration_seconds_count');
assert(Number(latency.data.result[0].value[1]) > 0);
const attempts = await metric('nightweaver_invocation_attempts_total');
assert.equal(attempts.data.result[0].metric.role, 'merger');
assert.equal(attempts.data.result[0].metric.status, 'completed');
const logQuery = `{service_name="nightweaver-runtime"} | operation="verified_merge" | bead_id="${signals.beadId}" | runtime_id="${signals.runtimeId}" | invocation_id="${signals.invocationId}"`;
const logs = await eventually(async () => {
  const result = await json(`${loki}/loki/api/v1/query_range?query=${encodeURIComponent(logQuery)}&limit=10`);
  assert.equal(result.data.result.flatMap(stream => stream.values).length, 1);
  return result;
});
const traceQuery = `{ resource.service.name = "nightweaver-runtime" && span.bead.id = "${signals.beadId}" && span.runtime.id = "${signals.runtimeId}" && span.invocation.id = "${signals.invocationId}" }`;
const traces = await eventually(async () => {
  const result = await json(`${tempo}/api/search?q=${encodeURIComponent(traceQuery)}`);
  assert.equal(result.traces.length, 2);
  return result;
});
console.log('Confirmed correlated local event and two short traces.');
const traceDetails = await Promise.all(traces.traces.map(trace => json(`${tempo}/api/traces/${trace.traceID}`)));
const dashboard = JSON.parse(await readFile(new URL('../../observability/first-path-dashboard.json', import.meta.url), 'utf8'));
await json(`${grafana}/api/dashboards/db`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ dashboard, overwrite: false }) });
const imported = await json(`${grafana}/api/dashboards/uid/${dashboard.uid}`);
assert.equal(imported.dashboard.panels.length, 5);
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
  const errors = [];
  page.on('pageerror', error => errors.push(error.message));
  const vars = new URLSearchParams({ 'var-bead': signals.beadId, 'var-runtime': signals.runtimeId, 'var-invocation': signals.invocationId });
  const dashboardUrl = `${grafana}/d/${dashboard.uid}?${vars}`;
  await page.goto(dashboardUrl, { waitUntil: 'networkidle' });
  await page.getByText('Observed invocation duration', { exact: true }).waitFor();
  await page.getByText('Verified merge closure confirmed.', { exact: true }).waitFor({ timeout: 30_000 });
  assert.equal(await page.getByText('No data', { exact: true }).count(), 0);
  await page.screenshot({ path: join(output, 'dashboard.png'), fullPage: true });
  // Follow the imported dashboard's actual links; their queries must retain all operational IDs.
  for (const [title, name] of [['Explore correlated traces', 'traces'], ['Explore verified events', 'events']]) {
    // Explore adds history entries while initializing. Return to the exact dashboard
    // instead of assuming one Back operation crosses the entire Explore lifecycle.
    await page.goto(dashboardUrl, { waitUntil: 'networkidle' });
    const link = page.getByRole('link', { name: title, exact: true });
    const href = await link.getAttribute('href');
    const decoded = decodeURIComponent(href);
    for (const id of [signals.beadId, signals.runtimeId, signals.invocationId]) assert(decoded.includes(id));
    await link.click();
    await page.waitForURL('**/explore?**');
    await page.getByText(name === 'traces' ? 'verified_merge' : 'Verified merge closure confirmed.', { exact: true }).first().waitFor({ timeout: 30_000 });
    await page.screenshot({ path: join(output, `explore-${name}.png`), fullPage: true });
    console.log(`Verified imported dashboard Explore link: ${name}.`);
  }
  assert.deepEqual(errors, []);
  await writeFile(join(output, 'queries.json'), JSON.stringify({ merge, latency, attempts, logs, traces, traceDetails, dashboardUid: dashboard.uid, browserErrors: errors, hostedAcceptance: 'not performed' }, null, 2));
} finally { await browser.close(); }
