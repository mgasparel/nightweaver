// Match the selected surface and the navigation interval, not background traffic.
export function resourceKey(url) {
  const parsed = new URL(url);
  return /^\/api\/operations\/v1\/projects\/qa\/resources$/.test(parsed.pathname)
    ? parsed.searchParams.get('key') : null;
}

export function legacyReceipts(browser) {
  // Old traces lack request identity. Only one preceding request for a key can
  // establish a match. Once ordering is ambiguous, do not guess FIFO ordering.
  const pending = new Map(), ambiguous = new Set(), receipts = [];
  const events = [
    ...(browser.requests ?? []).filter(row => row.method === 'GET')
      .map(row => ({ ...row, key: resourceKey(row.url), request: true })),
    ...(browser.resources ?? []).map(row => ({ ...row, request: false }))
  ].sort((a, b) => a.atMs - b.atMs || Number(b.request) - Number(a.request));
  for (const row of events) {
    if (!row.key) continue;
    const queue = pending.get(row.key) ?? [];
    if (row.request) {
      queue.push(row.atMs); pending.set(row.key, queue);
    } else {
      if (queue.length !== 1) ambiguous.add(row.key);
      const requestAtMs = !ambiguous.has(row.key) ? queue[0] : null;
      receipts.push({ key: row.key, atMs: row.atMs, requestAtMs, status: row.status });
      queue.length = 0;
    }
  }
  return receipts;
}

export function sampleReceipt(sample, receipts, endMs) {
  const keys = new Set(sample.proof?.resources?.map(row => row.key).filter(Boolean));
  const matching = receipts.filter(row => keys.has(row.key)
    && Number.isFinite(row.requestAtMs) && row.requestAtMs >= sample.startMs
    && row.atMs >= row.requestAtMs && row.atMs <= endMs).sort((a, b) => a.atMs - b.atMs);
  if (matching.length) return { httpMs: matching[0].atMs - sample.startMs,
    retainedReceipt: false, httpReceipt: matching[0], httpUnavailableReason: null };
  const retained = sample.mode !== 'cold' && keys.size > 0
    && sample.proof.resources.every(row => Number.isFinite(row.atMs) && row.atMs < sample.startMs)
    && !receipts.some(row => keys.has(row.key) && row.atMs >= sample.startMs && row.atMs <= endMs);
  return { httpMs: null, retainedReceipt: retained, httpReceipt: null,
    httpUnavailableReason: retained ? null : 'No resource receipt has a confirmed request in this sample interval.' };
}

export function analyzeReceipt(sample, browser) {
  const endMs = sample.endMs ?? (Number.isFinite(sample.detailMs) ? sample.startMs + sample.detailMs : NaN);
  return { ...sample, ...sampleReceipt(sample, browser.receipts ?? legacyReceipts(browser), endMs),
    receiptAttribution: browser.receipts ? 'request-identity' : 'legacy-unique-request' };
}
