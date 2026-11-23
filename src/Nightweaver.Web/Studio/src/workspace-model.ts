import type { Snapshot } from "./operations.ts";
import type { WorkspaceData } from "./bead-model.ts";

export type Workflow = { stage: string | null; pauseLabels: string[]; repairCycles: number | null };
export type BaseRow = Omit<WorkspaceData["bead"], "description" | "design" | "acceptanceCriteria" | "notes"> & {
  presentation: WorkspaceData["presentation"]; planning: WorkspaceData["planning"];
  actions: { canDecide: boolean; canRetry: boolean; canAbort: boolean; unavailableReason: string | null; expectedWorkflow: Workflow };
};
export type WorkspaceBase = { schema: "nightweaver-base-v1"; inventoryRevision: string; completeness: "complete";
  bead: BaseRow | null; absence: { id: string; inventoryRevision: string } | null };
export type Section<T> = { schema: "nightweaver-section-v1"; beadId: string; name: string;
  baseIdentity: { inventoryRevision: string; updatedAt: string; expectedWorkflow: Workflow } | null;
  state: "loading" | "available" | "empty" | "stale" | "unavailable" | "failed"; value: T | null };
export type TextSection = Pick<WorkspaceData["bead"], "description" | "design" | "acceptanceCriteria" | "notes"> & Pick<WorkspaceData, "sourceMetadata">;
export type HistorySection = Pick<WorkspaceData, "comments" | "history" | "attention">;
export type RelationSection = Pick<WorkspaceData, "relations" | "relationObservation">;

/** Inventory revision can advance without changing this Bead. Compare its full workflow and timestamp. */
export function sectionView<T>(snapshot: Snapshot<Section<T>>, base: BaseRow, name: string) {
  const section = snapshot.observation?.data;
  const value = section?.schema === "nightweaver-section-v1" && section.beadId === base.id && section.name === name ? section.value : null;
  const workflow = section?.baseIdentity?.expectedWorkflow;
  const expected = base.actions.expectedWorkflow;
  const matches = !!section && section.baseIdentity?.updatedAt === base.updatedAt && !!workflow
    && workflow.stage === expected.stage && workflow.repairCycles === expected.repairCycles
    && [...workflow.pauseLabels].sort().join("\0") === [...expected.pauseLabels].sort().join("\0");
  const current = matches && snapshot.synchronized === true && !snapshot.stale && !snapshot.error
    && snapshot.observation?.health === "fresh" && (section?.state === "available" || section?.state === "empty");
  const state = value ? current ? section!.state : "stale"
    : snapshot.error || snapshot.observation?.error ? "failed" : section?.state ?? "loading";
  return { value, current, state };
}

export function baseIsCurrent(snapshot: Snapshot<WorkspaceBase>) {
  return snapshot.synchronized === true && !snapshot.stale && !snapshot.error && snapshot.observation?.health === "fresh";
}

/** Confirmed absence intentionally has unavailable aggregate health, with fresh inventory proof. */
export function baseIsMissing(snapshot: Snapshot<WorkspaceBase>, id: string) {
  const envelope = snapshot.observation;
  const data = envelope?.data;
  return snapshot.synchronized === true && !snapshot.stale && !snapshot.error
    && envelope?.error?.code === "resource-missing" && envelope.sources.inventory?.health === "fresh"
    && envelope.synchronization?.pending === false && !envelope.synchronization.error
    && data?.schema === "nightweaver-base-v1" && data.completeness === "complete"
    && data.bead === null && data.absence?.id === id && data.absence.inventoryRevision === data.inventoryRevision;
}

export function currentAttention(base: BaseRow, history: ReturnType<typeof sectionView<HistorySection>>) {
  const item = history.current ? history.value?.attention : null;
  if (!item || item.beadId !== base.id || base.status === "closed" || !base.actions.expectedWorkflow.pauseLabels.length) return null;
  return { ...item, title: base.title, status: base.status, type: base.type, priority: base.priority,
    stage: base.stage, labels: base.labels, repairCycles: base.repairCycles,
    canDecide: base.actions.canDecide, canRetry: base.actions.canRetry, canAbort: base.actions.canAbort,
    unavailableReason: base.actions.unavailableReason };
}
