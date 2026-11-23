export type Message = { state: string; title: string; summary: string; role: string | null; stage: string | null;
  sections: { label: string; text: string; details: boolean; commentId: string | null }[]; original: string; sourceId: string };
export type AttentionItem = {
  beadId: string; title: string; type: string; status: string; priority: number;
  stage: string | null; labels: string[]; repairCycles: number | null;
  maxRepairCycles: number; kind: string; question: string | null; diagnostic: string | null;
  canDecide: boolean; canRetry: boolean; canAbort: boolean; unavailableReason: string | null;
  sourceCommentId: string | null; contextNotice: string | null;
  message?: Message | null; otherPauseReports?: Message[];
};
export type AttentionBaseRow = {
  id: string; title: string; type: string; status: string; priority: number; stage: string | null;
  labels: string[]; repairCycles: number | null; updatedAt: string;
  actions: { canDecide: boolean; canRetry: boolean; canAbort: boolean; unavailableReason: string | null;
    expectedWorkflow: { stage: string | null; pauseLabels: string[]; repairCycles: number | null } };
};
export type AttentionBase = { schema: "nightweaver-base-v1"; inventoryRevision: string;
  items: AttentionBaseRow[]; maxRepairCycles: number };
export type PauseExplanation = { beadId: string; state: string;
  baseIdentity: { updatedAt: string; expectedWorkflow: AttentionBaseRow["actions"]["expectedWorkflow"] };
  value: { attention: AttentionItem | null } | null };

/** A retained history cannot supply current context or actions for a different base identity. */
export function attentionFromBase(row: AttentionBaseRow, maxRepairCycles: number, section?: PauseExplanation, current = false): AttentionItem {
  const workflow = row.actions.expectedWorkflow;
  const identity = section?.baseIdentity;
  const matches = section?.beadId === row.id && identity?.updatedAt === row.updatedAt
    && identity.expectedWorkflow.stage === workflow.stage && identity.expectedWorkflow.repairCycles === workflow.repairCycles
    && [...identity.expectedWorkflow.pauseLabels].sort().join("\0") === [...workflow.pauseLabels].sort().join("\0");
  const evidence = current && matches && ["available", "empty"].includes(section!.state) ? section?.value?.attention : null;
  const kind = workflow.pauseLabels.length !== 1 || row.repairCycles === null ? "invalid-workflow"
    : workflow.pauseLabels[0] === "product-decision-required" ? "product-decision-required" : "needs-human";
  return { ...(evidence ?? {}), beadId: row.id, title: row.title, type: row.type, status: row.status,
    priority: row.priority, stage: row.stage, labels: row.labels, repairCycles: row.repairCycles, maxRepairCycles,
    kind, question: evidence?.question ?? null, diagnostic: evidence?.diagnostic ?? null,
    sourceCommentId: evidence?.sourceCommentId ?? null, contextNotice: evidence?.contextNotice ?? null,
    canDecide: !!evidence?.canDecide && row.actions.canDecide, canRetry: !!evidence?.canRetry && row.actions.canRetry,
    canAbort: !!evidence?.canAbort && row.actions.canAbort, unavailableReason: row.actions.unavailableReason };
}
/** Labels come from the validated server presenter, never inferred from original prose. */
export function decisionSections(item: AttentionItem) {
  if (item.kind !== "product-decision-required" || item.message?.state !== "supported") return [];
  return item.message.sections.filter(section => ["Question", "Recommended answer", "Tradeoffs"].includes(section.label)
    && section.text !== item.question);
}
/** Material retention warnings remain visible for either pause kind before actions. */
export function attentionWarningSections(item: AttentionItem) {
  if (item.message?.state !== "supported") return [];
  return item.message.sections.filter(section => ["Environment retention", "Historical cleanup warning"].includes(section.label));
}
/** Retain surviving focused/open rows across live reorder without opening the inventory. */
export function visibleAttention(items: AttentionItem[], limit: number, focused?: string, disclosed: ReadonlySet<string> = new Set()) {
  return items.filter((row, index) => index < limit || row.beadId === focused || disclosed.has(row.beadId));
}
/** An authoritative removal retires disclosure state; unavailable data does not. */
export function survivingAttentionDisclosures(items: AttentionItem[] | null | undefined, disclosed: Set<string>) {
  if (!items) return disclosed;
  const surviving = new Set(items.map(row => row.beadId));
  return [...disclosed].every(id => surviving.has(id)) ? disclosed : new Set([...disclosed].filter(id => surviving.has(id)));
}
/** Compact context cannot let legacy multiline evidence displace the Review action. */
export function attentionPreview(context: string) {
  return Array.from(context).slice(0, 140).join("").replace(/\s+/g, " ").trim();
}
export type WorkspaceData = {
  sourceMetadata?: string | null;
  presentation?: { role: string; summary: string; trackedStage: string | null; planning: string | null;
    publication: string | null; pause: string | null; ownership: string; repairsUsed: number | null;
    repairsMax: number | null; diagnostic: string | null } | null;
  planning?: { ready: boolean; canAuthorize: boolean; expected: string } | null;
  bead: { id: string; title: string; type: string; status: string; priority: number;
    stage: string | null; labels: string[]; assignee: string | null; repairCycles: number | null;
    description: string; design: string; acceptanceCriteria: string; notes: string; updatedAt: string };
  attention: AttentionItem | null;
  comments: { id: string; author: string; createdAt: string; body: string; kind: string; message?: Message | null }[];
  history: { activeCommentId: string | null; selectionSource: string };
  relations: { id: string; type: string; direction: string; title: string | null; role: string; issueType?: string | null; status?: string | null }[];
  relationObservation: { incoming: string; outgoing: string };
  environment: { state: string; observedAt: string; worktreePath: string | null; branch: string | null;
    head: string | null; gitStatus: string; processId: number | null; processState: string;
    source: string; warning: string | null; runtimeObservedAt: string | null; runtimeSource: string | null };
};
export const beadPath = (project: string, bead: string) =>
  `/projects/${encodeURIComponent(project)}/beads/${encodeURIComponent(bead)}`;
export const safeBeadId = (id: string) => /^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(id) && !id.endsWith(".")
  && !/^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$/i.test(id.split(".")[0]);
export const expectedWorkflow = (item: AttentionItem) => ({ stage: item.stage ?? null,
  pauseLabels: item.labels.filter(label => label === "needs-human" || label === "product-decision-required"),
  repairCycles: item.repairCycles ?? 0 });
export type Action = "note_only" | "resume" | "retry" | "abort";
export function actionRequest(item: AttentionItem, action: Action, instruction: string) {
  return { url: `/api/beads/${encodeURIComponent(item.beadId)}/${action === "resume" || action === "note_only" ? "decide" : action}`,
    body: action === "abort" ? {} : action === "retry" ? { expected: expectedWorkflow(item) }
      : action === "resume" ? { instruction, action, expected: expectedWorkflow(item) } : { instruction, action } };
}

/** Only explicit absolute HTTP(S) evidence without credentials or whitespace is navigable. */
export function safeEvidenceUrl(text: string): boolean {
  if (!/^https?:\/\//i.test(text) || /\s/.test(text)) return false;
  try { const url = new URL(text); return !!url.hostname && !url.username && !url.password; }
  catch { return false; }
}
