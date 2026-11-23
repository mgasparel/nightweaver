import { createContext, useContext, useCallback, useEffect, useLayoutEffect, useRef, useState, useSyncExternalStore, type ReactNode } from "react";
import { Link, useLocation, useParams, useSearchParams } from "react-router";
import { Panel, Status } from "./primitives";
import { DeliveryView } from "./delivery-views";
import { operations, interactions } from "./operations-browser";
import { useObservation } from "./use-observation";
import { observationStatus, type Snapshot } from "./operations";
import { attentionFromBase, type AttentionBase, type PauseExplanation, attentionWarningSections, decisionSections, attentionPreview, beadPath, safeBeadId, survivingAttentionDisclosures, visibleAttention, type AttentionItem, type WorkspaceData, type Message, safeEvidenceUrl } from "./bead-model";
import { AttentionCommands } from "./attention-commands";
import { PlanningAuthorization } from "./product-views";
import { Relations } from "./workspace-relations";

const commands = new AttentionCommands(window.fetch.bind(window), () => {
  // Reconciliation errors already live in coordinator snapshots and Freshness.
  void operations.invalidateAfterCommand("tracker").catch(() => {});
});

// Render only through the existing locally vendored Markdown + sanitization policy.
type MarkdownEngine = { render(text: string): string; renderer: { rules: Record<string, (...args: never[]) => string> } };
declare global {
  interface Window {
    markdownit?: (options: Record<string, boolean>) => MarkdownEngine;
    DOMPurify?: { sanitize(html: string, options: Record<string, unknown>): DocumentFragment };
  }
}
function Markdown({ text }: { text: string }) {
  const host = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    const node = host.current!;
    if (!window.markdownit || !window.DOMPurify) { node.textContent = text; return; }
    const engine = window.markdownit({ html: false, linkify: false, typographer: false });
    engine.renderer.rules.image = () => "[image omitted]";
    const fragment = window.DOMPurify.sanitize(engine.render(text), { RETURN_DOM_FRAGMENT: true,
      ALLOWED_TAGS: ["a", "blockquote", "br", "code", "del", "em", "h2", "h3", "h4", "h5", "h6", "hr", "li", "ol", "p", "pre", "strong", "table", "tbody", "td", "th", "thead", "tr", "ul"],
      ALLOWED_ATTR: ["href", "title"] });
    for (const link of fragment.querySelectorAll("a")) {
      const href = link.getAttribute("href") ?? "";
      if (!safeEvidenceUrl(href)) link.replaceWith(...link.childNodes);
      else { link.target = "_blank"; link.rel = "noopener noreferrer"; }
    }
    const focused = node.contains(document.activeElement);
    node.replaceChildren(fragment);
    if (focused) node.focus();
  }, [text]);
  return <div ref={host} tabIndex={-1} className="bead-markdown" />;
}

function Heading({ title, children }: { title: string; children: ReactNode }) {
  return <div className="page-heading"><p className="eyebrow">Current project / Studio</p>
    <h1 tabIndex={-1}>{title}</h1><p>{children}</p></div>;
}

// Attention can link into the Workspace without fetching history or evidence targets.
const EvidenceHistory = createContext<{ path: string; comments?: WorkspaceData["comments"] } | null>(null);
function EvidenceSection({ section }: { section: Message["sections"][number] }) {
  const history = useContext(EvidenceHistory);
  if (section.commentId) {
    const found = history && (!history.comments || history.comments.some(row => row.id === section.commentId));
    return <div><p>{section.text}</p>{found
      ? <Link className="text-link" to={`${history.path}?${new URLSearchParams({ tab: "history", comment: section.commentId })}`}>View source comment {section.commentId}</Link>
      : <p className="bead-notice" role="status">Source comment {section.commentId} is unavailable in this history.</p>}</div>;
  }
  return safeEvidenceUrl(section.text)
    ? <p><a href={section.text} target="_blank" rel="noopener noreferrer">{section.text}</a></p>
    : <Markdown text={section.text} />;
}
function Evidence({ message, fallback }: { message?: Message | null; fallback: string }) {
  if (!message) return <Markdown text={fallback} />;
  return <div className="bead-evidence"><h3>{message.title}</h3>
    <p className="eyebrow">Source comment {message.sourceId} · {message.role ?? "Role unavailable"} · {message.stage ?? "Stage unavailable"}</p>
    <Markdown text={message.summary} />
    {message.sections.map((section, index) => section.details
      ? <details key={`${section.label}:${index}`}><summary>{section.label}</summary><EvidenceSection section={section} /></details>
      : <section key={`${section.label}:${index}`}><h3>{section.label}</h3><EvidenceSection section={section} /></section>)}
    <details><summary>Original source message</summary><pre className="bead-original" tabIndex={0} aria-label="Original source message">{message.original}</pre></details>
  </div>;
}

