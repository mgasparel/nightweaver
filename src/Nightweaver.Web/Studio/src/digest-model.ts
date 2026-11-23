export type DigestItem = { id: string; title: string; type: string; priority: number; status: string;
  activityKind: string; activityAt: string; outcomeLabel: string; summary: string; commits: string[];
  pullRequests: string[]; fromId?: string | null; fromRelation?: string | null;
  mergeEvidence?: { state: string; pullRequest?: string | null; mergeCommit?: string | null; commitUrl?: string | null; cleanupWarning?: string | null } | null;
  presentation?: { role: string; summary: string } };
export type Digest = { generatedAt: string; window: string; windowStart?: string | null; lastCaughtUpAt: string | null;
  isCaughtUp: boolean; metrics: { completedTotal: number; activeThreads: number; openOrUpdatedTotal: number; waitingForGuidance: number };
  threads: { root: DigestItem; completedCount: number; openCount: number; items: DigestItem[] }[];
  standaloneCompleted: DigestItem[]; standaloneOpen: DigestItem[] };
type BaseItem = { bead: Omit<DigestItem, "activityKind" | "activityAt" | "summary" | "outcomeLabel" | "commits" | "pullRequests"> & { updatedAt: string };
  activityKind: string; activityAt: string; fromId?: string | null; fromRelation?: string | null };
export type BaseDigest = Omit<Digest, "threads" | "standaloneCompleted" | "standaloneOpen"> & {
  threads: { root: BaseItem; completedCount: number; openCount: number; items: BaseItem[] }[];
  standaloneCompleted: BaseItem[]; standaloneOpen: BaseItem[] };
export type DigestMerges = { unavailableState?: string; sections: Record<string, { beadId: string; updatedAt: string; state: string; pending: boolean;
  validatedAgeMs?: number | null;
  outcomeLabel?: string; summary?: string; commits?: string[]; pullRequests?: string[];
  value?: DigestItem["mergeEvidence"]; source?: { observedAt: string | null; health: string; error?: unknown } }> };

/** Detail replaces only the matching row. Membership and clocks belong to base. */
export function digestWithSections(base: BaseDigest, merges?: DigestMerges, elapsedMs = 0): Digest {
  const item = (entry: BaseItem): DigestItem => {
    const row = entry.bead, section = merges?.sections[row.id];
    const matches = section?.beadId === row.id && Date.parse(section.updatedAt) === Date.parse(row.updatedAt);
    const fresh = matches && (section.state === "available" || section.state === "unavailable") && !section.pending && section.source?.health === "fresh"
      && !section.source.error && section.source.observedAt && section.validatedAgeMs != null
      && section.validatedAgeMs + elapsedMs < 120000;
    const evidence = row.status === "closed" && row.type === "task"
      ? fresh && section.state === "available" && section.value?.state === "verified" ? section.value
        : matches && section.value && (section.state === "stale" || !fresh && section.state === "available")
          ? { ...section.value, state: "stale" }
          : { state: matches ? section.state === "unavailable" ? section.value?.state ?? "unavailable" : section.state
            : section ? "stale" : merges ? merges.unavailableState ?? "unavailable" : "loading" }
      : undefined;
    return { ...row, ...entry, outcomeLabel: evidence?.state === "verified" ? "Merged" : fresh && section.outcomeLabel && section.outcomeLabel !== "Merged"
      ? section.outcomeLabel : row.status === "closed" ? "Closed" : row.presentation?.summary ?? row.status,
      summary: evidence?.state === "verified" ? "Host verified the merged pull request and passing required checks." : fresh ? section.summary ?? "" : "",
      commits: fresh ? section.commits ?? [] : [], pullRequests: fresh ? section.pullRequests ?? [] : [], mergeEvidence: evidence };
  };
  return { ...base, threads: base.threads.map(thread => ({ ...thread, root: item(thread.root), items: thread.items.map(item) })),
    standaloneCompleted: base.standaloneCompleted.map(item), standaloneOpen: base.standaloneOpen.map(item) };
}
export const windows = ["auto", "since_caught_up", "24h", "7d", "30d", "all"] as const;
export type DigestWindow = typeof windows[number];
export const digestWindow = (value: string | null): DigestWindow => windows.includes(value as DigestWindow) ? value as DigestWindow : "auto";

/** Preview window-active closed outcomes once; historical lineage context is not an outcome. */
export function completedOutcomes(data: Digest) {
  const items = [...data.standaloneCompleted, ...data.threads.flatMap(thread => [thread.root, ...thread.items])];
  return [...new Map(items.filter(item => item.status === "closed" && item.activityKind !== "context").map(item => [item.id, item])).values()]
    .sort((a, b) => Date.parse(b.activityAt) - Date.parse(a.activityAt));
}

/** Preserve the whole inventory when an open/focused thread crosses the preview boundary. */
export function visibleThreads(data: Digest, all: boolean, expanded: ReadonlySet<string>, focused?: string) {
  return all || data.threads.slice(5).some(thread => expanded.has(thread.root.id) || thread.root.id === focused)
    ? data.threads : data.threads.slice(0, 5);
}

type CheckpointState = { pending: boolean; message: string; saved?: string };
const idle: CheckpointState = { pending: false, message: "" };
/** Tab-owned explicit writes: capture the displayed cutoff before any asynchronous work. */
export class DigestCheckpoints {
  private states = new Map<string, CheckpointState>();
  private listeners = new Set<() => void>();
  constructor(private send: typeof fetch, private reconcile: () => void) {}
  get(project: string) { return this.states.get(project) ?? idle; }
  subscribe = (listener: () => void) => { this.listeners.add(listener); return () => { this.listeners.delete(listener); }; };
  private publish(project: string, state: CheckpointState) { this.states.set(project, state); for (const listener of this.listeners) listener(); }
  async save(project: string, displayed: Digest) {
    if (this.get(project).pending) return;
    const cutoff = displayed.generatedAt;
    if (!Number.isFinite(Date.parse(cutoff))) return;
    this.publish(project, { pending: true, message: "Saving displayed snapshot…" });
    const controller = new AbortController();
    let rejectDeadline!: (reason: Error) => void;
    const deadline = new Promise<never>((_, reject) => { rejectDeadline = reject; });
    const timer = setTimeout(() => { controller.abort(); rejectDeadline(Error("deadline")); }, 10000);
    try {
      const response = await Promise.race([this.send("/api/digest/catch-up", { method: "POST", signal: controller.signal,
        headers: { "Content-Type": "application/json", "X-Nightweaver-Request": "console-v1" },
        body: JSON.stringify({ caughtUpAt: cutoff }), cache: "no-store" }), deadline]);
      const receipt = await Promise.race([response.json(), deadline]);
      if (!response.ok || Date.parse(receipt.lastCaughtUpAt) !== Date.parse(cutoff)) throw Error("Unverified checkpoint");
      this.publish(project, { pending: false, message: "Displayed snapshot marked caught up. Later activity remains unread.", saved: cutoff });
    } catch {
      this.publish(project, { pending: false, message: "Checkpoint could not be verified. Review the current bookmark before explicitly trying again. Your displayed digest is retained." });
    } finally { clearTimeout(timer); this.reconcile(); }
  }
}
