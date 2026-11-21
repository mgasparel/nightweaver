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
/** Labels come from the validated server presenter, never inferred from original prose. */
export function decisionSections(item: AttentionItem) {
  if (item.kind !== "product-decision-required" || item.message?.state !== "supported") return [];
  return item.message.sections.filter(section => ["Question", "Recommended answer", "Tradeoffs"].includes(section.label)
    && section.text !== item.question);
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
