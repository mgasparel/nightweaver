import { summarizeTools, boundTools, toolCommand } from './shared-read-assertions.mjs';

// Keep scenario load separate from preparation and stack shutdown. This is an
// additive analysis; it never changes the retained runner's completion result.
export function scenarioWindow(browser) {
  const first = browser.samples?.find(row => row.mode === 'cold');
  const last = browser.external?.at(-1);
  const close = browser.changes?.find(row => row.kind === 'blocker-close');
  const origin = Date.parse(browser.clockOriginUtc);
  const start = Date.parse(first?.startedUtc);
  if (typeof browser.clockIdentity !== 'string' || !browser.clockIdentity.trim()
    || !Number.isFinite(origin) || !Number.isFinite(start)
    || !Number.isFinite(last?.settledMs) || !Number.isFinite(close?.readyRenderedMs)
    || toolCommand({ tool: 'bd', args: last?.args ?? [] }) !== 'ready'
    || last.settledMs < close.readyRenderedMs || origin + last.settledMs <= start) {
    return { available: false, reason: 'The cold start, Node clock bridge or final native Ready settlement is unavailable.' };
  }
  return { available: true, startedUtc: new Date(start).toISOString(),
    cutoffUtc: new Date(Math.ceil(origin + last.settledMs) + 1).toISOString(),
    clockIdentity: browser.clockIdentity, cutoffOffsetMs: last.settledMs,
    definition: 'Cold navigation through final native Ready verification after the requested browser changes. Later shutdown starts are outside this window.',
    precision: 'Node UTC clock origin plus its monotonic offset. The cutoff rounds outward by less than two milliseconds, including stored UTC-origin truncation.' };
}

export function windowLoad(browser, records, pending) {
  const window = scenarioWindow(browser);
  if (!window.available) return { window, load: null, bounds: null };
  return { window, load: summarizeTools(records, window.startedUtc, window.cutoffUtc, pending),
    bounds: boundTools(records, pending, window.startedUtc, window.cutoffUtc),
    laterStarts: [...records, ...pending].filter(row => Date.parse(row.startedUtc) >= Date.parse(window.cutoffUtc)).length };
}

export function settlementBoundary(pending, launched, observed) {
  if (!Number.isInteger(pending.pid) || !Number.isInteger(launched?.pid)
    || observed?.schema !== 'nightweaver-tool-observation-v1'
    || observed.wrapper?.pid !== pending.pid || observed.child?.pid !== launched.pid) return null;
  const times = [observed.wrapper.settledByUtc, observed.child.settledByUtc].map(Date.parse);
  const start = Date.parse(pending.startedUtc), cutoff = Date.parse(observed.observedUtc);
  if (!Number.isFinite(start) || !Number.isFinite(cutoff)
    || times.some(value => !Number.isFinite(value) || value < start || value > cutoff)) return null;
  // Date.parse truncates the native timestamp's sub-millisecond fraction.
  // Round outward so this remains an upper bound after JSON correlation.
  return new Date(Math.max(...times) + 1).toISOString();
}