/** Prose is never parsed into choices: only the server-attributed context is shown. */
function DecisionContext({ item }: { item: AttentionItem }) {
  const history = useContext(EvidenceHistory);
  return <section className="decision-context" aria-label="Pause question and context">
    <div className="bead-row-meta"><Status tone="warn">{item.kind === "product-decision-required" ? "Decision needed" : "Technical recovery"}</Status>
      <span>{item.beadId} · {item.stage ?? "Stage unavailable"} · Repairs {item.repairCycles ?? "Unavailable"}/{item.maxRepairCycles}</span></div>
    <h2 tabIndex={-1}>{item.kind === "product-decision-required" ? "Your decision" : "Recovery context"}</h2>
    <Markdown text={item.question || item.diagnostic || "No attributable question is available. Review the source evidence before acting."} />
    {item.message && <><p className="eyebrow">{item.message.role ?? "Role unavailable"}</p>
      {item.message.summary !== item.question && item.message.summary !== item.diagnostic && <Markdown text={item.message.summary} />}</>}
    {decisionSections(item).map((section, index) => <section key={`${section.label}:${index}`}>
      <h3>{section.label}</h3><EvidenceSection section={section} /></section>)}
    {attentionWarningSections(item).map((section, index) => <section className="bead-notice" key={`${section.label}:${index}`}>
      <h3>{section.label}</h3><EvidenceSection section={section} /></section>)}
    {item.contextNotice && <p className="bead-notice">{item.contextNotice}</p>}
    {history && item.sourceCommentId && <Link className="text-link" to={`${history.path}?${new URLSearchParams({ tab: "history", comment: item.sourceCommentId })}`}>Source comment {item.sourceCommentId}</Link>}
  </section>;
}
function DecisionEvidence({ item }: { item: AttentionItem }) {
  return <details className="decision-evidence"><summary>Original evidence and additional context</summary>
    <Evidence message={item.message} fallback={item.question || item.diagnostic || "No source evidence available."} />
  </details>;
}

/** A removed focused disclosure/control returns to this surviving heading, never another Bead. */
export function FocusBoundary({ children }: { children: ReactNode }) {
  const host = useRef<HTMLDivElement>(null);
  const focused = useRef<HTMLElement | null>(null);
  useLayoutEffect(() => {
    if (focused.current && !focused.current.isConnected && document.activeElement === document.body) {
      const target = host.current?.querySelector<HTMLElement>("h1, h2[tabindex], [data-focus-fallback]")
        ?? document.querySelector<HTMLElement>("main h1");
      target?.focus();
    }
  });
  return <div ref={host} onFocusCapture={event => { focused.current = event.target as HTMLElement; }}>{children}</div>;
}

export function Freshness({ snapshot, project, resource }: { snapshot: Snapshot; project: string; resource: string }) {
  const [error, setError] = useState("");
  const envelope = snapshot.observation;
  return <div className="bead-freshness">
    <div role="status"><Status tone={snapshot.stale || envelope?.health !== "fresh" || envelope?.synchronization?.error || snapshot.dataState === "delayed" ? "warn" : "accent"}>
      {observationStatus(snapshot)}</Status>
      <span> {envelope?.observedAt ? `Observed ${new Date(envelope.observedAt).toLocaleString()}` : "No observation yet"}</span></div>
    <button className="button" onClick={() => { setError(""); operations.refresh([{ projectId: project, key: resource }]).catch(() => setError("Refresh could not be confirmed. Try Refresh again.")); }}>
      {snapshot.refreshing ? "Refresh again" : "Refresh view"}</button>
    {snapshot.connectionState === "disconnected" && <p role="status">Observation connection unavailable. Reconnecting automatically; other Workspaces may be using the observation capacity.</p>}
    {(error || snapshot.error || envelope?.error) && <p role="alert">{error || snapshot.error || envelope?.error?.message}</p>}
    <details><summary>Source freshness</summary><ul>{Object.entries(envelope?.sources ?? {}).map(([name, source]) =>
      <li key={name}>{name}: {source.health} · {source.observedAt ? new Date(source.observedAt).toLocaleString() : "Not observed"}{source.error && ` · ${source.error.message}`}</li>)}</ul></details>
  </div>;
}

