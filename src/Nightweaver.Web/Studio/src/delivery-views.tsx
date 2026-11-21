import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Link } from 'react-router';
import { Status } from './primitives';
import { useObservation } from './use-observation';
import { operations } from './operations-browser';
import { InvocationController, emptyLog, isCurrent, runtimeState, deliveryProgress, type Factory, type Selection, type Slot } from './delivery-model';

const display = (value: unknown) => value == null || value === '' ? 'Unavailable' : String(value);
const time = (value?: string) => value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString() : 'Unavailable';
function Fields({ values }: { values: [string, unknown][] }) { return <dl className="delivery-fields">{values.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{display(value)}</dd></div>)}</dl>; }
export function RuntimeSummary({ project }: { project: string }) {
  const observation = useObservation<Factory>(project, 'factory');
  return <span>Runtime · {runtimeState(observation.observation?.data, observation.stale || observation.observation?.health !== 'fresh')}</span>;
}
export function DeliveryMetric({ project }: { project: string }) {
  const observation = useObservation<Factory>(project, 'factory');
  const data = observation.observation?.data;
  const pool = data?.snapshot?.delivery;
  const state = runtimeState(data, observation.stale || observation.observation?.health !== 'fresh');
  return <div className="metric"><span className="eyebrow">Delivery</span><strong>{pool ? pool.slots.filter(slot => slot.status !== 'idle').length : '—'}</strong><small>{state === 'live' || state === 'idle' ? 'Observed occupied bays' : `Last observed · ${state}`}</small></div>;
}
export function DeliveryView({ project, compact = false, bead }: { project: string; compact?: boolean; bead?: string }) {
  const observation = useObservation<Factory>(project, 'factory');
  const data = observation.observation?.data;
  const snapshot = data?.snapshot;
  const state = runtimeState(data, observation.stale || observation.observation?.health !== 'fresh');
  const [selection, select] = useState<Selection>();
  const [error, setError] = useState('');
  const heading = useRef<HTMLHeadingElement>(null);
  const host = useRef<HTMLDivElement>(null);
  const focused = useRef<HTMLElement | null>(null);
  useLayoutEffect(() => { if (focused.current && !focused.current.isConnected && document.activeElement === document.body) heading.current?.focus(); });
  const open = (slot: Slot) => select({ runtime: snapshot!.runtime_id, slot: structuredClone(slot) });
  return <div ref={host} onFocusCapture={e => { focused.current = e.target as HTMLElement; }}>
    {!compact && <div className="page-heading"><p className="eyebrow">Current project / Studio</p><h1 id="factory-title" ref={heading} tabIndex={-1}>Delivery floor</h1><p>Parallel Delivery. One careful merge at a time. Activity below is observed execution; tracker status is shown in the Bead Workspace.</p></div>}
    {compact && <h3 id="factory-title" ref={heading} tabIndex={-1}>{bead ? `Output for ${bead}` : "Runtime activity"}</h3>}
    <div className="delivery-health" id="factory-status" data-state={state} role="status"><Status tone={state === 'live' || state === 'idle' ? 'accent' : 'warn'}>{state}</Status>
      <span>{snapshot ? `Observed ${time(snapshot.observed_at)}` : 'Runtime observations unavailable'}</span>
      <button className="button" onClick={() => { setError(''); void operations.refresh([{ projectId: project, key: 'factory' }]).catch(e => setError(String(e))); }}>{observation.refreshing ? 'Refresh runtime again' : 'Refresh runtime'}</button>
    </div>
    {(observation.error || observation.observation?.error || error) && <p role="alert">{error || observation.error || observation.observation?.error?.message}</p>}
    {state !== 'live' && state !== 'idle' && snapshot && <p>Showing last observed activity. Current execution is unconfirmed.</p>}
    {!snapshot && <p>No runtime snapshot is available. Tracker ownership does not establish execution.</p>}
    {snapshot && <>
      {!compact && <details><summary>Runtime diagnostics</summary><Fields values={[["Runtime", snapshot.runtime_id], ["Runtime status", snapshot.runtime_status], ["Target Repository", snapshot.target_repository], ["Observation reason", data?.reason]]} /></details>}
      {([['Planning', snapshot.planning], ['Delivery', snapshot.delivery], ['Merger', snapshot.merger]] as const).map(([name, pool]) => <section className="delivery-pool" key={name} aria-label={`${name} pool`}>
        <div className="delivery-pool-heading"><h3>{name === 'Merger' ? 'Singleton Merger' : name}</h3><span>{pool ? `${pool.slots.filter(s => s.status !== 'idle').length} of ${pool.capacity} occupied · ${pool.capacity === 0 ? 'Pool disabled' : pool.slots.filter(s => s.status === 'idle').length + ' idle'}` : 'Observations unavailable'}</span></div>
        <div className="delivery-bays">{pool?.slots.filter(slot => bead ? slot.invocation?.bead_id === bead : !compact || slot.status !== 'idle').slice(0, compact ? 3 : undefined).map(slot => <article className="delivery-bay" data-slot-id={slot.slot_id} data-status={slot.status} data-outcome={slot.invocation?.status} key={snapshot.runtime_id + ':' + slot.slot_id}>
          <div className="delivery-pool-heading"><span className="eyebrow">{slot.slot_id}</span><Status>{state === 'live' || state === 'idle' ? '' : 'Last observed · '}{slot.status}</Status></div>
          {slot.status !== 'idle' ? <BayWork slot={slot} project={project} state={state} delivery={name === 'Delivery'} /> : <p>{state === 'idle' || state === 'live' ? 'Available for admission' : 'Last observed idle'}{slot.invocation ? ' · Retained invocation: ' + slot.invocation.status : ''}</p>}
          {!compact && (slot.status !== "idle" || slot.invocation) && <details><summary>Diagnostics</summary><Fields values={[["Stage", slot.stage], ["Started", time(slot.invocation?.started_at)], ["Deadline", time(slot.invocation?.deadline_at)], ["Repairs", `${display(slot.repair_cycles)} / ${display(slot.max_repair_cycles)}`], ["Diff additions / deletions", slot.diff?.status === 'available' ? `${display(slot.diff.added)} / ${display(slot.diff.deleted)}` : undefined], ["Invocation", slot.invocation?.invocation_id], ["Invocation outcome", slot.invocation?.status], ["CLI", slot.invocation?.cli], ["Configured model", slot.invocation?.configured_model], ["Observed model", slot.invocation?.actual_model], ["Effort", slot.invocation?.effort], ["PID", slot.invocation?.process_id], ["Exit code", slot.invocation?.exit_code], ["Branch", slot.branch], ["Worktree", slot.worktree_path], ["Diff status", slot.diff?.status], ["Binary files", slot.diff?.binary], ["Untracked files", slot.diff?.untracked]]} /></details>}
          {slot.invocation && <button className="button" data-open-telemetry-slot={slot.slot_id} data-runtime-id={snapshot.runtime_id} data-invocation-id={slot.invocation.invocation_id} onClick={() => open(slot)}>{slot.status === 'running' && slot.invocation.status === 'running' && state === 'live' ? 'Open live invocation' : 'Open retained invocation'}</button>}
        </article>)}</div>{compact && <p className="delivery-summary-note">{bead ? (pool?.slots.some(slot => slot.invocation?.bead_id === bead) ? "Exact observed invocation identity" : "No matching invocation is retained in this pool.") : "Up to three occupied bays shown. Open the floor for all bays and retained output."}</p>}</section>)}
    </>}
    {selection && <InvocationDialog selection={selection} current={isCurrent(data, selection)} close={() => { select(undefined); requestAnimationFrame(() => { const trigger = Array.from(host.current?.querySelectorAll<HTMLElement>('[data-open-telemetry-slot]') ?? []).find(node => node.dataset.runtimeId === selection.runtime && node.dataset.openTelemetrySlot === selection.slot.slot_id && node.dataset.invocationId === selection.slot.invocation?.invocation_id); (trigger ?? heading.current)?.focus({ preventScroll: true }); }); }} />}
  </div>;
}
function BayWork({ slot, project, state, delivery }: { slot: Slot; project: string; state: string; delivery: boolean }) {
  const progress = deliveryProgress(slot, state);
  return <>
    <h3 className="delivery-task-title">Title unavailable</h3>
    <div className="delivery-task-identity"><span>{project}</span><span aria-hidden="true"> / </span>{slot.bead_id ? <Link to={`/projects/${encodeURIComponent(project)}/beads/${encodeURIComponent(slot.bead_id)}`}>{slot.bead_id}</Link> : <span>Bead unavailable</span>}</div>
    <p className="delivery-activity"><span className="delivery-type">Type unavailable</span> {slot.role ?? 'Role unavailable'} · {slot.invocation?.started_at ? `Started ${time(slot.invocation.started_at)}` : 'Start time unavailable'}</p>
    <p className="delivery-outcome">Invocation: {slot.invocation?.status ?? 'Unavailable'}</p>
    {delivery && <div className="delivery-progress" data-neutral={progress.neutral}>
      <div className="delivery-segments" aria-hidden="true">{[0, 1, 2, 3].map(index => <span key={index} data-position={index === progress.index ? 'current' : index < progress.index ? 'preceding' : 'remaining'} />)}</div>
      <div className="delivery-progress-caption"><span>{progress.caption}</span>{progress.position && <span className="delivery-stage-position">{progress.position}</span>}</div>
    </div>}
  </>;
}

