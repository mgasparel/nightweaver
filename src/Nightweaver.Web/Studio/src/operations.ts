export type Identity = { projectId: string; key: string };
export type ObservationError = { code: string; message: string; retryable: boolean };
export type SourceState = { health: "fresh" | "stale" | "unavailable"; observedAt: string | null;
  lastAttemptAt: string | null; error: ObservationError | null };
export type Envelope<T = unknown> = Identity & {
  schema: "nightweaver-observation-v1"; epoch: string; revision: string;
  health: "fresh" | "stale" | "unavailable"; data: T | null;
  observedAt: string | null; lastAttemptAt: string | null;
  policy?: { expectedValidationIntervalMs: number; maxSnapshotAgeMs: number; maxPendingAgeMs: number } | null;
  validatedAgeMs?: number | null; pendingAgeMs?: number | null;
  synchronization?: { journal: "catching-up" | "current" | "degraded" | "inactive"; pending: boolean; delayed?: boolean; error?: ObservationError | null } | null;
  sources: Record<string, SourceState>; error: ObservationError | null; targetIntervalMs: number; nextAttemptAt: string | null;
};
export type Snapshot<T = unknown> = { observation?: Envelope<T>; connectionState: "connecting" | "connected" | "recovering" | "disconnected";
  dataState?: "loading" | "unavailable" | "current" | "refreshing" | "delayed" | "stale";
  synchronized?: boolean; updatesRecovering?: boolean;
  refreshing: boolean; stale: boolean; error?: string };
type Frame = { schema: string; projectId: string; epoch: string; versions?: Record<string, string>; heartbeatMs?: number };
export type Transport = {
  connect(project: string, keys: string[], event: (name: string, data: Frame) => void, failed: () => void): () => void;
  read(identity: Identity, signal: AbortSignal): Promise<Envelope>;
  refresh(project: string, keys: string[], signal: AbortSignal): Promise<void>;
  wake?(project: string, keys: string[], signal: AbortSignal): Promise<void>;
};
export type Scheduler = { now(): number; later(callback: () => void, ms: number): () => void; random(): number };
type Entry = { identity: Identity; listeners: Set<() => void>; snapshot: Snapshot; watermark: bigint;
  wanted: bigint; pending: boolean; request?: AbortController; cancelDeadline?: () => void;
  cancelCatchup?: () => void;
  receivedAt?: number; catchupSince?: number; needsRead?: boolean;
  bytes: number; touched: number; refreshAttempt?: string | null; refreshOwner?: object; refreshError?: string };