function Guidance({ project, id, item, current }: { project: string; id: string; item?: AttentionItem | null; current: boolean }) {
  const [draft, setDraft] = useState(() => interactions.get(project, id));
  const [error, setError] = useState("");
  const [confirm, setConfirm] = useState(false);
  const cancel = useRef<HTMLButtonElement>(null);
  const abort = useRef<HTMLButtonElement>(null);
  const state = useSyncExternalStore(commands.subscribe, useCallback(() => commands.get(project, id), [project, id]));
  const blocked = !current || !item || state.pending || state.uncertain;
  const invalidDraft = draft.includes("\0") || new TextEncoder().encode(draft.trim()).length > 16 * 1024;
  useEffect(() => { if (confirm) cancel.current?.focus(); }, [confirm]);
  // A transport replacement temporarily blocks submission; it does not revoke
  // this exact Bead's confirmation or its focused Cancel control.
  useEffect(() => { if (!item?.canAbort) setConfirm(false); }, [item?.canAbort]);
  return <FocusBoundary><section className="guidance" aria-label={`Actions for ${id}`}>
    {item?.kind !== "product-decision-required" && <h2 tabIndex={-1}>Human guidance</h2>}
    <p>{item?.kind === "product-decision-required" ? "Resume permits ordinary scheduling for this Bead. It does not directly launch an Agent or override a paused Project. Save Note keeps the pause." : "Retry permits ordinary scheduling for this Bead; it does not launch an Agent directly or override a paused Project. The host checks fresh tracker state before every action."}</p>
    {(!current || !item) && <p className="bead-notice" role="status">The previous action is unavailable: this Bead resolved, was removed, or its current pause cannot be verified. Your draft stays with {id}.</p>}
    {item?.unavailableReason && <p className="bead-notice">{item.unavailableReason}</p>}
    {(item?.kind === "product-decision-required" || draft || !item) && <label>Guidance for {id}
      <textarea data-focus-fallback value={draft} rows={4} onChange={event => {
        try { interactions.set(project, id, event.target.value); setDraft(event.target.value); setError(""); }
        catch (reason) { setError(String(reason)); }
      }} /></label>}
    {draft && <button className="text-link" onClick={() => { interactions.discard(project, id); setDraft(""); setError(""); }}>Discard draft</button>}
    {error && <p role="alert">{error}</p>}
    {invalidDraft && <p role="alert">Guidance must be at most 16 KiB of UTF-8 text and contain no NUL characters. Your draft is retained.</p>}
    <div className="bead-actions">
      {item?.kind === "product-decision-required" && <>
        <button className="button primary" disabled={blocked || invalidDraft || !item.canDecide || !draft.trim()} onClick={() => void commands.submit(project, item, "resume", draft)}>Send &amp; Resume</button>
        <button className="button" disabled={blocked || invalidDraft || !item.canDecide || !draft.trim()} onClick={() => void commands.submit(project, item, "note_only", draft)}>Save Note</button>
      </>}
      {item?.kind !== "product-decision-required" && item && <>
        <button className="button primary" disabled={blocked || !item.canRetry} onClick={() => void commands.submit(project, item, "retry", "")}>Reset Budget &amp; Retry</button>
        <button ref={abort} className="button" disabled={blocked || !item.canAbort} onClick={() => setConfirm(true)}>Abort &amp; Close</button>
      </>}
    </div>
    {confirm && <div className="bead-notice" onKeyDown={event => { if (event.key === "Escape") { setConfirm(false); abort.current?.focus(); } }}>
      <p>Close {id} as an abort? This preserves pause state and repair budget. It does not merge or delete its branch or pull request.</p>
      <div className="bead-actions"><button ref={cancel} className="button" onClick={() => { setConfirm(false); abort.current?.focus(); }}>Cancel</button>
        <button className="button" disabled={blocked || !item?.canAbort} onClick={() => { setConfirm(false); if (item) void commands.submit(project, item, "abort", ""); }}>Confirm Abort &amp; Close</button></div>
    </div>}
    {state.message && <p role="status" className="bead-notice">{state.message}</p>}
    {state.uncertain && <button className="button" disabled={!current} onClick={() => commands.acknowledge(project, id)}>I reviewed current state and history</button>}
  </section></FocusBoundary>;
}

