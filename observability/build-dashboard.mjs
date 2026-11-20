// Regenerate the versioned dashboard and budget after changing a closed signal contract.
import { readFileSync, writeFileSync } from 'node:fs';
const path = new URL('./first-path-dashboard.json', import.meta.url);
const dashboard = JSON.parse(readFileSync(path));
dashboard.title = 'Nightweaver - delivery bottlenecks';
dashboard.version = 2;
dashboard.description = 'Observed slot-seconds are disjoint per reservation, additive across parallel slots, never wall-clock lead time. Unknown is not zero. Metrics unsampled; details sampled/dropped. Free retention: 14 days. Hosted acceptance pending. See docs/design/grafana-bottlenecks.md.';
dashboard.panels = dashboard.panels.filter(p => p.id <= 5);
const [duration, merges, cleanup, events, traces] = dashboard.panels;
const rate = n => `rate(${n}[$__rate_interval])`;
const increase = n => `increase(${n}[$__range])`;
duration.targets[0].expr = `sum by(role) (${rate('nightweaver_invocation_duration_seconds_sum')}) / sum by(role) (${rate('nightweaver_invocation_duration_seconds_count')})`;
duration.description = 'Mean seconds by role, rate(sum)/rate(count). Resets handled per series before aggregation; no complete samples means unknown.';
merges.targets[0].expr = `sum(${increase('nightweaver_verified_merge_total')})`;
cleanup.targets[0].expr = `sum by(outcome) (${increase('nightweaver_merge_cleanup_total')})`;
merges.description = 'Window increase of independently verified guarded closures. Extrapolated across scrapes, reset aware; export loss is possible. Cleanup never subtracts success.';
cleanup.description = 'Window increase by outcome, reset aware. Queued is not completed; housekeeping is independent of closure.';
events.gridPos.y = 75;
traces.gridPos.y = 84;
events.title = 'Lifecycle events - operational IDs';
events.targets[0].expr = events.targets[0].expr.replace(' | operation="verified_merge"', '');
function add(title, expr, unit, description, legend = '{{pool}}') {
  const index = dashboard.panels.length - 5;
  dashboard.panels.push({ id: index + 6, type: 'timeseries', title, description,
    gridPos: { x: index % 2 * 12, y: 5 + Math.floor(index / 2) * 7, w: 12, h: 7 },
    datasource: { type: 'prometheus', uid: '${DS_METRICS}' },
    targets: [{ refId: 'A', expr, range: true, instant: false, legendFormat: legend }],
    fieldConfig: { defaults: { unit, noValue: 'Unknown / no samples', custom: { drawStyle: 'line', lineWidth: 2, showPoints: 'auto' } }, overrides: [] },
    options: { legend: { displayMode: 'table', placement: 'bottom' }, tooltip: { mode: 'multi' } } });
}
const fresh = '(nightweaver_pool_fresh == 1) and on(pool,job,instance) (nightweaver_pool_coverage == 1)';
add('Occupied time allocation', `sum by(pool,phase) (${rate('nightweaver_phase_seconds_total')})`, 'short', 'Completed reservation slot-seconds per second. Seven disjoint phases include coarse preparation/finalization; active work appears on release. Parallel slot-seconds are not wall time.', '{{pool}} / {{phase}}');
add('Live occupied / configured capacity', 'sum by(pool) (nightweaver_pool_occupied) / sum by(pool) (nightweaver_pool_capacity)', 'percentunit', 'Live owned reservations / configured slots, including preparation and settlement. Missing exporter is unknown.');
add('Observed eligible queue', `nightweaver_pool_queued and on(pool,job,instance) (${fresh})`, 'short', 'Fresh complete observed eligibility only. Excludes blocked, paused, reserved and backpressured work. Absence is unknown, never zero.');
add('Oldest observed eligible wait', `nightweaver_pool_oldest_seconds and on(pool,job,instance) (${fresh})`, 's', 'Age from first observed eligibility; 30-second discovery quantization plus read latency. Not total backlog age. Fresh and complete only.');
add('Queue freshness and coverage', '{__name__=~"nightweaver_pool_(fresh|coverage|backpressure)"}', 'short', 'Fresh and coverage must both be 1 for queue panels. Backpressure is the separate Planning gate. Missing stream is unknown.', '{{pool}} / {{__name__}}');
add('Queue observation age', 'nightweaver_pool_age_seconds', 's', 'Seconds since successful observation; >60 seconds invalidates waiting coverage. Initial unavailable has no age.');
add('Completed observed eligible waits', `sum by(pool) (${rate('nightweaver_eligible_wait_duration_seconds_sum')}) / sum by(pool) (${rate('nightweaver_eligible_wait_duration_seconds_count')})`, 's', 'Mean complete continuously observed intervals to reservation. Left-censored startup/restart and failed reads are excluded.');
add('Lost waiting coverage', `sum by(pool,reason) (${increase('nightweaver_eligible_wait_censored_total')})`, 'short', 'Diagnostic censor observations, not unique Beads. Capacity refusals may repeat counts.', '{{pool}} / {{reason}}');
add('Invocation outcomes', `sum by(role,status) (${increase('nightweaver_invocation_attempts_total')})`, 'short', 'Unsampled settled attempts. Timeout/retry does not imply semantic repair or approval.', '{{role}} / {{status}}');
add('Classifier and verification diagnostics', `sum by(kind,outcome) (${increase('nightweaver_workflow_results_total')})`, 'short', 'Diagnostic results include classification, verification and publication. Classifier success is not work approval; diagnostic durations overlap phases.', '{{kind}} / {{outcome}}');
add('Operational retries', `sum by(reason) (${increase('nightweaver_operational_retries_total')})`, 'short', 'Operational retry observations, distinct from semantic repairs. Reset aware window increase.', '{{reason}}');
add('Semantic repairs', `sum by(role) (${increase('nightweaver_semantic_repairs_total')})`, 'short', 'Confirmed ChangesRequired publications only; not classifier diagnostics or operational retries.', '{{role}}');
add('Known-history first-pass approvals', `sum by(role) (${increase('nightweaver_approvals_total{coverage="FirstPass"}')}) / sum by(role) (${increase('nightweaver_approvals_total{coverage=~"FirstPass|LaterPass"}')})`, 'percentunit', 'FirstPass / (FirstPass + LaterPass) approvals. Unknown excluded and shown alongside. Repeated attempts also make LaterPass.', '{{role}}');
add('Approval history coverage', `sum by(role,coverage) (${increase('nightweaver_approvals_total')})`, 'short', 'Always inspect Unknown with first-pass ratio. Missing boundaries, restart and visibility gaps prevent known history.', '{{role}} / {{coverage}}');
const lifecycleGate = 'and on(job,instance) (nightweaver_lifecycle_fresh == 1) and on(job,instance) (nightweaver_lifecycle_coverage == 1)';
add('Observed human pause age', `nightweaver_lifecycle_oldest_seconds ${lifecycleGate}`, 's', 'Oldest same-runtime pause age by label set, including left-censored intervals. No restart-spanning lead time.', '{{pause}}');
add('Completed human wait', `sum by(pause) (${rate('nightweaver_human_wait_duration_seconds_sum')}) / sum by(pause) (${rate('nightweaver_human_wait_duration_seconds_count')})`, 's', 'Mean complete observed unpaused-paused-unpaused segments only. Notes, abort and close cannot complete waits. External removal is not Resume authority.', '{{pause}}');
add('Web source age', 'nightweaver_observation_age_seconds', 's', 'Retained maximum age by source/health. Initial unavailable age absent; inspect health counts alongside. Connection health is not source freshness.', '{{source}} / {{health}}');
add('Web source health', 'nightweaver_observation_health', 'short', 'Cached resource counts by source/health, not subscribers. Healthy/stale/unavailable/pending/idle/stopped remain distinct.', '{{source}} / {{health}}');
add('Exporter drops and failures', '{__name__=~"nightweaver_telemetry_(dropped|export_failed)_total"}', 'short', 'Cumulative bounded loss; restart resets. Missing data cannot prove no loss. Inspect local fixed diagnostics when tenant is unreachable.', '{{__name__}} / {{signal}} {{reason}}');
add('Lifecycle freshness and coverage', '{__name__=~"nightweaver_lifecycle_(fresh|coverage)"}', 'short', 'Both gates must be 1 for pause counts and ages. Missing is unknown. No accounting ledger.', '{{__name__}}');
add('Observed paused inventory', `nightweaver_lifecycle_paused ${lifecycleGate}`, 'short', 'Fresh complete observed count, grouped by pause label set. Not a command or readiness authority.', '{{pause}}');
add('Classifier duration', `sum by(kind) (${rate('nightweaver_classification_duration_seconds_sum')}) / sum by(kind) (${rate('nightweaver_classification_duration_seconds_count')})`, 's', 'Mean primary/fallback/duplicate-failure seconds. Already included in classification phase; never add again.', '{{kind}}');
add('Confirmed lifecycle publications', `sum by(role,outcome) (${increase('nightweaver_published_outcomes_total')})`, 'short', 'Confirmed Planning/Delivery publications, distinct from diagnostic classifier results. Graph publication is a milestone, not a second Planner approval.', '{{role}} / {{outcome}}');
add('Lost lifecycle coverage', `sum by(reason) (${increase('nightweaver_lifecycle_censored_total')})`, 'short', 'Lost or partial observation intervals. Restart, capacity, close and changed labels can censor history; these are not unique Beads.', '{{reason}}');
events.gridPos.y = 89;
traces.gridPos.y = 98;
writeFileSync(path, JSON.stringify(dashboard, null, 2) + '\n');

