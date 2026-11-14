export type Identity = { projectId: string; key: string };
export type ObservationError = { code: string; message: string; retryable: boolean };
export type SourceState = { health: "fresh" | "stale" | "unavailable"; observedAt: string | null;
  lastAttemptAt: string | null; error: ObservationError | null };
export type Envelope<T = unknown> = Identity & {
  schema: "nightweaver-observation-v1"; epoch: string; revision: string;
  health: "fresh" | "stale" | "unavailable"; data: T | null;
  observedAt: string | null; lastAttemptAt: string | null;
  sources: Record<string, SourceState>; error: ObservationError | null; targetIntervalMs: number; nextAttemptAt: string | null;
};
export type Snapshot<T = unknown> = { observation?: Envelope<T>; connectionState: "connecting" | "connected" | "disconnected";
  refreshing: boolean; stale: boolean; error?: string };
type Frame = { schema: string; projectId: string; epoch: string; versions?: Record<string, string>; heartbeatMs?: number };
export type Transport = {
  connect(project: string, keys: string[], event: (name: string, data: Frame) => void, failed: () => void): () => void;
  read(identity: Identity, signal: AbortSignal): Promise<Envelope>;
  refresh(project: string, keys: string[]): Promise<void>;
};
export type Scheduler = { now(): number; later(callback: () => void, ms: number): () => void; random(): number };
type Entry = { identity: Identity; listeners: Set<() => void>; snapshot: Snapshot; watermark: bigint;
  wanted: bigint; pending: boolean; request?: AbortController; bytes: number; touched: number; refreshAttempt?: string | null };
const id = (identity: Identity) => JSON.stringify([identity.projectId, identity.key]);
const revision = (value: string) => { if (typeof value !== "string" || !/^[1-9][0-9]*$/.test(value)) throw Error("Invalid observation revision"); return BigInt(value); };
const empty = (): Snapshot => ({ connectionState: "disconnected", refreshing: false, stale: true });

/** One owner per tab. Network, timers and lifecycle are adapters, never React state. */
export class QueryCoordinator {
  private entries = new Map<string, Entry>();
  private epoch?: string;
  private generation = 0;
  private close?: () => void;
  private scheduled?: () => void;
  private retry?: () => void;
  private tick?: () => void;
  private retries = 0;
  private lastFrame = 0;
  private lastPoll = 0;
  private visible = true;
  private disposed = false;
  private hello = false;
  private signature = "";
  private buffered = new Map<string, { epoch: string; revision: bigint }>();
  private refreshes = new Map<string, Promise<void>>();
  constructor(private transport: Transport, private clock: Scheduler) { this.timer(); }

  subscribe(identity: Identity, listener: () => void): () => void {
    if (this.disposed) throw Error("Coordinator disposed");
    const active = this.active();
    if (active.some(entry => entry.identity.projectId !== identity.projectId)) throw Error("Only one current project is supported");
    let entry = this.entries.get(id(identity));
    if (!entry?.listeners.size && active.length >= 32) throw Error("Too many active observations");
    if (!entry) {
      this.evict();
      if (this.entries.size >= 64) throw Error("Observation cache is full; close an unused view");
      entry = { identity: { ...identity }, listeners: new Set(), snapshot: empty(), watermark: 0n, wanted: 0n,
        pending: false, bytes: 0, touched: this.clock.now() };
      this.entries.set(id(identity), entry);
    }
    entry.listeners.add(listener);
    entry.touched = this.clock.now();
    this.schedule();
    let subscribed = true;
    return () => {
      if (!subscribed) return;
      subscribed = false;
      entry.listeners.delete(listener); entry.touched = this.clock.now();
      if (!entry.listeners.size) { entry.request?.abort(); entry.request = undefined; }
      this.schedule();
    };
  }

  getSnapshot<T = unknown>(identity: Identity): Snapshot<T> { return (this.entries.get(id(identity))?.snapshot ?? missing) as Snapshot<T>; }

  async refresh(identities: Identity[]): Promise<void> {
    const selected = [...new Map(identities.map(identity => [id(identity), identity])).values()];
    if (!selected.length) return;
    const project = selected[0].projectId;
    if (selected.length > 32 || selected.some(identity => identity.projectId !== project)) throw Error("Invalid refresh scope");
    const key = JSON.stringify([project, selected.map(identity => identity.key).sort()]);
    const existing = this.refreshes.get(key);
    if (existing) return existing;
    for (const identity of selected) {
      const entry = this.entries.get(id(identity));
      if (entry) {
        entry.refreshAttempt = entry.snapshot.observation?.lastAttemptAt ?? null;
        this.publish(entry, { refreshing: true, error: undefined });
      }
    }
    const operation = this.transport.refresh(project, selected.map(identity => identity.key)).then(() => {
      for (const identity of selected) { const entry = this.entries.get(id(identity)); if (entry) this.queue(entry); }
    }).catch(error => {
      for (const identity of selected) { const entry = this.entries.get(id(identity)); if (entry) this.publish(entry, { refreshing: false, error: String(error) }); }
      throw error;
    }).finally(() => this.refreshes.delete(key));
    this.refreshes.set(key, operation);
    return operation;
  }

