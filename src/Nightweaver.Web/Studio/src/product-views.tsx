import { useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import { Link, useSearchParams } from "react-router";
import { Panel, Status } from "./primitives";
import { operations, interactions } from "./operations-browser";
import { queryKey } from "./operations";
import { useObservation } from "./use-observation";
import { beadPath, type WorkspaceData } from "./bead-model";
import { Freshness, FocusBoundary } from "./bead-views";
import { ProductCommands, emptyProduct, type ProductDraft } from "./product-commands";

const commands = new ProductCommands(window.fetch.bind(window), kind => { void operations.invalidateAfterCommand(kind).catch(() => {}); });
const commandKey = (project: string, action: string, id = "") => JSON.stringify([project, action, id]);
function useCommand(key: string) { return useSyncExternalStore(commands.subscribe, useCallback(() => commands.get(key), [key])); }

export function ProductIntake({ project }: { project: string }) {
  const key = commandKey(project, "intake");
  const [draft, setDraft] = useState<ProductDraft>(() => {
    const saved = interactions.get(project, "$product-intake");
    return saved ? JSON.parse(saved) : emptyProduct();
  });
  const [storageError, setStorageError] = useState("");
  const state = useCommand(key);
  const result = useRef<HTMLParagraphElement>(null);
  function update(next: ProductDraft) {
    try { interactions.set(project, "$product-intake", JSON.stringify(next)); setDraft(next); setStorageError(""); }
    catch (error) { setStorageError(String(error)); }
  }
  return <Panel title="New Product Bead">
    <p>Describe a feature or bug. Creation leaves it open and unassigned in backlog; Ready for Planning is a separate action.</p>
    <form className="product-form" onSubmit={event => { event.preventDefault(); void commands.create(key, { ...draft }).then(() => result.current?.focus()); }}>
      <div className="product-options"><label>Type<select value={draft.type} disabled={state.pending} aria-invalid={!!state.errors?.type} aria-describedby="type-error" onChange={e => update({ ...draft, type: e.target.value })}><option value="feature">Feature</option><option value="bug">Bug</option></select><span id="type-error" className="field-error">{state.errors?.type?.join(" ")}</span></label>
        <label>Priority<select value={draft.priority} disabled={state.pending} aria-invalid={!!state.errors?.priority} aria-describedby="priority-error" onChange={e => update({ ...draft, priority: Number(e.target.value) })}>{[0, 1, 2, 3].map(p => <option key={p} value={p}>P{p}</option>)}</select><span id="priority-error" className="field-error">{state.errors?.priority?.join(" ")}</span></label></div>
      {([['title', 'Title', 200], ['description', 'Description', 4000], ['acceptanceCriteria', 'Acceptance criteria', 2000]] as const).map(([name, label, max]) => <label key={name}>{label}<span className="field-help">{max} characters maximum</span>
        {name === "title" ? <input required maxLength={max} value={draft[name]} disabled={state.pending} aria-invalid={!!state.errors?.[name]} aria-describedby={`${name}-error`} onChange={e => update({ ...draft, [name]: e.target.value })} />
          : <textarea required rows={name === "description" ? 5 : 3} maxLength={max} value={draft[name]} disabled={state.pending} aria-invalid={!!state.errors?.[name]} aria-describedby={`${name}-error`} onChange={e => update({ ...draft, [name]: e.target.value })} />}
        <span id={`${name}-error`} className="field-error">{state.errors?.[name]?.join(" ")}</span></label>)}
      {storageError && <p role="alert">{storageError}</p>}
      {state.errors && <p role="alert">{Object.entries(state.errors).map(([name, errors]) => `${name}: ${errors.join(" ")}`).join(" · ")}</p>}
      <div className="bead-actions"><button className="button primary" disabled={state.pending || state.review || !!state.created}>Create Product Bead</button>
        <button type="button" className="button" disabled={state.pending || state.review} onClick={() => { interactions.discard(project, "$product-intake"); setDraft(emptyProduct()); commands.acknowledge(key); }}>Start a new draft</button></div>
      <p ref={result} tabIndex={-1} role="status" className={state.message ? "bead-notice" : ""}>{state.message}</p>
      {state.created && <Link className="button" to={beadPath(project, state.created)}>Open {state.created}</Link>}
      {state.review && <><a className="text-link" href="/#beads" target="_blank" rel="noopener noreferrer">Check current backlog</a><button type="button" className="button" onClick={() => commands.acknowledge(key)}>I checked current evidence; allow manual submission</button></>}
    </form>
  </Panel>;
}

export function PlanningAuthorization({ project, data, current }: { project: string; data: Pick<WorkspaceData, "bead" | "planning">; current: boolean }) {
  const key = commandKey(project, "ready", data.bead.id);
  const state = useCommand(key);
  const planning = data.planning;
  const heading = useRef<HTMLHeadingElement>(null);
  useLayoutEffect(() => {
    if (state.confirmed && document.activeElement === document.body) heading.current?.focus();
  }, [state.confirmed]);
  if (!planning) return null;
  return <section className="planning-authorization" aria-label="Planning readiness"><h3 ref={heading} tabIndex={-1}>Planning readiness</h3>
    <p>{planning.ready ? "Ready for Planning. Normal scheduling and pause rules still apply." : "Backlog. Creation and prose edits do not authorize Planning."}</p>
    <p>The host compares this displayed identity with fresh tracker state before authorization, including when source observations are overdue.</p>
    {!planning.ready && !state.confirmed && <><p>Only an open, unassigned, unpaused Product can be authorized. This does not launch an Agent directly.</p>
      <button className="button primary" disabled={!current || !planning.canAuthorize || state.pending || state.review} onClick={() => void commands.ready(key, data.bead.id, planning.expected)}>Ready for Planning</button></>}
    {state.message && <p role="status" className="bead-notice">{state.message}</p>}
    {(state.review || state.confirmed && !planning.ready) && <button className="button" disabled={!current} onClick={() => { void operations.refresh([{ projectId: project, key: `base:workspace:${data.bead.id}` }]).then(() => commands.acknowledge(key)).catch(() => {}); }}>Refresh and review readiness</button>}
  </section>;
}

type CleanupItem = { beadId: string; status: string; revision: string; retry?: { outcome: string; requestId: string }; record: {
  attempts: number; cycleAttempts?: number; nextAttempt?: string; lastAttempt?: string; resolvedAt?: string;
  diagnostic?: string; lastFailure?: string; lastDenial?: string; verifiedHead?: string; mergeCommit?: string; lastRetryRequestId?: string;
  worktree: { path: string; confirmedAbsent: boolean }; tmp: { path: string; confirmedAbsent: boolean };
} };
type CleanupPage = { availability: string; reason?: string; observedAt: string; runtimeId: string; canRetry: boolean;
  counts: Record<string, number> | null; page: number; pages: number; total: number; items: CleanupItem[] };
const date = (value?: string) => value ? new Date(value).toLocaleString() : "None";
const labels: Record<string, string> = { pending: "Pending", "retry-scheduled": "Retry scheduled", "needs-attention": "Needs attention", resolved: "Resolved" };
function CleanupRow({ project, item, data, current }: { project: string; item: CleanupItem; data: CleanupPage; current: boolean }) {
  const key = commandKey(project, "cleanup", item.beadId);
  const state = useCommand(key);
  const record = item.record;
  const retry = item.retry;
  const outcome = retry?.outcome === "accepted" && record.lastRetryRequestId === retry.requestId && (record.cycleAttempts ?? 0) > 0 ? "attempted" : retry?.outcome;
  return <article className="cleanup-card">
    <div className="bead-row-meta"><Link className="text-link" to={beadPath(project, item.beadId)}>{item.beadId}</Link><Status tone={item.status === "needs-attention" ? "warn" : "accent"}>{labels[item.status]}</Status></div>
    {record.diagnostic && <p>{record.diagnostic}</p>}
    <p>{record.attempts} total attempts · Next: {date(record.nextAttempt)}</p>
    <details><summary>Resources &amp; attempt evidence</summary>
      {([['Worktree', record.worktree], ['Temporary files', record.tmp]] as const).map(([name, value]) =>
        <p key={name}>{name}: {value.confirmedAbsent ? "confirmed absent" : "not confirmed absent"}<code>{value.path}</code></p>)}
      <p>Last attempt: {date(record.lastAttempt)} · Cycle attempts: {record.cycleAttempts ?? record.attempts} · Resolved: {date(record.resolvedAt)}</p>
      {([['Previous failure', record.lastFailure], ['Policy/access denial', record.lastDenial], ['Verified head', record.verifiedHead], ['Merge commit', record.mergeCommit]] as const).map(([name, value]) => value && <p key={name}>{name}: {value}</p>)}
    </details>
    {outcome && <p>Cleanup Retry: {outcome}{outcome === "queued" ? " — awaiting owner inspection" : ["stale", "protected", "expired"].includes(outcome) ? " — rejected; refresh and inspect before retrying" : ""}.</p>}
    {item.status === "needs-attention" && <button className="button" disabled={!current || !data.canRetry || !data.runtimeId || state.pending || state.review || outcome === "queued"} onClick={() => void commands.cleanup(key, item.beadId, data.runtimeId, item.revision)}>Retry cleanup</button>}
    {state.message && <p role="status" className="bead-notice">{item.status === "resolved" ? "Cleanup confirmed: worktree, registration and temporary files are absent." : state.message}</p>}
    {state.review && <button className="button" disabled={!current} onClick={() => commands.acknowledge(key)}>I reviewed current cleanup evidence</button>}
  </article>;
}

export function CleanupView({ project }: { project: string }) {
  const [params, setParams] = useSearchParams();
  const filter = Object.keys(labels).includes(params.get("status") ?? "") ? params.get("status")! : "all";
  const search = (params.get("q") ?? "").slice(0, 200);
  const page = Math.max(1, Math.min(1000000, Number(params.get("page")) || 1));
  const resource = queryKey("cleanup", filter, search, Math.floor(page));
  const snapshot = useObservation<CleanupPage>(project, resource);
  const data = snapshot.observation?.data;
  const current = !snapshot.stale && snapshot.observation?.health === "fresh";
  const range = useRef<HTMLParagraphElement>(null);
  useEffect(() => { if (range.current && document.activeElement === document.body) range.current.focus(); }, [resource]);
  function change(name: string, value: string) { const next = new URLSearchParams(params); next.set(name, value); if (name !== "page") next.delete("page"); setParams(next); }
  return <FocusBoundary><div className="page-heading"><p className="eyebrow">Current project / Studio</p><h1 tabIndex={-1}>Housekeeping</h1><p>Completed environments, with disposal evidence and guarded recovery.</p></div>
    <Freshness snapshot={snapshot} project={project} resource={resource} />
    <Panel title="Cleanup obligations"><p>Delivery completion and cleanup are separate. Retry asks the current owner to inspect exact evidence; it cannot bypass a policy or access denial.</p>
      <div className="explorer-controls"><label>Search cleanup<input type="search" maxLength={200} value={search} onChange={e => change("q", e.target.value)} /></label>
        <label>Status<select value={filter} onChange={e => change("status", e.target.value)}><option value="all">All statuses</option>{Object.entries(labels).map(([key, label]) => <option key={key} value={key}>{label}</option>)}</select></label></div>
      <p role="status">Owner {data?.availability ?? "unavailable"} · {data?.canRetry ? "accepting Retry" : "Retry unavailable"}. {data?.reason}</p>
      {data?.counts && <div className="cleanup-counts">{Object.entries(data.counts).map(([key, count]) => <Status key={key}>{count} {labels[key]}</Status>)}</div>}
      <p ref={range} tabIndex={-1} role="status">{!data?.counts ? "Cleanup evidence unavailable; this is not an empty queue." : data.total ? `Page ${data.page} of ${data.pages} · ${data.total} matching obligations` : "No matching cleanup obligations."}</p>
      <div className="cleanup-inventory">{data?.items.map(item => <CleanupRow key={item.beadId} project={project} item={item} data={data} current={current} />)}</div>
      <div className="bead-actions"><button className="button" disabled={!data?.counts || data.page <= 1} onClick={() => change("page", String((data?.page ?? 1) - 1))}>Previous page</button>
        <button className="button" disabled={!data?.counts || data.page >= data.pages} onClick={() => change("page", String((data?.page ?? 1) + 1))}>Next page</button></div>
    </Panel></FocusBoundary>;
}