export function AttentionView({ project, compact = false }: { project: string; compact?: boolean }) {
  const snapshot = useObservation<AttentionBase>(project, "base:attention");

  const [selected, setSelected] = useState<string>();
  const [preview, setPreview] = useState<string>();
  const base = snapshot.observation?.data;
  useEffect(() => {
    if (compact && base && !base.items.some(row => row.id === preview)) setPreview(base.items[0]?.id);
  }, [compact, base, preview]);
  const explanationId = selected ?? (compact ? preview ?? base?.items[0]?.id : undefined);
  const row = base?.items.find(row => row.id === explanationId);
  const historyKey = row ? `section:${row.id}:history` : undefined;
  const history = useObservation<PauseExplanation>(project, historyKey);
  const historyCurrent = !history.stale && history.synchronized === true && history.observation?.health === "fresh";
  const items = base?.items.map(row => attentionFromBase(row, base.maxRepairCycles, history.observation?.data ?? undefined, historyCurrent));
  const retained = history.observation?.data?.beadId === explanationId ? history.observation?.data?.value?.attention : null;
  const explanationState = history.observation?.error || history.error ? "Pause explanation failed. Refresh to try again."
    : history.observation?.data && !historyCurrent ? "Pause explanation is stale. Current evidence is pending."
    : historyCurrent ? "No attributable pause explanation is available. Review the history before acting."
    : "Loading pause explanation. Question, role and pause time are unknown until history is confirmed.";
  const [limit, setLimit] = useState(8);
  const [focused, setFocused] = useState<string>();
  const [disclosed, setDisclosed] = useState(new Set<string>());
  useEffect(() => { setDisclosed(previous => survivingAttentionDisclosures(items, previous)); }, [items]);
  const reviewHeading = useRef<HTMLHeadingElement>(null);
  useEffect(() => { if (selected) reviewHeading.current?.focus(); }, [selected]);
  const item = items?.find(row => row.beadId === selected);
  const attentionWarning = snapshot.stale || snapshot.error || snapshot.observation?.health !== "fresh"
    || snapshot.dataState === "delayed" || snapshot.connectionState === "disconnected"
    || snapshot.observation?.synchronization?.error || snapshot.observation?.synchronization?.journal === "catching-up";
  // Keep the identity selected even after it leaves the live collection.
  return <FocusBoundary>
    {!compact && <Heading title="Attention">Decisions and recovery, with the evidence close at hand.</Heading>}
    {(!compact || attentionWarning) && <Freshness snapshot={snapshot} project={project} resource="base:attention" />}
    <div className={compact ? "" : "attention-board"}><div>
    {!compact && <p className="eyebrow attention-list-title">Paused Beads · {items?.length ?? "Unavailable"}</p>}
    {!items ? <p className="bead-empty">Attention is unavailable. Refresh to try again.</p> : items.length === 0
      ? <p className="bead-empty">No paused Beads in this observation. Runtime activity is reported separately.</p>
      : <div className={compact ? "attention-inventory" : "attention-layout"}>
        <section className="attention-inventory" aria-label="Paused Beads" onBlurCapture={event => { if (!event.currentTarget.contains(event.relatedTarget)) setFocused(undefined); }}>
          {visibleAttention(items, compact ? 3 : limit, focused, disclosed).map(row => <article key={row.beadId} onFocusCapture={() => setFocused(row.beadId)} className={`attention-row ${selected === row.beadId ? "selected" : ""}`}>
            {compact && <p className="attention-kicker">{row.kind === "product-decision-required" ? "Needs your decision" : "Recovery needs attention"} · {project}</p>}
            {!compact && <><div className="bead-row-meta"><span>{row.beadId}</span><Status tone="warn">{row.kind === "product-decision-required" ? "Decision needed" : "Recovery needed"}</Status></div>
            <h2><Link to={beadPath(project, row.beadId)}>{row.title}</Link></h2></>}
            {compact && <>
              <h2 className="attention-question">{attentionPreview(row.question || row.diagnostic || row.title)}</h2>
              {!row.sourceCommentId && <p role="status">{row.beadId === explanationId ? explanationState : "Pause explanation is not loaded. Open Review to read the evidence."}</p>}
              {row.beadId === explanationId && !row.sourceCommentId && retained && <HistoricalExplanation item={retained} />}
              {row.beadId !== explanationId && <p><button className="text-link" onClick={() => setPreview(row.beadId)}>Load pause explanation for {row.beadId}</button></p>}
              {decisionSections(row).filter(section => section.label === "Recommended answer").slice(0, 1).map(section => <p className="attention-recommendation" key={section.label}>{row.message?.role ?? "Source"} recommendation: {attentionPreview(section.text)}</p>)}
              {attentionWarningSections(row).map(section => <p className="bead-notice" key={section.label}>{section.text}</p>)}
              {row.unavailableReason && <p className="bead-notice">{row.unavailableReason}</p>}
              <Link className="button primary" aria-label={`Review ${row.beadId}`} to={beadPath(project, row.beadId)}>{row.kind === "product-decision-required" ? "Review decision" : "Review recovery"} <span aria-hidden="true">→</span></Link>
              {(row.question || row.diagnostic) && <details className="attention-context" open={disclosed.has(row.beadId)} onToggle={event => {
                const open = event.currentTarget.open;
                setDisclosed(previous => {
                  if (previous.has(row.beadId) === open) return previous;
                  const next = new Set(previous); if (open) next.add(row.beadId); else next.delete(row.beadId); return next;
                });
              }}>
                <summary>Read full context &amp; pause details</summary>
                <p>{row.question || row.diagnostic}</p>
                <p>{row.title} · {row.stage ?? "Stage unavailable"} · P{row.priority} · Repairs {row.repairCycles ?? "Unavailable"}/{row.maxRepairCycles}</p>
                <p>{row.sourceCommentId ? `Source comment ${row.sourceCommentId}` : "Source comment unavailable"}</p>
                {row.contextNotice && <p>{row.contextNotice}</p>}
              </details>}
            </>}
            {compact ? <div className="bead-row-meta"><span>{row.beadId}</span><span>{row.kind === "product-decision-required" ? "Decision waiting" : "Recovery waiting"}</span></div> : <p>{row.stage ?? "Stage unavailable"} · P{row.priority} · Repairs {row.repairCycles ?? "Unavailable"}/{row.maxRepairCycles}</p>}
            {!compact && <button className="text-link" aria-pressed={selected === row.beadId} onClick={() => setSelected(row.beadId)}>Review {row.beadId}</button>}
          </article>)}
          {!compact && items.length > limit && <button className="button" onClick={() => setLimit(value => value + 8)}>Show more paused Beads ({items.length - limit})</button>}
          {compact && <Link className="text-link" to={`/projects/${encodeURIComponent(project)}/attention`}>Open Attention Desk · {items.length} paused</Link>}
        </section>
      </div>}
    </div>
    {!compact && !selected && <Panel title="Choose a paused Bead"><p>Select Review to read evidence and prepare guidance, or open its Full Bead Workspace.</p></Panel>}
    {!compact && selected && <Panel title={`Review ${selected}`}>
      <h2 ref={reviewHeading} tabIndex={-1}>{item?.title ?? `Retained draft · ${selected}`}</h2>
      {historyKey && <Freshness snapshot={history} project={project} resource={historyKey} />}
      {item && !item.sourceCommentId && <p role="status">{explanationState}</p>}
      {item && !item.sourceCommentId && retained && <HistoricalExplanation item={retained} />}
      {item && <DecisionContext item={item} />}
      <Guidance key={selected} project={project} id={selected} item={item} current={snapshot.synchronized === true && !snapshot.stale && snapshot.observation?.health !== "unavailable" && !!item} />
      {item && <EvidenceHistory value={{ path: beadPath(project, selected) }}><DecisionEvidence item={item} /></EvidenceHistory>}
      <Link className="text-link" to={beadPath(project, selected)}>Open Full Bead Workspace and evidence</Link>
    </Panel>}
    </div>
    {compact && !attentionWarning && <details className="overview-observation"><summary>Observation details &amp; refresh</summary><Freshness snapshot={snapshot} project={project} resource="base:attention" /></details>}
  </FocusBoundary>;
}

