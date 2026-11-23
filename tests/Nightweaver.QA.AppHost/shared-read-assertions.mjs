import assert from 'node:assert/strict';

// Reject absent, failed, partial and shell-only samples. A missing field is not 0.
export function assertSample(sample) {
  assert.equal(sample.error, null, 'Trial failed');
  for (const key of ['usefulMs', 'detailMs']) {
    assert(Number.isFinite(sample[key]) && sample[key] >= 0, `Missing ${key}`);
  }
  assert(sample.httpMs === null && sample.retainedReceipt === true
    || sample.httpMs === null && typeof sample.httpUnavailableReason === 'string' && sample.httpUnavailableReason.length > 0
    || Number.isFinite(sample.httpMs) && sample.httpMs >= 0, 'Missing httpMs');
  assert(sample.detailMs >= sample.usefulMs);
  assert(sample.proof?.rendered?.length > 0, 'No rendered facts');
  assert(sample.proof?.resources?.length > 0, 'No source evidence');
  assert(sample.proof.resources.every(value => value.epoch && value.revision && value.sources
    && Object.keys(value.sources).length > 0 && value.dataPresent
    && (value.health === 'fresh' || value.required?.length > 0
      && value.required.every(key => value.sources[key]?.health === 'fresh'))), 'Source validation incomplete');
  assert.equal(sample.proof.expected, true, 'Fixture facts differ');
}

export function summarizeTools(records, startUtc, endUtc, pending = []) {
  const start = Date.parse(startUtc), end = Date.parse(endUtc);
  assert(Number.isFinite(start) && Number.isFinite(end) && end >= start);
  if (end === start) return { count: null, commandsPerSecond: null, families: null,
    bytes: null, nativeInclusiveMs: null, errors: null, peakConcurrency: null,
    unavailableReason: 'Interval is below UTC correlation resolution.', records: [] };
  // Include all commands that overlap the interval, including an in-flight
  // command admitted before navigation. Never imply exact monotonic correlation.
  const selected = records.filter(row => Date.parse(row.startedUtc) < end
    && Date.parse(row.startedUtc) + row.elapsedMs >= start);
  const families = {};
  const events = [];
  for (const row of selected) {
    assert(Number.isFinite(row.elapsedMs) && row.elapsedMs >= 0);
    const command = row.args.filter(arg => !arg.startsWith('--'))[0] ?? 'unknown';
    const family = `${row.tool}:${command}`;
    families[family] = (families[family] ?? 0) + 1;
    events.push([Date.parse(row.startedUtc), 1], [Date.parse(row.startedUtc) + row.elapsedMs, -1]);
  }
  let live = 0, peakConcurrency = 0;
  for (const [, delta] of events.sort((a, b) => a[0] - b[0] || a[1] - b[1])) {
    live += delta; peakConcurrency = Math.max(peakConcurrency, live);
  }
  const measured = { count: selected.length, commandsPerSecond: selected.length / ((end - start) / 1000), families,
    bytes: selected.reduce((sum, row) => sum + row.outputBytes + row.errorBytes, 0),
    nativeInclusiveMs: selected.reduce((sum, row) => sum + row.elapsedMs, 0),
    errors: selected.filter(row => row.exitCode !== 0).length, peakConcurrency,
    gateWaitMs: null, admissionWaitMs: null, projectionMs: null,
    unavailableReason: 'PATH shim sees tool execution after admission and gate wait. UTC interval correlation has millisecond precision. Native time includes shim forwarding.',
    records: selected };
  // A missing final record proves a start, but not its end, bytes or exit.
  // It may overlap any later interval. Never report settled-only sums as totals.
  const uncertain = pending.filter(row => Date.parse(row.startedUtc) < end);
  if (!uncertain.length) return measured;
  const started = [...records, ...pending].filter(row => Date.parse(row.startedUtc) >= start
    && Date.parse(row.startedUtc) < end);
  const startedFamilies = {};
  for (const row of started) {
    const family = `${row.tool}:${row.args.filter(arg => !arg.startsWith('--'))[0] ?? 'unknown'}`;
    startedFamilies[family] = (startedFamilies[family] ?? 0) + 1;
  }
  return { count: null, commandsPerSecond: null, families: null, bytes: null,
    nativeInclusiveMs: null, errors: null, peakConcurrency: null,
    gateWaitMs: null, admissionWaitMs: null, projectionMs: null,
    startedCount: started.length, startedFamilies, settledOnly: measured,
    unsettledRecords: uncertain,
    unavailableReason: 'Command starts lack final records. Overlap, native duration, bytes, exits and peak concurrency are incomplete. startedCount counts recorded tool starts inside this UTC interval only.' };
}