const source = readFileSync(new URL('../src/Nightweaver/Telemetry/OperationalTelemetry.cs', import.meta.url), 'utf8');
const views = source.slice(source.indexOf('return instrument.Name switch'), source.indexOf('_ => MetricStreamConfiguration.Drop'));
const instruments = [];
for (const match of views.matchAll(/((?:"nightweaver\.[^"]+"\s*(?:or\s*)?)+)=> new (MetricStreamConfiguration|ExplicitBucketHistogramConfiguration)\s*\{([^}]+)\}/gs)) {
  for (const name of match[1].matchAll(/"(nightweaver\.[^"]+)"/g)) {
    const points = Number(match[3].match(/CardinalityLimit = (\d+)/)[1]);
    const series = match[2].startsWith('Explicit') ? 11 : 1;
    instruments.push({ instrument: name[1], sdkPointsIncludingOverflow: points, translatedSeriesPerPoint: series,
      labels: [...match[3].matchAll(/"([a-z]+)"/g)].map(m => m[1]), seriesPerService: points * series });
  }
}
const total = 2 * (instruments.reduce((sum, item) => sum + item.seriesPerService, 0) + 1);
if (instruments.length !== 38 || total > 2000) throw new Error(`Unexpected closed inventory: ${instruments.length} instruments, ${total} series`);
writeFileSync(new URL('./series-budget.json', import.meta.url), JSON.stringify({ schema: 'nightweaver-series-budget-v1',
  services: ['nightweaver-runtime', 'nightweaver-web'], resourceLabels: ['service.name'], targetInfoPerService: 1,
  histogramBounds: [0.1, 1, 5, 30, 120, 600, 1800, 7200], histogramSeries: '9 buckets including +Inf, sum and count', instruments, total, limit: 2000 }, null, 2) + '\n');
console.log(`${instruments.length} instruments; conservative translated series: ${total}`);