type RefreshAttempt = { promise: Promise<void>; cancel(error: Error): void };
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
  private lastBackend?: number;
  private firstConnectionAttempt?: number;
  private lastPoll = 0;
  private visible = true;
  private disposed = false;
  private hello = false;
  private signature = "";
  private wakeAttempt?: AbortController;
  private buffered = new Map<string, { epoch: string; revision: bigint }>();
  private refreshes = new Map<string, RefreshAttempt>();
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
      if (!entry.listeners.size) this.cancelRead(entry);
      this.schedule();
    };
  }

  getSnapshot<T = unknown>(identity: Identity): Snapshot<T> { return (this.entries.get(id(identity))?.snapshot ?? missing) as Snapshot<T>; }

  async refresh(identities: Identity[]): Promise<void> {
    if (this.disposed) throw Error("Coordinator disposed");
    const selected = [...new Map(identities.map(identity => [id(identity), identity])).values()];
    if (!selected.length) return;
    const project = selected[0].projectId;
    if (selected.length > 32 || selected.some(identity => identity.projectId !== project)) throw Error("Invalid refresh scope");
    const key = JSON.stringify([project, selected.map(identity => identity.key).sort()]);
    const existing = this.refreshes.get(key);
    if (existing) return existing.promise;
    const owner = {};
    const entries: Entry[] = [];
    for (const identity of selected) {
      const entry = this.entries.get(id(identity));
      if (entry) {
        entries.push(entry); entry.refreshOwner = owner; entry.refreshError = undefined;
        entry.refreshAttempt = entry.snapshot.observation?.lastAttemptAt ?? null;
        this.publish(entry, { refreshing: true, error: undefined });
      }
    }
    const request = new AbortController();
    let resolve!: () => void; let reject!: (error: unknown) => void;
    const promise = new Promise<void>((yes, no) => { resolve = yes; reject = no; });
    let cancelDeadline = () => {};
    const finish = (failed: boolean, error?: unknown) => {
      if (this.refreshes.get(key) !== attempt) return;
      // Release identity first; abort handlers and late transport settlement may reenter.
      this.refreshes.delete(key); cancelDeadline();
      if (failed) request.abort();
      for (const entry of entries) {
        if (this.entries.get(id(entry.identity)) !== entry || entry.refreshOwner !== owner) continue;
        entry.refreshOwner = undefined;
        if (failed) {
          entry.refreshError = String(error);
          this.publish(entry, { refreshing: false, error: entry.refreshError });
        } else this.queue(entry);
      }
      if (failed) reject(error); else resolve();
    };
    const attempt: RefreshAttempt = { promise, cancel: error => finish(true, error) };
    this.refreshes.set(key, attempt);
    cancelDeadline = this.clock.later(() => attempt.cancel(Error("Reconciliation request timed out; outcome unknown. Refresh again to retry.")), 10000);
    try {
      this.transport.refresh(project, selected.map(identity => identity.key), request.signal)
        .then(() => finish(false), error => finish(true, error));
    } catch (error) { finish(true, error); }
    return promise;
  }

  invalidateAfterCommand(kind: "tracker" | "checkpoint" | "cleanup"): Promise<void> {
    const keys = this.active().filter(({ identity }) => kind === "tracker" ? /^(attention|explorer:|digest:|workspace:|base:|section:|cleanup:)/.test(identity.key)
      : kind === "checkpoint" ? identity.key.startsWith("digest:") : /^(cleanup:|factory$)/.test(identity.key));
    return this.refresh(keys.map(entry => entry.identity));
  }

  setVisible(visible: boolean) {
    if (visible === this.visible) return;
    this.visible = visible;
    if (visible) this.wake();
    else { this.retry?.(); this.retry = undefined; this.disconnect(); }
  }
  suspend() {
    this.retry?.(); this.retry = undefined; this.disconnect(); this.wakeAttempt?.abort();
    this.cancelRefreshes("Page suspended; reconciliation outcome unknown");
  }
  wake() {
    if (this.disposed || !this.visible) return;
    for (const entry of this.active()) { entry.needsRead = true; this.publish(entry, { synchronized: false }); }
    if (!this.hello) this.restart(); else this.resync();
    // Wake is a new read-reconciliation hint, never a replay of a guarded
    // tracker command. The server also checks the journal immediately.
    const active = this.active();
    if (active.length && this.transport.wake) {
      this.wakeAttempt?.abort();
      const request = new AbortController(); this.wakeAttempt = request;
      const cancel = this.clock.later(() => request.abort(), 10000);
      void this.transport.wake(active[0].identity.projectId, active.map(entry => entry.identity.key), request.signal)
        .catch(() => {}).finally(() => { cancel(); if (this.wakeAttempt === request) this.wakeAttempt = undefined; });
    }
  }
  dispose() {
    this.disposed = true; this.scheduled?.(); this.retry?.(); this.tick?.(); this.disconnect();
    this.wakeAttempt?.abort();
    this.cancelRefreshes("Coordinator disposed; reconciliation outcome unknown");
    this.entries.clear();
  }
  private active() { return [...this.entries.values()].filter(entry => entry.listeners.size); }
  private cancelRefreshes(message: string) {
    for (const attempt of this.refreshes.values()) attempt.cancel(Error(message));
  }
  private publish(entry: Entry, patch: Partial<Snapshot>) {
    const snapshot = { ...entry.snapshot, ...patch };
    const envelope = snapshot.observation;
    const elapsed = entry.receivedAt === undefined ? 0 : Math.max(0, this.clock.now() - entry.receivedAt);
    const age = envelope?.validatedAgeMs == null ? undefined : envelope.validatedAgeMs + elapsed;
    const pendingAge = envelope?.pendingAgeMs == null ? undefined : envelope.pendingAgeMs + elapsed;
    const catchupAge = entry.catchupSince === undefined ? undefined : this.clock.now() - entry.catchupSince;
    // Runtime retains its own policy. Tracker age is supplied by the server,
    // never computed by subtracting unrelated browser/server wall clocks.
    const tracker = envelope && envelope.key !== "factory" && !envelope.key.startsWith("cleanup:");
    const expired = envelope?.policy ? age === undefined || age > envelope.policy.maxSnapshotAgeMs
      : tracker ? true : !!envelope && age !== undefined && age > envelope.targetIntervalMs + 2000;
    snapshot.stale = expired || envelope?.health === "stale";
    snapshot.dataState = !envelope?.data ? envelope?.error ? "unavailable" : "loading"
      : snapshot.stale ? "stale"
      : (pendingAge ?? 0) > (envelope.policy?.maxPendingAgeMs ?? 5000) || (catchupAge ?? 0) > 5000 ? "delayed"
      : envelope.synchronization?.pending || envelope.synchronization?.journal === "catching-up" || entry.needsRead || entry.wanted > entry.watermark || snapshot.refreshing ? "refreshing" : "current";
    entry.snapshot = snapshot;
    for (const listener of entry.listeners) listener();
  }
  private evict(requiredBytes = 0, except?: Entry) {
    const unused = [...this.entries.values()].filter(entry => !entry.listeners.size && entry !== except).sort((a, b) => a.touched - b.touched);
    for (const entry of unused) {
      const bytes = [...this.entries.values()].reduce((sum, item) => sum + item.bytes, 0);
      if (this.entries.size < 64 && bytes + requiredBytes <= 16 * 1024 * 1024 && this.clock.now() - entry.touched < 30000) break;
      this.cancelRead(entry); this.entries.delete(id(entry.identity));
    }
  }
  private schedule() {
    if (this.scheduled || this.disposed) return;
    this.scheduled = this.clock.later(() => {
      this.scheduled = undefined;
      const signature = JSON.stringify(this.active().map(entry => id(entry.identity)).sort());
      if (signature !== this.signature) { this.signature = signature; this.restart(); }
      if (this.visible) for (const entry of this.active()) if (entry.pending) this.read(entry);
    }, 0);
  }
  private connection(): Snapshot["connectionState"] {
    const evidenceSince = this.lastBackend ?? this.firstConnectionAttempt;
    if (evidenceSince !== undefined && this.clock.now() - evidenceSince >= 25000) return "disconnected";
    if (this.lastBackend === undefined) return this.retries ? "recovering" : "connecting";
    return this.hello && this.active().every(entry => !entry.needsRead) ? "connected" : "recovering";
  }
  private disconnect() {
    ++this.generation; this.hello = false; this.close?.(); this.close = undefined;
    this.buffered.clear();
    for (const entry of this.entries.values()) {
      this.cancelRead(entry);
      entry.needsRead = true;
      this.publish(entry, { connectionState: this.connection(), synchronized: false, updatesRecovering: true });
    }
  }
  private restart() {
    this.retry?.(); this.retry = undefined; this.disconnect();
    const active = this.active();
    if (!active.length) this.firstConnectionAttempt = undefined;
    if (!active.length || this.disposed || !this.visible) return;
    const generation = this.generation;
    this.firstConnectionAttempt ??= this.clock.now();
    this.lastFrame = this.clock.now();
    for (const entry of active) this.publish(entry, { connectionState: this.connection() });
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
        for (const entry of this.entries.values()) { entry.watermark = 0n; entry.wanted = 0n; entry.needsRead = true; }
      }
      this.hello = true; this.retries = 0; this.lastBackend = this.clock.now();
      for (const entry of active) {
        const buffered = this.buffered.get(entry.identity.key);
        entry.wanted = max(max(entry.watermark, revision(frame.versions[entry.identity.key])), buffered?.epoch === frame.epoch ? buffered.revision : 0n);
        entry.needsRead = true;
        this.publish(entry, { connectionState: "recovering", synchronized: false, updatesRecovering: true });
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
    this.lastBackend = this.clock.now();
    for (const entry of this.active()) this.publish(entry, { connectionState: this.connection() });
    if (name === "resync") this.resync();
    if (name === "invalidation") {
      for (const entry of this.active()) {
        const value = frame.versions?.[entry.identity.key];
        if (value && revision(value) > max(entry.wanted, entry.watermark)) {
          entry.wanted = revision(value);
          // A notification starts a replacement read, not a source failure. Keep
          // the last validated status while catching up, with one fixed deadline
          // that subsequent notifications cannot postpone.
          entry.catchupSince ??= this.clock.now();
          if (!entry.cancelCatchup) entry.cancelCatchup = this.clock.later(() => {
            entry.cancelCatchup = undefined;
            if (entry.listeners.size && entry.wanted > entry.watermark)
              this.publish(entry, {});
          }, 5000);
          this.queue(entry);
        }
      }
    }
  }
  private resync() { for (const entry of this.active()) this.queue(entry); }
  private queue(entry: Entry) { entry.pending = true; this.schedule(); }
  private cancelRead(entry: Entry) {
    entry.cancelCatchup?.(); entry.cancelCatchup = undefined;
    entry.cancelDeadline?.(); entry.cancelDeadline = undefined;
    const request = entry.request; entry.request = undefined;
    request?.abort();
  }
  private read(entry: Entry) {
    if (!this.epoch || !this.visible || entry.request || !entry.listeners.size) return;
    entry.pending = false;
    const request = new AbortController(); entry.request = request;
    const generation = this.generation; const epoch = this.epoch;
    const current = () => !this.disposed && generation === this.generation && entry.request === request
      && this.entries.get(id(entry.identity)) === entry && !!entry.listeners.size;
    entry.cancelDeadline = this.clock.later(() => {
      if (!current()) return;
      // Release identity before abort: adapters may ignore cancellation or settle late.
      this.cancelRead(entry);
      this.publish(entry, { error: "Observation read timed out; retrying", refreshing: false });
      this.queue(entry);
    }, 10000);
    this.transport.read(entry.identity, request.signal).then(observation => {
      if (!current()) return;
      if (observation.projectId !== entry.identity.projectId || observation.key !== entry.identity.key || observation.schema !== "nightweaver-observation-v1") throw Error("Invalid observation identity");
      if (observation.epoch !== epoch) { this.restart(); return; }
      if (!["fresh", "stale", "unavailable"].includes(observation.health) || !observation.sources
        || !Number.isFinite(observation.targetIntervalMs) || observation.targetIntervalMs <= 0)
        throw Error("Malformed observation");
      const version = revision(observation.revision);
      this.lastBackend = this.clock.now();
      if (version < entry.watermark) return;
      if (version < entry.wanted) return;
      const bytes = new TextEncoder().encode(JSON.stringify(observation)).length;
      this.evict(bytes - entry.bytes, entry);
      if ([...this.entries.values()].reduce((sum, item) => sum + item.bytes, 0) - entry.bytes + bytes > 16 * 1024 * 1024) throw Error("Observation storage limit reached");
      this.lastBackend = this.clock.now(); entry.receivedAt = this.clock.now(); entry.needsRead = false;
      entry.catchupSince = undefined;
      entry.bytes = bytes; entry.watermark = version; entry.wanted = max(entry.wanted, version);
      entry.cancelCatchup?.(); entry.cancelCatchup = undefined;
      const refreshing = entry.snapshot.refreshing && observation.lastAttemptAt === entry.refreshAttempt;
      this.publish(entry, { observation, stale: false, error: entry.refreshError, refreshing,
        synchronized: this.hello, updatesRecovering: !this.hello, connectionState: this.connection() });
    }).catch(error => { if (current()) this.publish(entry, { error: String(error) }); })
      .finally(() => {
        if (current()) {
          entry.cancelDeadline?.(); entry.cancelDeadline = undefined; entry.request = undefined;
          if (entry.pending) this.schedule();
        }
      });
  }
  private timer() {
    this.tick = this.clock.later(() => {
      if (this.disposed) return;
      if (this.close && this.clock.now() - this.lastFrame >= 25000) this.failed();
      for (const entry of this.entries.values()) this.publish(entry, { connectionState: this.connection() });
      if (this.visible && this.epoch && this.clock.now() - this.lastPoll >= 2000) { this.lastPoll = this.clock.now(); this.resync(); }
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

export function observationStatus(snapshot: Snapshot): string {
  const body = snapshot.observation?.error ? `Source error · ${snapshot.observation.error.message}`
    : ({ loading: "Loading", unavailable: "Unavailable", current: "Current", refreshing: "Refreshing",
      delayed: "Updates delayed", stale: "Snapshot stale" } as const)[snapshot.dataState ?? "loading"];
  const channel = snapshot.observation?.synchronization?.error?.message
    ?? (snapshot.observation?.synchronization?.journal === "catching-up" ? "Catching up" : undefined);
  return channel ? `${body} · ${channel}` : body;
}
