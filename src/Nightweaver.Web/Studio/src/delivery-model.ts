export type Invocation = { bead_id: string; invocation_id: string; cli: string; configured_model?: string; actual_model?: string; effort: string; status: string; started_at?: string; deadline_at?: string; process_id?: number; exit_code?: number };
export type Slot = { slot_id: string; status: string; bead_id?: string; role?: string; stage?: string; branch?: string; worktree_path?: string; repair_cycles?: number; max_repair_cycles?: number; invocation?: Invocation; diff?: { status: string; added?: number; deleted?: number; binary?: number; untracked?: number } };
export type Pool = { capacity: number; slots: Slot[] };
export type Factory = { availability: string; reason?: string; snapshot?: { runtime_id: string; observed_at: string; runtime_status: string; target_repository: string; planning?: Pool; delivery: Pool; merger: Pool } };
export type Selection = { runtime: string; slot: Slot };
const deliveryStages = ['Implement', 'Review', 'QA', 'Merge'] as const;
/** Position is an observed stage, never a completion percentage or approval history. */
export function deliveryProgress(slot: Slot, state: string) {
  const index = ['stage:implement', 'stage:review', 'stage:qa', 'stage:merge'].indexOf(slot.stage ?? '');
  const retained = state !== 'live' && state !== 'idle';
  const paused = slot.status === 'paused';
  const neutral = retained || paused || slot.status !== 'running' || slot.invocation?.status !== 'running';
  const qualifier = retained ? `Last observed · ${state} · ` : '';
  if (index < 0) return { index, neutral: true, caption: qualifier + 'Stage unavailable', position: undefined };
  const current = deliveryStages[index];
  const caption = paused ? `Paused at ${current}` : neutral ? `Observed at ${current}` : index === 3 ? 'Merge · final stage' : `${current} → ${deliveryStages[index + 1]}`;
  return { index, neutral, caption: qualifier + caption, position: `Stage ${index + 1} of 4` };
}
export function isCurrent(factory: Factory | null | undefined, selected: Selection) {
  const s = factory?.snapshot;
  return s?.runtime_id === selected.runtime && [s.planning, s.delivery, s.merger].some(pool => pool?.slots.some(slot => slot.slot_id === selected.slot.slot_id && slot.invocation?.invocation_id === selected.slot.invocation?.invocation_id));
}
export function runtimeState(factory: Factory | null | undefined, stale: boolean) {
  if (!factory?.snapshot) return 'unavailable';
  if (factory.availability !== 'live') return factory.availability;
  if (stale) return 'stale';
  const s = factory.snapshot;
  return s && [s.planning, s.delivery, s.merger].every(pool => !pool || pool.slots.every(slot => slot.status === 'idle')) ? 'idle' : 'live';
}
export type LogState = { text: string; cursor: number; truncated: boolean; status: string; ended: boolean };
export const emptyLog = (): LogState => ({ text: '', cursor: 0, truncated: false, status: 'Connecting…', ended: false });
export type Stream = { addEventListener(name: string, callback: (event: MessageEvent) => void): void; close(): void };
/** Owns exactly one log stream. Native EventSource retains Last-Event-ID on reconnect. */
export class InvocationController {
  state = emptyLog();
  private source?: Stream;
  private generation = 0;
  constructor(private connect: (url: string) => Stream, private changed: (state: LogState) => void) {}
  open(selection: Selection) {
    this.close();
    this.state = emptyLog(); this.changed(this.state);
    const generation = this.generation;
    const invocation = selection.slot.invocation!;
    const query = new URLSearchParams({ runtime_id: selection.runtime, slot_id: selection.slot.slot_id, invocation_id: invocation.invocation_id });
    const source = this.source = this.connect('/api/telemetry/stream?' + query);
    for (const name of ['status', 'reset', 'log', 'error', 'end']) source.addEventListener(name, event => {
      if (generation !== this.generation || this.state.ended) return;
      let data;
      try { data = event.data ? JSON.parse(event.data) : null; } catch { this.publish({ status: 'A malformed stream event was ignored.' }); return; }
      if (data && ((data.runtimeId && data.runtimeId !== selection.runtime) || (data.slotId && data.slotId !== selection.slot.slot_id) || (data.invocationId && data.invocationId !== invocation.invocation_id))) return;
      if (name === 'reset') this.publish({ text: '', cursor: Number.isSafeInteger(data?.cursor) ? data.cursor : 0, truncated: false });
      if (name === 'status') this.publish({ status: data?.availability === 'live' ? String(data.status) : 'Stale runtime observation · ' + (data?.status ?? 'Unavailable') });
      if (name === 'log' && Number.isSafeInteger(data?.cursor) && data.cursor > this.state.cursor && typeof data.text === 'string') {
        let text = this.state.text + data.text.replace(/\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))/g, '').replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, '');
        let truncated = this.state.truncated;
        const lines = text.split('\n');
        if (lines.length > 2000) { text = lines.slice(-2000).join('\n'); truncated = true; }
        const bytes = new TextEncoder().encode(text);
        if (bytes.length > 256 * 1024) { let start = bytes.length - 256 * 1024; while ((bytes[start] & 0xc0) === 0x80) start++; text = new TextDecoder().decode(bytes.subarray(start)); truncated = true; }
        this.publish({ text, cursor: data.cursor, truncated });
      }
      if (name === 'error') this.publish({ status: data ? 'Log stream unavailable: ' + (data.reason ?? 'unknown') : 'Disconnected · Reconnecting… Agent execution may still be running.' });
      if (name === 'end') { this.publish({ ended: true, status: 'Stream ended: ' + (data?.status ?? data?.reason ?? 'complete') }); source.close(); }
    });
  }
  close() { this.generation++; this.source?.close(); this.source = undefined; }
  private publish(patch: Partial<LogState>) { this.state = { ...this.state, ...patch }; this.changed(this.state); }
}