function InvocationDialog({ selection, current, close }: { selection: Selection; current: boolean; close(): void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const output = useRef<HTMLPreElement>(null);
  const [log, setLog] = useState(emptyLog);
  const [follow, setFollow] = useState(true);
  const position = useRef(0);
  useEffect(() => {
    dialog.current?.showModal();
    const controller = new InvocationController(url => new EventSource(url), setLog);
    controller.open(selection);
    return () => controller.close();
  }, [selection]);
  useLayoutEffect(() => { if (output.current) output.current.scrollTop = follow ? output.current.scrollHeight : position.current; }, [log.text, follow]);
  const i = selection.slot.invocation!;
  return <dialog id="invocation-dialog" className="invocation-dialog" ref={dialog} aria-labelledby="invocation-title" onKeyDown={e => {
    if (e.key !== 'Tab') return;
    const controls = Array.from(dialog.current?.querySelectorAll<HTMLElement>('button:not(:disabled), summary, [tabindex="0"]') ?? []).filter(node => node.getClientRects().length);
    if (e.shiftKey && document.activeElement === controls[0]) { e.preventDefault(); controls.at(-1)?.focus(); }
    else if (!e.shiftKey && document.activeElement === controls.at(-1)) { e.preventDefault(); controls[0]?.focus(); }
  }} onCancel={e => { e.preventDefault(); close(); }}>
    <div className="dialog-heading"><h2 id="invocation-title">{selection.slot.role ?? 'Retained invocation'} · {i.bead_id}</h2><button id="invocation-close" className="button" autoFocus onClick={close}>Close</button></div>
    <div className="invocation-body"><p id="invocation-status" role="status">{log.status}</p>{!current && <p role="status">Selected invocation is no longer current. Showing retained output; following only the selected stream.</p>}
      <details><summary>Invocation identity and diagnostics</summary><div id="invocation-metadata"><Fields values={[["Runtime", selection.runtime], ["Slot", selection.slot.slot_id], ["Invocation", i.invocation_id], ["Bead", i.bead_id], ["CLI", i.cli], ["Configured model", i.configured_model], ["Observed model", i.actual_model], ["Worktree", selection.slot.worktree_path], ["Branch", selection.slot.branch]]} /></div></details>
      <p id="invocation-truncation" hidden={!log.truncated}>Earlier output omitted. Showing at most 2,000 lines / 256 KiB.</p>
      <pre id="invocation-output" ref={output} tabIndex={0} aria-label="Invocation output" onScroll={() => { const node = output.current!; position.current = node.scrollTop; if (node.scrollHeight - node.scrollTop - node.clientHeight >= 48) setFollow(false); }}>{log.text}</pre>
    </div><div className="telemetry-actions"><button id="invocation-pause" className="button" onClick={() => setFollow(!follow)}>{follow ? 'Pause follow' : 'Resume follow'}</button><button id="invocation-latest" className="button" onClick={() => setFollow(true)}>Jump to latest</button></div>
  </dialog>;
}
