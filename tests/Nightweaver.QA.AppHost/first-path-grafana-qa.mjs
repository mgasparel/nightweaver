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
assert(attempts.data.result.some(row => row.metric.role === 'merger' && row.metric.status === 'completed'));
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
assert.equal(imported.dashboard.panels.length, 29);
// This fixture collects at 100ms, unlike the normal 60s exporter. Its datasource
// must not round a subsecond observation down to the previous one-minute step.
const fixtureDatasource = await json(`${grafana}/api/datasources/uid/prometheus`);
await json(`${grafana}/api/datasources/uid/prometheus`, {
  method: 'PUT', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ ...fixtureDatasource, jsonData: { ...fixtureDatasource.jsonData, timeInterval: '1s' } })
});
// Query every imported PromQL target through the real translation backend, including expected no-data.
const panelQueries = [];
for (const panel of dashboard.panels.filter(p => p.datasource.type === 'prometheus')) {
  for (const target of panel.targets) {
    const expr = target.expr.replaceAll('$__rate_interval', '1m').replaceAll('$__range', '1h');
    const result = await metric(expr);
    assert.equal(result.status, 'success', panel.title);
    panelQueries.push({ title: panel.title, expr, result });
  }
}
const names = await json(`${prometheus}/api/v1/label/__name__/values`);
await writeFile(join(output, 'metric-names.json'), JSON.stringify(names, null, 2));
for (const name of ['nightweaver_phase_seconds_total', 'nightweaver_pool_oldest_seconds',
  'nightweaver_observation_age_seconds', 'nightweaver_human_wait_duration_seconds_count',
  'nightweaver_approvals_total', 'nightweaver_telemetry_dropped_total']) assert(names.data.includes(name), name);
const queue = await metric('nightweaver_pool_queued and on(pool,job,instance) (nightweaver_pool_fresh == 0)');
// The backend can retain the earlier queue gauge after the exporter omits it.
// The dashboard's positive freshness gate must exclude that retained gauge.
const gatedQueue = await metric('nightweaver_pool_queued and on(pool,job,instance) (nightweaver_pool_fresh == 1) and on(pool,job,instance) (nightweaver_pool_coverage == 1)');
assert.equal(gatedQueue.data.result.length, 0);
const freshness = await metric('nightweaver_pool_fresh');
assert.equal(freshness.data.result.length, 3);
assert(freshness.data.result.every(row => Number(row.value[1]) === 0));
const browser = await chromium.launch({ headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1100 } });
  const errors = [];
  const datasourceResponses = [];
  const datasourceDetails = [];
  page.on('pageerror', error => errors.push(error.message));
  page.on('response', response => {
    if (response.url().includes('/api/ds/query')) {
      datasourceResponses.push(response.status());
      datasourceDetails.push(response.json().then(result => ({ request: JSON.parse(response.request().postData()), result })));
    }
  });
  const vars = new URLSearchParams({ 'var-bead': signals.beadId, 'var-runtime': signals.runtimeId, 'var-invocation': signals.invocationId, from: 'now-5m', to: 'now' });
  const dashboardUrl = `${grafana}/d/${dashboard.uid}?${vars}`;
  await page.goto(dashboardUrl, { waitUntil: 'networkidle' });
  await page.getByText('Observed invocation duration', { exact: true }).waitFor();
  // Grafana virtualizes panels; scroll the imported detail row into view before inspecting it.
  await page.getByText('Lifecycle events - operational IDs', { exact: true }).scrollIntoViewIfNeeded();
  await page.getByText('Verified merge closure confirmed.', { exact: true }).waitFor({ timeout: 30_000 });
  // Missing complete intervals and never-observed publications must remain no data.
  await page.getByText('Observed eligible queue', { exact: true }).scrollIntoViewIfNeeded();
  await page.waitForTimeout(2000); // Allow Grafana's lazy range panels to render after scroll.
  await page.waitForLoadState('networkidle');
  await page.getByText('planning', { exact: true }).first().waitFor();
  await page.screenshot({ path: join(output, 'overview.png') });
  await page.getByText('Known-history first-pass approvals', { exact: true }).scrollIntoViewIfNeeded();
  await page.waitForTimeout(2000);
  await page.waitForLoadState('networkidle');
  await page.screenshot({ path: join(output, 'rework.png') });
  await writeFile(join(output, 'grafana-query-details.json'), JSON.stringify(await Promise.all(datasourceDetails), null, 2));
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
  assert(datasourceResponses.length > 0);
  assert(datasourceResponses.every(status => status === 200), `Grafana datasource statuses: ${datasourceResponses}`);
  await writeFile(join(output, 'queries.json'), JSON.stringify({ merge, latency, attempts, logs, traces, traceDetails, panelQueries, names, queue, gatedQueue, freshness, datasourceResponses, dashboardUid: dashboard.uid, browserErrors: errors, hostedAcceptance: 'not performed' }, null, 2));
} finally { await browser.close(); }