function HistoricalExplanation({ item }: { item: AttentionItem }) {
  return <details className="attention-context"><summary>Retained pause explanation · historical</summary>
    <p>This evidence does not confirm the current pause or permit an action.</p>
    <DecisionContext item={item} /><DecisionEvidence item={item} />
  </details>;
}

function History({ data }: { data: WorkspaceData }) {
  const [count, setCount] = useState(8);
  const [params] = useSearchParams();
  const selected = params.get("comment");
  const location = useLocation();
  const target = data.comments.find(comment => comment.id === selected);
  const history = useRef<HTMLDivElement>(null);
  useLayoutEffect(() => {
    if (!selected || !target) return;
    const disclosure = Array.from(history.current?.querySelectorAll<HTMLDetailsElement>(".bead-comment") ?? [])
      .find(node => node.dataset.commentId === selected);
    if (disclosure) { disclosure.open = true; disclosure.querySelector<HTMLElement>(":scope > summary")?.focus(); }
  }, [selected, target?.id, location.key]);
  const active = data.comments.find(comment => comment.id === data.history.activeCommentId);
  // Active evidence is always first; ordinary long history stays bounded.
  const rows = data.comments.filter(comment => comment.id !== active?.id).slice(-count).reverse();
  const visible = [...(active ? [active] : []), ...rows];
  if (target && !visible.some(row => row.id === target.id)) visible.push(target);
  return <Panel title="History and evidence">
    <p>{data.comments.length} comments · Showing active evidence and {Math.min(rows.length, count)} recent comments.</p>
    {selected && !target && <p className="bead-notice" role="status">Source comment {selected} is unavailable in this history.</p>}
    <div ref={history} className="bead-history">{visible.map(comment => <details key={comment.id} data-comment-id={comment.id} className="bead-comment">
      <summary>{comment.id === active?.id ? "Active pause evidence · " : ""}{comment.author} · {new Date(comment.createdAt).toLocaleString()} · {comment.kind}</summary>
      <Evidence message={comment.message} fallback={comment.body} />
    </details>)}</div>
    {data.comments.length > rows.length + (active ? 1 : 0) && <button className="button" onClick={() => setCount(value => value + 8)}>Show 8 older comments</button>}
    {!data.comments.length && <p>No comments recorded.</p>}
  </Panel>;
}

