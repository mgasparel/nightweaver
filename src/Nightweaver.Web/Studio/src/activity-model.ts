export const ROW_LIMIT = 500;
export const EXPANSION_LIMIT = 20;
export type ActivityRow = {
  schema: number; position: { journal: string; offset: number; runtime: string; sequence: number };
  kind: string; phase: string; strength: string; message: string; bead?: string; invocation?: string;
  observedAt: string; sourceTime?: string; freshness: string; source: string; sourceReference: string;
  reason: string; count?: number; limit?: number; role?: string; merge?: { pullRequest: string };
};
export type Detail = { id: string; availability: string; workspace?: string; invocation?: string; pullRequest?: string };
export type ActivityStatus = { oldest?: number; newest?: number; loadedThrough?: number; loading: boolean;
  state: string; journalHealth: string; runtimeHealth: string; runtimeId?: string;
  observedAt?: string; attemptedAt?: string; runtimeObservedAt?: string; runtimeReplaced: boolean;
  reconnectGap?: boolean; details: Detail[] };
export type Page = ActivityStatus & { records: ActivityRow[]; cursor?: string };
export const rowId = (row: ActivityRow) => `1.${row.position.journal.replaceAll('-', '')}.${row.position.offset}`;
export function cursorParts(cursor?: string) {
  const match = /^1\.([a-f0-9]{32})\.(0|[1-9][0-9]*)$/.exec(cursor ?? '');
  return match ? { journal: match[1], offset: BigInt(match[2]) } : undefined;
}
export function atOffset(cursor: string, offset: number) {
  return `1.${cursorParts(cursor)!.journal}.${Math.max(0, offset)}`;
}
export const safeBead = (bead: string) => bead.length <= 128 && /^[A-Za-z0-9][A-Za-z0-9._-]*$/.test(bead);
export function selection(search: string) {
  const query = new URLSearchParams(search);
  const level = query.get('level') ?? '';
  const bead = query.get('bead') ?? '';
  return { level: ['', 'work', 'warning', 'error'].includes(level) ? level : '', bead: safeBead(bead) ? bead : '' };
}
export function levelOf(row: ActivityRow) {
  if (['RecoveryFailed', 'CleanupFailed'].includes(row.kind) || row.kind === 'HealthChanged' && ['RuntimeFailed', 'Unhealthy'].includes(row.reason)) return 'error';
  return ['ChangesRequired', 'PauseObserved', 'EvidenceUnavailable', 'RecordsLost', 'RuntimeReplaced', 'HistoryGap'].includes(row.kind) ? 'warning' : 'work';
}
export function mergeRows(current: ActivityRow[], incoming: ActivityRow[], older = false) {
  const rows = [...new Map([...current, ...incoming].map(row => [rowId(row), row])).values()];
  // Journal order is independent of source and observation clocks. Never mix journals.
  const journal = current[0]?.position.journal ?? incoming[0]?.position.journal;
  const ordered = rows.filter(row => row.position.journal === journal).sort((a, b) => a.position.offset - b.position.offset);
  return older ? ordered.slice(0, ROW_LIMIT) : ordered.slice(-ROW_LIMIT);
}
export class ActivityBuffer {
  rows: ActivityRow[] = [];
  details = new Map<string, Detail>();
  expanded = new Set<string>();
  cursor?: string;
  received?: string;
  pending = 0;
  paused = false;
  gap = false;
  replaced = false;
  accept(row: ActivityRow, detail?: Detail) {
    const id = rowId(row), next = cursorParts(id)!, prior = cursorParts(this.received);
    if (prior?.journal === next.journal && prior.offset >= next.offset) return;
    this.received = id;
    if (this.paused) { this.pending = Math.min(ROW_LIMIT + 1, this.pending + 1); return; }
    if (prior && prior.journal !== next.journal) { this.rows = []; this.details.clear(); this.expanded.clear(); this.gap = true; }
    this.rows = mergeRows(this.rows, [row]);
    if (this.rows.length === ROW_LIMIT) this.gap = true;
    this.cursor = id;
    if (detail) this.details.set(id, detail);
    this.trim();
  }
  page(page: Page, older = false) {
    this.status(page);
    if (!older && this.rows[0] && page.records[0] && this.rows[0].position.journal !== page.records[0].position.journal) {
      this.rows = []; this.details.clear(); this.expanded.clear(); this.gap = true;
    }
    const before = this.rows.length;
    this.rows = mergeRows(this.rows, page.records, older);
    if (!older && before + page.records.length > ROW_LIMIT) this.gap = true;
    for (const detail of page.details) this.details.set(detail.id, detail);
    if (!older && page.cursor) this.cursor = this.received = page.cursor;
    this.trim();
  }
  status(status: ActivityStatus) {
    if (/Gap|Expired|Truncated|Invalid/.test(status.state) || status.reconnectGap) this.gap = true;
    if (status.runtimeReplaced) this.replaced = true;
  }
  toggle(id: string) {
    this.paused = true;
    if (this.expanded.has(id)) this.expanded.delete(id);
    else if (this.expanded.size < EXPANSION_LIMIT) this.expanded.add(id);
  }
  trim() {
    const ids = new Set(this.rows.map(rowId));
    for (const id of this.details.keys()) if (!ids.has(id)) this.details.delete(id);
    for (const id of this.expanded) if (!ids.has(id)) this.expanded.delete(id);
  }
}
// Only server-approved detail destinations can become links. Never use record prose as HTML or a URL.
export function safeDetail(detail: Detail | undefined, project: string, row: ActivityRow) {
  const base = `/projects/${encodeURIComponent(project)}/beads/${encodeURIComponent(row.bead ?? '')}`;
  const workspace = row.bead && [base, `${base}?tab=history`].includes(detail?.workspace ?? '') ? detail!.workspace : undefined;
  let pullRequest: string | undefined;
  if (row.kind === 'MergeVerified' && row.strength === 'Verified' && detail?.pullRequest === row.merge?.pullRequest && detail?.pullRequest && detail.pullRequest.length <= 512 && /^https:\/\/[A-Za-z0-9.-]+\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/pull\/[1-9][0-9]*$/.test(detail.pullRequest)) pullRequest = detail.pullRequest;
  let invocation: string | undefined;
  if (detail?.invocation) {
    let url: URL;
    try { url = new URL(detail.invocation, 'https://local.invalid'); } catch { return { workspace, pullRequest, invocation }; }
    if (url.origin === 'https://local.invalid' && url.pathname === '/api/telemetry/stream' &&
      url.searchParams.get('runtime_id')?.replaceAll('-', '').toLowerCase() === row.position.runtime.replaceAll('-', '').toLowerCase() && url.searchParams.get('invocation_id') === row.invocation &&
      /^[a-z]+-[0-9]+$/.test(url.searchParams.get('slot_id') ?? '')) invocation = url.pathname + url.search;
  }
  return { workspace, pullRequest, invocation };
}
export function observationDelay(row: ActivityRow) {
  if (!row.sourceTime) return 'Source time unavailable';
  const seconds = (Date.parse(row.observedAt) - Date.parse(row.sourceTime)) / 1000;
  return Number.isFinite(seconds) && seconds >= 0 ? `Observed ${Math.round(seconds)}s after source` : 'Clock order uncertain';
}
