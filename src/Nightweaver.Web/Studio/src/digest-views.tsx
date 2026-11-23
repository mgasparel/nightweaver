import { observationStatus } from "./operations";
import { Component, useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { Link, useSearchParams } from "react-router";
import { beadPath, safeEvidenceUrl } from "./bead-model";
import { completedOutcomes, digestWithSections, DigestCheckpoints, digestWindow, visibleThreads, windows, type BaseDigest, type DigestMerges, type Digest, type DigestItem } from "./digest-model";
import { operations } from "./operations-browser";
import { Status } from "./primitives";
import { useObservation } from "./use-observation";

const checkpoints = new DigestCheckpoints(window.fetch.bind(window), () => { void operations.invalidateAfterCommand("checkpoint").catch(() => {}); });
const labels: Record<string, string> = { auto: "Automatic", since_caught_up: "Since caught up", "24h": "24 hours", "7d": "7 days", "30d": "30 days", all: "All time" };
const stamp = (value: string) => new Date(value).toLocaleString();

function useDigest(project: string, resource: string) {
  const base = useObservation<BaseDigest>(project, resource);
  const merges = useObservation<DigestMerges>(project, resource.replace("base:digest:", "digest-merges:"));
  const elapsed = merges.receivedAt === undefined ? Infinity : Math.max(0, performance.now() - merges.receivedAt);
  return { ...base, observation: base.observation && { ...base.observation,
    data: base.observation.data ? digestWithSections(base.observation.data, merges.observation?.data
      ?? (merges.error || merges.observation?.error ? {sections: {}, unavailableState: "failed"} : undefined), elapsed) : null } };
}

/** Read DOM geometry before React moves keyed threads; restore the same focused anchor. */
class DigestFocus extends Component<{ children: ReactNode }> {
  host = { current: null as HTMLDivElement | null };
  focused: HTMLElement | null = null;
  getSnapshotBeforeUpdate() {
    const node = this.focused;
    return node && document.activeElement === node ? { node, top: node.getBoundingClientRect().top } : null;
  }
  componentDidUpdate(_props: unknown, _state: unknown, snapshot: { node: HTMLElement; top: number } | null) {
    if (!snapshot) return;
    if (!snapshot.node.isConnected) this.host.current?.querySelector<HTMLElement>("#digest-toggle")?.focus({ preventScroll: true });
    else window.scrollBy(0, snapshot.node.getBoundingClientRect().top - snapshot.top);
  }
  render() { return <div ref={node => { this.host.current = node; }} onFocusCapture={event => { this.focused = event.target as HTMLElement; }}>{this.props.children}</div>; }
}

export function DigestMetric({ project }: { project: string }) {
  const snapshot = useDigest(project, "base:digest:auto");
  const data = snapshot.observation?.data;
  return <div className="metric"><span className="eyebrow">Completed</span><strong>{data?.metrics.completedTotal ?? "—"}</strong>
    <small>{data ? `${labels[data.window]} · ${observationStatus(snapshot)}` : "Digest unavailable"}</small></div>;
}

export function CatchUpPreview({ project }: { project: string }) {
  const snapshot = useDigest(project, "base:digest:auto");
  const data = snapshot.observation?.data;
  const [focused, setFocused] = useState<string>();
  const preview = useRef<HTMLDivElement>(null);
  const active = useRef<HTMLElement | null>(null);
  useLayoutEffect(() => {
    if (active.current && !active.current.isConnected && document.activeElement === document.body) preview.current?.querySelector<HTMLElement>('h2')?.focus({preventScroll: true});
  });
  const outcomes = data ? completedOutcomes(data) : [];
  return <div className="digest-preview" ref={preview} onFocusCapture={event => { active.current = event.target as HTMLElement; }}>
    <div className="panel-heading"><h2 tabIndex={-1}>Completed outcomes</h2><span>{data ? labels[data.window] : "Period unavailable"}</span></div>
    <div className="panel-body">
      {(snapshot.stale || snapshot.observation?.health !== "fresh" || snapshot.dataState === "delayed" || snapshot.observation?.synchronization?.error || snapshot.observation?.synchronization?.journal === "catching-up") && <p role="status">{data ? "Showing last confirmed outcomes" : "Digest unavailable"} · {observationStatus(snapshot)}</p>}
      {(snapshot.error || snapshot.observation?.error) && <p role="alert">{snapshot.error || snapshot.observation?.error?.message}</p>}
      {data && <p>{data.metrics.completedTotal} completed Beads in this window.</p>}
      {data && !outcomes.length && <p>No completed outcomes in this observation.</p>}
      <ul className="digest-outcomes" onBlurCapture={event => { if (!event.currentTarget.contains(event.relatedTarget)) setFocused(undefined); }}>
        {outcomes.filter((item, index) => index < 3 || item.id === focused).map(item => <li key={item.id} onFocusCapture={() => setFocused(item.id)}>
          <span className="outcome-check" aria-hidden="true">✓</span><div><h3><Link to={beadPath(project, item.id)}>{item.title || item.id}</Link></h3>
            <p>{project} · {stamp(item.activityAt)} · {item.mergeEvidence?.state === "verified" ? "Merged" : "Closed"}</p>
            {item.mergeEvidence?.cleanupWarning && <p className="bead-notice">Cleanup needs attention: {item.mergeEvidence.cleanupWarning}</p>}
          </div>
        </li>)}
      </ul>
      <Link className="text-link" to={`/projects/${encodeURIComponent(project)}/catch-up`}>Read the outcomes →</Link>
    </div>
  </div>;
}

export function CatchUpView({ project, compact = false }: { project: string; compact?: boolean }) {
  const [params, setParams] = useSearchParams();
  const window = digestWindow(params.get("window"));
  const resource = `base:digest:${window}`;
  const snapshot = useDigest(project, resource);
  // Keep the last successful resource selected while a different filter loads
  // or fails. Both subscriptions still share the tab's bounded response cache.
  const [displayedResource, setDisplayedResource] = useState(resource);
  const displayed = useDigest(project, displayedResource);
  const data = displayed.observation?.data;
  useEffect(() => {
    if (snapshot.observation?.data && resource !== displayedResource) setDisplayedResource(resource);
  }, [snapshot.observation, resource, displayedResource]);
  const command = useSyncExternalStore(checkpoints.subscribe, useCallback(() => checkpoints.get(project), [project]));
  const [expanded, setExpanded] = useState(false);
  const [error, setError] = useState("");
  const saved = useRef(command.saved);
  const intent = useRef<string | undefined>(undefined);
  useEffect(() => {
    if (command.saved && saved.current !== command.saved) {
      saved.current = command.saved;
      if (intent.current !== window) return;
      intent.current = undefined;
      setExpanded(false);
      if (!compact) setParams(previous => { const next = new URLSearchParams(previous); next.set("window", "since_caught_up"); return next; });
    }
  }, [command.saved, compact, setParams, window]);
  const stale = snapshot.stale || snapshot.observation?.health !== "fresh";
  const refresh = () => { setError(""); void operations.refresh([resource, resource.replace("base:digest:", "digest-merges:")].map(key => ({ projectId: project, key }))).catch(() => setError("Refresh could not be confirmed. Try again.")); };
  return <DigestFocus>
    {!compact && <div className="page-heading"><p className="eyebrow">Current project / Studio</p><h1 tabIndex={-1}>Catch up</h1><p>Meaningful changes, connected to the work behind them.</p></div>}
    <section id="catch-up" className="studio-digest" data-generated-at={data?.generatedAt}>
      <div className="digest-overview">
        <div><p className="eyebrow">Your catch-up digest</p><h2>{data?.isCaughtUp ? "You’re caught up." : data ? `${data.metrics.completedTotal} completed · ${data.metrics.openOrUpdatedTotal} open or updated` : "Awaiting your digest"}</h2>
          {compact && <p className="digest-preview-scope">{data ? `${labels[data.window]} · Snapshot ${stamp(data.generatedAt)}` : "Reporting window unavailable"}. Expand for outcomes in this window.</p>}
          <p id="digest-checkpoint-badge">{data?.lastCaughtUpAt ? `Caught up through ${stamp(data.lastCaughtUpAt)}` : "No catch-up bookmark set"}</p></div>
        <div className="digest-summary-actions">          <button id="digest-refresh" className="button" onClick={refresh}>{snapshot.refreshing ? "Refreshing…" : "Refresh digest"}</button>
          <button id="digest-mark-caught-up" className="button primary" disabled={!data || command.pending || data.isCaughtUp} onClick={() => { if (data) { intent.current = window; void checkpoints.save(project, data); } }}>{command.pending ? "Saving…" : data?.isCaughtUp ? "Caught Up" : "Mark Caught Up"}</button>
        <button id="digest-toggle" className="button" aria-controls="digest-body" aria-expanded={expanded} onClick={() => setExpanded(!expanded)}>{expanded ? "Collapse" : "Expand"}</button></div>
      </div>
      <p id="digest-status" role="status">{resource !== displayedResource ? `Showing last confirmed ${data ? labels[data.window] : "digest"} while ${labels[window]} is awaiting a successful observation.` : stale ? "Showing last confirmed data when available · awaiting current observation" : ""}</p>
      {(error || snapshot.error || snapshot.observation?.error || (command.message && !command.saved && !command.pending)) && <div id="digest-error" role="alert" className="bead-notice"><p id="digest-error-text">{error || snapshot.error || snapshot.observation?.error?.message || command.message}</p><button id="digest-retry" className="text-link" onClick={refresh}>Retry refresh</button></div>}
      {command.saved && <p role="status">{command.message}</p>}
      <div id="digest-body" hidden={!expanded}>
        <div className="digest-toolbar"><div className="digest-windows" aria-label="Digest window">{windows.map(value => <button key={value} data-digest-window={value} className="button" aria-pressed={window === value} onClick={() => { const next = new URLSearchParams(params); next.set("window", value); setParams(next); }}>{labels[value]}</button>)}</div>

        </div>
        <p className="digest-observation">{data ? `Snapshot ${stamp(data.generatedAt)} · ${labels[data.window]}. Mark Caught Up saves this snapshot’s cutoff.` : "Digest unavailable. Refresh to try again."}</p>
        {data && <>
          <div className="digest-metrics">{[["completed", "Completed", data.metrics.completedTotal], ["threads", "Lineage threads", data.metrics.activeThreads], ["open", "Open / updated", data.metrics.openOrUpdatedTotal], ["waiting", "Waiting for guidance", data.metrics.waitingForGuidance]].map(([id, label, value]) => <div key={id}><strong id={`digest-metric-${id}`}>{value}</strong><span>{label}</span></div>)}</div>
          {data.isCaughtUp && <div id="digest-caught-up-bar" className="bead-notice"><p>No changes since your bookmark.</p><button id="digest-view-24h" className="text-link" onClick={() => { const next = new URLSearchParams(params); next.set("window", "24h"); setParams(next); }}>View last 24 hours</button></div>}
          <DigestContents key={displayedResource} project={project} data={data} />
        </>}
      </div>
      {compact && <Link className="text-link" to={`/projects/${encodeURIComponent(project)}/catch-up`}>Visit catch up →</Link>}
    </section>
  </DigestFocus>;
}

function More({ total, limit, all, label, onClick }: { total: number; limit: number; all: boolean; label: string; onClick: () => void }) {
  return total > limit ? <button className="digest-inline-toggle text-link" aria-expanded={all} onClick={onClick}>{all ? `Show fewer ${label}` : `Show ${total - limit} more ${label}`}</button> : null;
}
function Item({ project, item, context = "item", onFocus, onDisclosure }: { project: string; item: DigestItem; context?: string; onFocus?: () => void; onDisclosure?: (open: boolean) => void }) {
  const [expanded, setExpanded] = useState(false);
  const long = item.summary?.length > 140;
  const id = `digest-summary-${encodeURIComponent(context + ":" + item.id)}`;
  return <div className="digest-item-row" data-bead-id={item.id} onFocusCapture={onFocus}>
    <div className="digest-item-meta"><Status>{item.presentation?.role ?? "Role unavailable"} · Type: {item.type} · P{item.priority}</Status><Status>{item.activityKind === "context" ? `Root context (${item.status})` : (item.activityKind === "closed" ? item.outcomeLabel : item.presentation?.summary ?? item.outcomeLabel)}</Status>
      {item.fromId && <span>↳ {item.fromRelation ?? "Lineage"}: <Link to={beadPath(project, item.fromId)}>{item.fromId}</Link></span>}
      {item.mergeEvidence?.state === "verified" || item.mergeEvidence?.state === "stale" ? <>
        {item.mergeEvidence.pullRequest && safeEvidenceUrl(item.mergeEvidence.pullRequest) && <a href={item.mergeEvidence.pullRequest} target="_blank" rel="noreferrer">{item.mergeEvidence.state === "stale" ? "Previous merge evidence" : "Merged pull request"}</a>}
        {item.mergeEvidence.commitUrl && safeEvidenceUrl(item.mergeEvidence.commitUrl) && <a href={item.mergeEvidence.commitUrl} target="_blank" rel="noreferrer"><code>{item.mergeEvidence.mergeCommit?.slice(0, 7)}</code></a>}
      </> : <>{item.commits?.map(sha => <code key={sha}>{sha.slice(0, 7)}</code>)}{item.pullRequests?.map(pr => <span key={pr}>{pr}</span>)}</>}</div>
    <h4><Link to={beadPath(project, item.id)}>{item.id} · {item.title}</Link></h4>
    <p className="digest-item-time">{stamp(item.activityAt)}</p>
    {item.summary && <p id={id} className={`digest-summary${long && !expanded ? " is-clamped" : ""}`}>{item.summary}</p>}
    {item.mergeEvidence && item.mergeEvidence.state !== "verified" && <p className="digest-item-time">{item.mergeEvidence.state === "missing" ? "No host merge verification recorded." : `Merge verification ${item.mergeEvidence.state}.`}</p>}
    {item.mergeEvidence?.cleanupWarning && <p className="bead-notice">Cleanup needs attention: {item.mergeEvidence.cleanupWarning}</p>}
    {long && <button className="digest-inline-toggle text-link" aria-expanded={expanded} aria-controls={id} onClick={() => { setExpanded(!expanded); onDisclosure?.(!expanded); }}>{expanded ? "Show less" : "Show more"}</button>}
  </div>;
}

/** Keep only surviving focused/open items beyond the normal preview, not the entire history. */
function ItemList({ project, items, limit, label, context, id, className }: { project: string; items: DigestItem[]; limit: number; label: string; context?: string; id?: string; className?: string }) {
  const [all, setAll] = useState(false);
  const [focused, setFocused] = useState<string>();
  const [disclosed, setDisclosed] = useState(new Set<string>());
  const visible = all ? items : items.filter((item, index) => index < limit || item.id === focused || disclosed.has(item.id));
  return <>
    <div id={id} className={className} onBlurCapture={event => { if (!event.currentTarget.contains(event.relatedTarget)) setFocused(undefined); }}>
      {visible.map(item => <Item key={item.id} project={project} item={item} context={context} onFocus={() => setFocused(item.id)} onDisclosure={open => {
        setDisclosed(previous => { const next = new Set(previous); if (open) next.add(item.id); else next.delete(item.id); return next; });
      }} />)}
    </div>
    <More total={items.length} limit={all ? limit : visible.length} all={all} label={label} onClick={() => setAll(!all)} />
  </>;
}

function DigestContents({ project, data }: { project: string; data: Digest }) {
  const [expanded, setExpanded] = useState(new Set<string>());
  const [allThreads, setAllThreads] = useState(false);
  const [focused, setFocused] = useState<string>();
  const toggle = (ids: Set<string>, id: string) => { const next = new Set(ids); if (next.has(id)) next.delete(id); else next.add(id); return next; };
  const threads = visibleThreads(data, allThreads, expanded, focused);
  const standalone = [...data.standaloneCompleted, ...data.standaloneOpen];
  return <>
    {!data.threads.length && !standalone.length && <p id="digest-empty" className="bead-empty">No activity in this window.</p>}
    {!!data.threads.length && <section id="digest-threads-section"><h3>Connected work <span className="eyebrow">{data.threads.length} threads</span></h3><div id="digest-threads">
      {threads.map(thread => { const id = thread.root.id; const open = expanded.has(id); const activity = `digest-thread-${encodeURIComponent(id)}`;
        return <article key={id} className="digest-thread-card" data-root-bead-id={id} onFocusCapture={() => setFocused(id)}>
          <div className="digest-thread-header"><div><p className="eyebrow">{thread.root.presentation?.role ?? "Role unavailable"} · Type: {thread.root.type} · P{thread.root.priority} · {thread.root.activityKind === "context" ? `Origin (${thread.root.status})` : (thread.root.activityKind === "closed" ? thread.root.outcomeLabel : thread.root.presentation?.summary ?? thread.root.outcomeLabel)}</p>
            <h4 className="digest-thread-title"><Link to={beadPath(project, id)}>{id} · {thread.root.title}</Link></h4>
            <p className="digest-thread-context">{thread.completedCount} completed · {thread.openCount} open / updated</p></div>
            <button className="digest-inline-toggle digest-activity-toggle text-link" data-digest-focus={`activity:${id}`} aria-label={`${open ? "Hide" : "Show"} activity for ${id} (${thread.items.length} items)`} aria-controls={activity} aria-expanded={open} onClick={() => setExpanded(toggle(expanded, id))}>{open ? "Hide activity" : `Show activity (${thread.items.length})`}</button></div>
          <div id={activity} className="digest-thread-activity" hidden={!open}>{open && <>
            <Item project={project} item={thread.root} context={`root:${id}`} />
            <ItemList project={project} items={thread.items} limit={4} label="in thread" context={id} className="digest-thread-items" />
          </>}</div>
        </article>; })}
    </div><More total={data.threads.length} limit={5} all={threads.length > 5} label="threads" onClick={() => { const show = threads.length <= 5; setAllThreads(show); if (!show) { setExpanded(new Set()); setFocused(undefined); } }} /></section>}
    {!!standalone.length && <section id="digest-standalone-section"><h3>Standalone activity</h3><ItemList project={project} items={standalone} limit={6} label="standalone items" id="digest-standalone" /></section>}
  </>;
}
