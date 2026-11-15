import { actionRequest, type Action, type AttentionItem } from "./bead-model.ts";

export type CommandState = { pending: boolean; uncertain: boolean; message: string };
const idle: CommandState = { pending: false, uncertain: false, message: "" };

/** Tab-owned commands survive route changes. No automatic replay, including after timeout. */
export class AttentionCommands {
  private states = new Map<string, CommandState>();
  private listeners = new Set<() => void>();
  constructor(private send: typeof fetch, private reconcile: () => void) {}
  private key(project: string, bead: string) { return JSON.stringify([project, bead]); }
  get(project: string, bead: string) { return this.states.get(this.key(project, bead)) ?? idle; }
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private publish(key: string, state: CommandState) { this.states.set(key, state); for (const listener of this.listeners) listener(); }
  acknowledge(project: string, bead: string) {
    if (!this.get(project, bead).pending) this.publish(this.key(project, bead), idle);
  }
  async submit(project: string, item: AttentionItem, action: Action, instruction: string) {
    const key = this.key(project, item.beadId);
    const current = this.get(project, item.beadId);
    if (current.pending || current.uncertain) return;
    const request = actionRequest(item, action, instruction);
    this.publish(key, { pending: true, uncertain: false, message: "Submitting…" });
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), 10000);
    try {
      // Race the deadline too: a late adapter response must not settle a newer command.
      const response = await Promise.race([this.send(request.url, { method: "POST", signal: controller.signal,
        headers: { "Content-Type": "application/json", "X-Nightweaver-Request": "console-v1", Accept: "application/json" },
        body: JSON.stringify(request.body), cache: "no-store" }),
        new Promise<never>((_, reject) => controller.signal.addEventListener("abort", () => reject(Error("deadline")), { once: true }))]);
      const result = await Promise.race([response.json(), new Promise<never>((_, reject) => {
        if (controller.signal.aborted) reject(Error("deadline"));
        else controller.signal.addEventListener("abort", () => reject(Error("deadline")), { once: true });
      })]);
      if (!response.ok) {
        const progress = "commentRecorded" in result || "stateChanged" in result
          ? ` Comment: ${result.commentRecorded === true ? "recorded" : result.commentRecorded === false ? "not recorded" : "unverified"}. Workflow: ${result.stateChanged === true ? "changed" : result.stateChanged === false ? "unchanged" : "unverified"}.` : "";
        const uncertain = response.status >= 500 || !!progress;
        this.publish(key, { pending: false, uncertain,
          message: `${uncertain ? "Action outcome unknown. Review current state and history. " : response.status === 409 ? "Bead changed. Review current state before trying again. " : response.status === 404 ? "Bead unavailable. Your draft is retained. " : "Action rejected. "}${result.detail ?? "Refresh and review current state."}${progress}` });
        return;
      }
      const resolved = action !== "note_only";
      if (result.beadId !== item.beadId || result.action !== action || result.resolved !== resolved
        || result.commentRecorded !== true || result.stateChanged !== resolved) throw Error("Invalid command receipt");
      this.publish(key, { pending: false, uncertain: false, message: resolved
        ? "Action confirmed. Awaiting current observation; your draft is retained." : "Note saved. This Bead remains paused. Your draft is retained." });
    } catch {
      this.publish(key, { pending: false, uncertain: true,
        message: "Action outcome unknown. Your draft is retained. Review current state and history before another action. This request will not be resubmitted." });
    } finally { clearTimeout(timer); this.reconcile(); }
  }
}