export function BeadWorkspace({ project }: { project: string }) {
  const { beadId = "" } = useParams();
  if (!safeBeadId(beadId)) return <FocusBoundary>
    <Heading title={`Bead ${beadId}`}>Full Bead Workspace · current project</Heading>
    <WorkspaceTabs />
    <Panel title="Bead unavailable"><p>This Bead identifier cannot be queried. No observations or actions are available.</p></Panel>
  </FocusBoundary>;
  return <WorkspaceIdentity key={beadId} project={project} id={beadId} />;
}
function WorkspaceTabs() {
  const [params] = useSearchParams();
  const tab = params.get("tab") ?? "summary";
  return <nav className="workspace-tabs" aria-label="Workspace sections">{["summary", "history", "output"].map(value => {
    const next = new URLSearchParams(params); next.set("tab", value);
    return <Link key={value} to={`?${next}`} aria-current={tab === value || value === "summary" && !["history", "output"].includes(tab) ? "page" : undefined}>{value[0].toUpperCase() + value.slice(1)}</Link>;
  })}</nav>;
}
function WorkspaceIdentity({ project, id }: { project: string; id: string }) {
  const resource = `workspace:${id}`;
  const snapshot = useObservation<WorkspaceData>(project, resource);
  const [params] = useSearchParams();
  const tab = ["summary", "history", "output"].includes(params.get("tab") ?? "") ? params.get("tab")! : "summary";
  const data = snapshot.observation?.data;
  const valid = data?.bead.id === id ? data : null;
  const missing = snapshot.observation?.error?.code === "resource-missing";
  const [hadAttention, setHadAttention] = useState(false);
  useEffect(() => { if (valid?.attention) setHadAttention(true); }, [valid?.attention]);
  return <EvidenceHistory value={{ path: beadPath(project, id), comments: valid?.comments }}><FocusBoundary>
    <Heading title={valid?.bead.title ?? `Bead ${id}`}>{id} · Full Bead Workspace</Heading>
    <Link className="text-link" to={`/projects/${encodeURIComponent(project)}/beads?${params}`}>Back to Explorer</Link>
    <Freshness snapshot={snapshot} project={project} resource={resource} />
    <WorkspaceTabs />
    {missing && <p className="bead-notice" role="status">This Bead no longer exists. Retained details are historical; actions are unavailable.</p>}
    {!valid && <Panel title={missing ? "Bead unavailable" : "Waiting for Bead details"}><p>Current details cannot be read. Use Refresh to try again.</p></Panel>}
    {valid && <>
      {(valid.attention || hadAttention || interactions.get(project, id)) && <Panel title="Attention and guidance" className="workspace-guidance">
        {valid.attention && <DecisionContext item={valid.attention} />}
        <Guidance project={project} id={id} item={valid.attention} current={snapshot.synchronized === true && !missing && !snapshot.stale && snapshot.observation?.health !== "unavailable"} />
        {valid.attention && <DecisionEvidence item={valid.attention} />}
        <Link className="text-link" to={`?${new URLSearchParams({ ...Object.fromEntries(params), tab: "history" })}`}>Read active evidence and full history</Link>
      </Panel>}
      <div hidden={tab !== "summary"}><Panel title="Summary" accessory={<Status>{valid.bead.status}</Status>}>
        <div className="bead-facts"><span>{valid.bead.type} · P{valid.bead.priority}</span><span>{valid.bead.stage ?? "No stage"}</span>
          <span>Repairs {valid.bead.repairCycles ?? 0}</span><span>Assignee: {valid.bead.assignee ?? "Unassigned"}</span></div>
        <p className="bead-labels">{valid.bead.labels.join(" · ") || "No labels"}</p>
        {valid.presentation && <>
          <p className="bead-notice">{valid.presentation.role} · {valid.presentation.summary}</p>
          <details><summary>Workflow and source facts</summary>
            <dl className="bead-environment">{([
              ["Role", valid.presentation.role], ["Tracked stage", valid.presentation.trackedStage],
              ["Planning", valid.presentation.planning], ["Publication", valid.presentation.publication],
              ["Pause", valid.presentation.pause], ["Ownership", valid.presentation.ownership],
              ["Repairs used", valid.presentation.repairsUsed], ["Configured repair limit", valid.presentation.repairsMax],
              ["Diagnostic", valid.presentation.diagnostic]
            ] as const).map(([name, value]) =>
              <div key={name}><dt>{name}</dt><dd>{value ?? "Unavailable"}</dd></div>)}</dl>
            <p>These are tracker observations; ownership and stage do not establish running execution.</p>
            <pre className="bead-original" tabIndex={0} aria-label="Original tracker metadata">{valid.sourceMetadata ?? "No source metadata recorded."}</pre>
          </details>
        </>}
        <PlanningAuthorization project={project} data={valid} current={snapshot.synchronized === true && !missing && !snapshot.stale && snapshot.observation?.health !== "unavailable"} />
        {[["Description", valid.bead.description], ["Acceptance criteria", valid.bead.acceptanceCriteria]].map(([label, text]) => <section key={label}><h3>{label}</h3><Markdown text={text || "Not provided."} /></section>)}
        <details><summary>Design and notes</summary><Markdown text={valid.bead.design || "No design recorded."} /><Markdown text={valid.bead.notes || "No notes recorded."} /></details>
        <Relations data={valid} project={project} />
        <details><summary>Retained environment · {valid.environment.state}</summary><dl className="bead-environment">
          {([["Worktree", valid.environment.worktreePath], ["Branch", valid.environment.branch], ["HEAD", valid.environment.head],
            ["Git status", valid.environment.gitStatus], ["Environment observed", valid.environment.observedAt], ["Environment source", valid.environment.source],
            ["Process", valid.environment.processId], ["Process state", valid.environment.processState], ["Runtime observed", valid.environment.runtimeObservedAt],
            ["Runtime source", valid.environment.runtimeSource], ["Warning", valid.environment.warning]] as const)
            .map(([name, value]) => <div key={name}><dt>{name}</dt><dd>{String(value ?? "Unavailable")}</dd></div>)}</dl></details>
      </Panel></div>
      <div hidden={tab !== "history"}><History data={valid} /></div>
      <div hidden={tab !== "output"}><Panel title="Invocation output">{tab === "output" && <DeliveryView project={project} compact bead={id} />}</Panel></div>
    </>}
    {!valid && interactions.get(project, id) && <Guidance project={project} id={id} current={false} />}
  </FocusBoundary></EvidenceHistory>;
}
