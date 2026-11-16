import { safeBeadId } from "./bead-model.ts";

export type ProductDraft = { type: string; priority: number; title: string; description: string; acceptanceCriteria: string };
export const emptyProduct = (): ProductDraft => ({ type: "feature", priority: 2, title: "", description: "", acceptanceCriteria: "" });
export type Result = { pending: boolean; review: boolean; message: string; errors?: Record<string, string[]>; created?: string; confirmed?: boolean };
const idle: Result = { pending: false, review: false, message: "" };

/** Commands are tab-owned and never replayed by observations, reconnects or navigation. */
export class ProductCommands {
  private states = new Map<string, Result>();
  private listeners = new Set<() => void>();
  private requestIds = new Map<string, string>();
  constructor(private send: typeof fetch, private reconcile: (kind: "tracker" | "cleanup") => void) {}
  get(key: string) { return this.states.get(key) ?? idle; }
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private publish(key: string, state: Result) { this.states.set(key, state); for (const listener of this.listeners) listener(); }
  acknowledge(key: string) { if (!this.get(key).pending) this.publish(key, idle); }
  create(key: string, draft: ProductDraft) {
    if (this.get(key).created) return Promise.resolve();
    return this.submit(key, "/api/beads", draft, "tracker", (value, status) => {
      if (status !== 201 || typeof value.id !== "string" || !safeBeadId(value.id) || value.type !== draft.type || value.priority !== draft.priority || value.status !== "open") throw Error("receipt");
      return { created: value.id, message: `Created ${value.id} in backlog. Planning requires explicit authorization.` };
    });
  }
  ready(key: string, id: string, expected: string) {
    return this.submit(key, `/api/beads/${encodeURIComponent(id)}/ready-for-planning`, { expected }, "tracker", (value, status) => {
      if (status !== 200 || value.beadId !== id || value.action !== "ready-for-planning" || value.stateChanged !== true) throw Error("receipt");
      return { confirmed: true, message: "Planning readiness saved. Normal scheduling rules still apply." };
    });
  }
  cleanup(key: string, id: string, runtimeId: string, revision: string) {
    const identity = JSON.stringify([key, runtimeId, revision]);
    if (!this.requestIds.has(identity)) this.requestIds.set(identity, crypto.randomUUID().replaceAll("-", ""));
    const requestId = this.requestIds.get(identity)!;
    return this.submit(key, `/api/cleanup/${encodeURIComponent(id)}/retry`, { runtimeId, revision, requestId }, "cleanup", (value, status) => {
      if (status !== 202 || value.request?.requestId !== requestId || value.request?.runtimeId !== runtimeId || value.request?.beadId !== id || value.request?.revision !== revision
        || !["queued", "accepted", "stale", "protected", "expired"].includes(value.request?.outcome)) throw Error("receipt");
      if (["stale", "protected", "expired"].includes(value.request.outcome)) this.requestIds.delete(identity);
      return { message: value.request.outcome === "queued" ? "Queued for owner inspection. Cleanup is not yet confirmed."
        : `Request ${value.request.outcome}. Refresh and inspect the current obligation.` };
    });
  }
  private async submit(key: string, url: string, body: unknown, kind: "tracker" | "cleanup", receipt: (value: any, status: number) => Partial<Result>) {
    if (this.get(key).pending || this.get(key).review) return;
    this.publish(key, { pending: true, review: false, message: "Submitting…" });
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 10000);
    const deadline = new Promise<never>((_, reject) => controller.signal.addEventListener("abort", () => reject(Error("deadline")), { once: true }));
    try {
      const response = await Promise.race([this.send(url, { method: "POST", cache: "no-store", signal: controller.signal,
        headers: { "Content-Type": "application/json", "X-Nightweaver-Request": "console-v1", Accept: "application/json" }, body: JSON.stringify(body) }), deadline]);
      const value = await Promise.race([response.json(), deadline]);
      if (!response.ok) {
        this.publish(key, { pending: false, review: response.status >= 500 || response.status === 409,
          errors: value.errors && typeof value.errors === "object" && Object.values(value.errors).every(rows => Array.isArray(rows) && rows.every(row => typeof row === "string")) ? value.errors : undefined,
          message: `${response.status >= 500 ? "Outcome unknown. Check current evidence before manually resubmitting. " : response.status === 409 ? "Evidence changed. Refresh and review before trying again. " : "Request rejected. "}${value.detail ?? value.title ?? "Review the fields and try again."} Your input is retained.` });
      } else this.publish(key, { pending: false, review: false, ...receipt(value, response.status) } as Result);
    } catch {
      this.publish(key, { pending: false, review: true, message: "Outcome unknown. Your input is retained. Check the backlog or current evidence before manually resubmitting. This request will not be repeated automatically." });
    } finally { clearTimeout(timer); this.reconcile(kind); }
  }
}