  invalidateAfterCommand(kind: "tracker" | "checkpoint" | "cleanup"): Promise<void> {
    const keys = this.active().filter(({ identity }) => kind === "tracker" ? /^(attention|explorer:|digest:|workspace:)/.test(identity.key)
      : kind === "checkpoint" ? identity.key.startsWith("digest:") : /^(cleanup:|factory$)/.test(identity.key));
    return this.refresh(keys.map(entry => entry.identity));
  }

  setVisible(visible: boolean) { this.visible = visible; if (visible) this.wake(); }
  suspend() { this.retry?.(); this.retry = undefined; this.disconnect(); }
  wake() { if (this.disposed) return; if (!this.hello) this.restart(); else this.resync(); }
  dispose() {
    this.disposed = true; this.scheduled?.(); this.retry?.(); this.tick?.(); this.disconnect();
    this.entries.clear();
  }
  private active() { return [...this.entries.values()].filter(entry => entry.listeners.size); }
  private publish(entry: Entry, patch: Partial<Snapshot>) {
    entry.snapshot = { ...entry.snapshot, ...patch };
    for (const listener of entry.listeners) listener();
  }
  private evict(requiredBytes = 0, except?: Entry) {
    const unused = [...this.entries.values()].filter(entry => !entry.listeners.size && entry !== except).sort((a, b) => a.touched - b.touched);
    for (const entry of unused) {
      const bytes = [...this.entries.values()].reduce((sum, item) => sum + item.bytes, 0);
      if (this.entries.size < 64 && bytes + requiredBytes <= 16 * 1024 * 1024 && this.clock.now() - entry.touched < 30000) break;
      entry.request?.abort(); this.entries.delete(id(entry.identity));
    }
  }
  private schedule() {
    if (this.scheduled || this.disposed) return;
    this.scheduled = this.clock.later(() => {
      this.scheduled = undefined;
      const signature = JSON.stringify(this.active().map(entry => id(entry.identity)).sort());
      if (signature !== this.signature) { this.signature = signature; this.restart(); }
      for (const entry of this.active()) if (entry.pending) this.read(entry);
    }, 0);
  }
  private disconnect() {
    ++this.generation; this.hello = false; this.close?.(); this.close = undefined;
    this.buffered.clear();
    for (const entry of this.entries.values()) {
      entry.request?.abort(); entry.request = undefined;
      this.publish(entry, { connectionState: "disconnected", stale: true });
    }
  }
  private restart() {
    this.retry?.(); this.retry = undefined; this.disconnect();
    const active = this.active();
    if (!active.length || this.disposed) return;
    const generation = this.generation;
    this.lastFrame = this.clock.now();
    for (const entry of active) this.publish(entry, { connectionState: "connecting" });
    this.close = this.transport.connect(active[0].identity.projectId, active.map(entry => entry.identity.key),
      (name, frame) => {
        if (generation !== this.generation) return;
        try { this.event(name, frame); } catch { this.failed(); }
      }, () => { if (generation === this.generation) this.failed(); });
  }
  private failed() {
    this.disconnect(); this.retry?.();
    const delay = Math.max(1000, Math.min(15000, 1000 * 2 ** Math.min(4, this.retries++)) * (0.75 + this.clock.random() * 0.25));
    this.retry = this.clock.later(() => { this.retry = undefined; this.restart(); }, delay);
  }
  private event(name: string, frame: Frame) {
    if (frame.schema !== "nightweaver-operations-v1" || frame.projectId !== this.active()[0]?.identity.projectId || !frame.epoch) throw Error("Invalid stream identity");
    this.lastFrame = this.clock.now();
    if (name === "hello") {
      const active = this.active();
      if (!frame.versions || Object.keys(frame.versions).length !== active.length || active.some(entry => !frame.versions?.[entry.identity.key])) throw Error("Incomplete handshake");
      if (this.epoch !== frame.epoch) {
        this.epoch = frame.epoch;
        for (const entry of this.entries.values()) { entry.watermark = 0n; entry.wanted = 0n; }
      }
      this.hello = true; this.retries = 0;
      for (const entry of active) {
        const buffered = this.buffered.get(entry.identity.key);
        entry.wanted = max(max(entry.watermark, revision(frame.versions[entry.identity.key])), buffered?.epoch === frame.epoch ? buffered.revision : 0n);
        this.publish(entry, { connectionState: "connected", stale: true });
      }
      this.buffered.clear(); this.resync(); return;
    }
    if (!this.hello) {
      if (name === "invalidation") for (const entry of this.active()) {
        const value = frame.versions?.[entry.identity.key];
        if (value) {
          const previous = this.buffered.get(entry.identity.key);
          this.buffered.set(entry.identity.key, { epoch: frame.epoch,
            revision: max(revision(value), previous?.epoch === frame.epoch ? previous.revision : 0n) });
        }
      }
      return;
    }
    if (frame.epoch !== this.epoch) { this.restart(); return; }
    if (name === "resync") this.resync();
    if (name === "invalidation") {
      for (const entry of this.active()) {
        const value = frame.versions?.[entry.identity.key];
        if (value && revision(value) > max(entry.wanted, entry.watermark)) {
          entry.wanted = revision(value); this.publish(entry, { stale: true }); this.queue(entry);
        }
      }
    }
  }
  private resync() { for (const entry of this.active()) this.queue(entry); }
  private queue(entry: Entry) { entry.pending = true; this.schedule(); }
  private read(entry: Entry) {
    if (!this.hello || entry.request || !entry.listeners.size) return;
    entry.pending = false;
    const request = new AbortController(); entry.request = request;
    const generation = this.generation; const epoch = this.epoch;
    const current = () => !this.disposed && generation === this.generation && entry.request === request
      && this.entries.get(id(entry.identity)) === entry && !!entry.listeners.size;
    this.transport.read(entry.identity, request.signal).then(observation => {
      if (!current()) return;
      if (observation.projectId !== entry.identity.projectId || observation.key !== entry.identity.key || observation.schema !== "nightweaver-observation-v1") throw Error("Invalid observation identity");
      if (observation.epoch !== epoch) { this.restart(); return; }
      const version = revision(observation.revision);
      if (version < max(entry.watermark, entry.wanted)) { this.publish(entry, { stale: true }); return; }
      const bytes = new TextEncoder().encode(JSON.stringify(observation)).length;
      this.evict(bytes - entry.bytes, entry);
      if ([...this.entries.values()].reduce((sum, item) => sum + item.bytes, 0) - entry.bytes + bytes > 16 * 1024 * 1024) throw Error("Observation storage limit reached");
      entry.bytes = bytes; entry.watermark = version; entry.wanted = max(entry.wanted, version);
      const refreshing = entry.snapshot.refreshing && observation.lastAttemptAt === entry.refreshAttempt;
      this.publish(entry, { observation, stale: false, error: undefined, refreshing });
    }).catch(error => { if (current()) this.publish(entry, { error: String(error), stale: true }); })
      .finally(() => { if (current()) { entry.request = undefined; if (entry.pending) this.schedule(); } });
  }
  private timer() {
    this.tick = this.clock.later(() => {
      if (this.disposed) return;
      if (this.close && this.clock.now() - this.lastFrame >= 25000) this.failed();
      if (this.visible && this.hello && this.clock.now() - this.lastPoll >= 2000) { this.lastPoll = this.clock.now(); this.resync(); }
      this.evict(); this.timer();
    }, 1000);
  }
}
const max = (a: bigint, b: bigint) => a > b ? a : b;
const missing = empty();

/** Interaction lifetime is independent of response eviction; never persist guidance. */
export class InteractionStore {
  private drafts = new Map<string, string>();
  get(projectId: string, beadId: string) { return this.drafts.get(id({ projectId, key: beadId })) ?? ""; }
  set(projectId: string, beadId: string, text: string) {
    const key = id({ projectId, key: beadId });
    const next = new Map(this.drafts); if (text) next.set(key, text); else next.delete(key);
    if (next.size > 20 || [...next.values()].reduce((sum, value) => sum + new TextEncoder().encode(value).length, 0) > 1024 * 1024)
      throw Error("Draft storage limit reached; send or explicitly discard another draft");
    this.drafts = next;
  }
  discard(projectId: string, beadId: string) { this.drafts.delete(id({ projectId, key: beadId })); }
}

export function queryKey(kind: "explorer" | "cleanup", filter: string, search = "", page = 1): string {
  const query = btoa(String.fromCharCode(...new TextEncoder().encode(search.trim()))).replaceAll("+", "-").replaceAll("/", "_").replace(/=+$/, "");
  return kind === "explorer" ? `explorer:${filter}:${query}` : `cleanup:${page}:${filter}:${query}`;
}
