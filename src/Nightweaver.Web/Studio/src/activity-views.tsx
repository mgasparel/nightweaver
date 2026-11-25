import { useEffect, useLayoutEffect, useRef, useState } from 'react';
import { Link, useLocation, useNavigate } from 'react-router';
import { Panel, Status } from './primitives';
import { ActivityBuffer, atOffset, cursorParts, EXPANSION_LIMIT, levelOf, observationDelay, ROW_LIMIT, rowId, safeBead, safeDetail, selection, type ActivityRow, type ActivityStatus, type Detail, type Page } from './activity-model';

const stamp = (value?: string) => value && Number.isFinite(Date.parse(value)) ? new Date(value).toLocaleString() : 'Unavailable';
const words = (value: string) => value.replace(/([a-z])([A-Z])/g, '$1 $2');

export function ActivityView({ project }: { project: string }) {
  const location = useLocation();
  const filters = selection(location.search);
  const prior = useRef(location.search);
  useLayoutEffect(() => {
    if (prior.current !== location.search) document.querySelector<HTMLButtonElement>('.activity-filters button')?.focus({ preventScroll: true });
    prior.current = location.search;
  }, [location.search]);
  return <ActivitySession key={`${project}:${filters.level}:${filters.bead}`} project={project} filters={filters} />;
}
function ActivitySession({ project, filters }: { project: string; filters: { level: string; bead: string } }) {
  const navigate = useNavigate();
  const [buffer] = useState(() => new ActivityBuffer());
  const [, render] = useState(0);
  const [status, setStatus] = useState<ActivityStatus>();
  const [connection, setConnection] = useState('Connecting');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [olderBound, setOlderBound] = useState<number>();
  const [now, setNow] = useState(Date.now());
  const viewport = useRef<HTMLDivElement>(null);
  const anchor = useRef<{ id: string; top: number } | undefined>(undefined);
  const heading = useRef<HTMLHeadingElement>(null);
  const source = useRef<EventSource | undefined>(undefined);
  const action = useRef<(older?: boolean) => Promise<void>>(async () => {});
  const update = () => render(value => value + 1);
  const pause = () => { buffer.paused = true; update(); };
  const rememberAnchor = () => {
    const host = viewport.current;
    if (!host) return;
    const row = [...host.querySelectorAll<HTMLElement>('[data-activity-id]')].find(node => node.getBoundingClientRect().bottom > host.getBoundingClientRect().top);
    anchor.current = row ? { id: row.dataset.activityId!, top: row.getBoundingClientRect().top } : undefined;
  };
  useLayoutEffect(() => {
    const host = viewport.current;
    if (!host) return;
    if (anchor.current) {
      const saved = anchor.current;
      const row = [...host.querySelectorAll<HTMLElement>('[data-activity-id]')].find(node => node.dataset.activityId === saved.id);
      if (row) host.scrollTop += row.getBoundingClientRect().top - saved.top;
      anchor.current = undefined;
    } else if (!buffer.paused) host.scrollTop = host.scrollHeight;
  });
  useEffect(() => {
    const controller = new AbortController();
    let stopped = false, timer: ReturnType<typeof setTimeout> | undefined;
    let detailBatch = new Map<string, Detail>();
    let lower: number | undefined;
    const resetOlder = (cursor?: string) => {
      const journal = cursorParts(cursor)?.journal;
      if (journal && buffer.displayedJournal && journal !== buffer.displayedJournal) {
        lower = undefined; setOlderBound(undefined);
      }
    };
    const base = `/api/activity/v1/projects/${encodeURIComponent(project)}`;
    const query = (cursor?: string) => {
      const values = new URLSearchParams();
      if (filters.level) values.set('level', filters.level);
      if (filters.bead) values.set('bead', filters.bead);
      if (cursor) values.set('cursor', cursor);
      return values.toString();
    };
    const read = async (cursor?: string): Promise<Page> => {
      const response = await fetch(`${base}/history?${query(cursor)}`, { signal: AbortSignal.any([controller.signal, AbortSignal.timeout(10000)]), cache: 'no-store' });
      if (!response.ok) throw new Error(`Activity history unavailable (${response.status}). Use Return to latest to retry.`);
      return response.json();
    };
    function close() { source.current?.close(); source.current = undefined; clearTimeout(timer); }
    function connect() {
      close();
      if (stopped || document.hidden) return;
      setConnection('Connecting');
      const stream = new EventSource(`${base}/events?${query(buffer.received ?? buffer.cursor)}`);
      source.current = stream;
      stream.addEventListener('open', () => { if (!stopped) { setConnection('Connected'); setError(''); } });
      stream.addEventListener('status', event => {
        if (stopped) return;
        const next: ActivityStatus = JSON.parse((event as MessageEvent).data);
        buffer.status(next); setStatus(next); setNow(Date.now());
        detailBatch = new Map(next.details.slice(0, 100).map(detail => [detail.id, detail]));
        update();
      });
      stream.addEventListener('activity', event => {
        if (stopped) return;
        const row: ActivityRow = JSON.parse((event as MessageEvent).data);
        resetOlder(rowId(row));
        buffer.accept(row, detailBatch.get(rowId(row))); update();
      });
      stream.addEventListener('cursor', event => {
        const cursor = (event as MessageEvent).lastEventId;
        if (!stopped && cursorParts(cursor)) { resetOlder(cursor); buffer.advance(cursor); update(); }
      });
      stream.onerror = () => {
        close();
        if (!stopped) { setConnection('Reconnecting'); timer = setTimeout(connect, 3000); }
      };
    }
    let working = false;
    async function load(older = false) {
      if (working) return;
      working = true; close(); setBusy(true); setError('');
      // Only the request starts follow. Later interaction or suspension owns pause.
      buffer.paused = older || document.hidden;
      try {
        let page: Page;
        if (older && lower != null && buffer.cursor) {
          const bound = lower;
          const start = Math.max(0, bound - Math.min(100, ROW_LIMIT - buffer.rows.length));
          page = await read(atOffset(buffer.cursor, start));
          if (cursorParts(page.cursor)?.journal !== buffer.displayedJournal) {
            resetOlder(page.cursor); buffer.status(page); buffer.gap = true;
            setStatus(page); update(); return;
          }
          // A filtered scan may pass the older window. Keep only its earlier rows.
          const historyRows = page.records.filter(row => row.position.offset <= bound);
          const historyDetails = [...page.details];
          for (let turn = 0; turn < 100 && page.cursor && Number(cursorParts(page.cursor)!.offset) < bound; turn++) {
            const previous = page.cursor;
            page = await read(previous);
            if (cursorParts(page.cursor)?.journal !== buffer.displayedJournal) {
              resetOlder(page.cursor); buffer.status(page); buffer.gap = true;
              setStatus(page); update(); return;
            }
            historyRows.push(...page.records.filter(row => row.position.offset <= bound));
            historyDetails.push(...page.details);
            if (page.cursor === previous) break;
          }
          rememberAnchor();
          lower = buffer.page({ ...page, records: historyRows, details: historyDetails }, true) ?? start;
        } else {
          // Resume after the last displayed scan. This detects loss over the unseen range.
          let resetHistory = false;
          try { page = await read(buffer.cursor); }
          catch (failure) {
            if (!buffer.cursor || controller.signal.aborted) throw failure;
            page = await read(); buffer.gap = true;
            resetHistory = true;
          }
          if ((!buffer.cursor || resetHistory) && page.cursor && page.loadedThrough != null) {
            const start = Math.max((page.oldest ?? 1) - 1, page.loadedThrough - 100);
            if (start > (page.oldest ?? 1) - 1) page = await read(atOffset(page.cursor, start));
            lower = start;
          }
          if (stopped) return;
          if (buffer.paused) {
            // Keep displayed rows, evidence and cursor for the next explicit return.
            buffer.page(page); setStatus(page); setNow(Date.now()); update(); return;
          }
          if (resetHistory) { buffer.rows = []; buffer.details.clear(); buffer.cursor = undefined; }
          buffer.pending = 0; buffer.expanded.clear();
          resetOlder(page.cursor);
          buffer.page(page);
          // Each turn is finite. SSE continues cursor catch-up, including initial source loading.
          lower ??= (page.oldest ?? 1) - 1;
        }
        if (stopped) return;
        setOlderBound(lower); setStatus(page); setNow(Date.now()); update();
      } catch (failure) {
        if (!stopped) { buffer.paused = true; setError(String(failure)); update(); }
      } finally {
        working = false;
        if (!stopped) { setBusy(false); connect(); }
      }
    }
    action.current = load;
    const visibility = () => {
      if (document.hidden) { buffer.paused = true; close(); setConnection('Suspended'); update(); }
      else connect();
    };
    const hide = () => { buffer.paused = true; close(); };
    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('pagehide', hide);
    window.addEventListener('pageshow', visibility);
    const clock = setInterval(() => setNow(Date.now()), 2000);
    void load();
    return () => { stopped = true; controller.abort(); close(); clearInterval(clock); document.removeEventListener('visibilitychange', visibility); window.removeEventListener('pagehide', hide); window.removeEventListener('pageshow', visibility); };
  // One owner per exact route selection. Route replacement disposes streams and reads.
  }, [buffer, project, filters.level, filters.bead]);
  const sourceOld = !status?.observedAt || now - Date.parse(status.observedAt) > 60000;
  const runtimeOld = !status?.runtimeObservedAt || now - Date.parse(status.runtimeObservedAt) > 10000;
  return <div className="activity-view">
    <div className="page-heading"><p className="eyebrow">Current project / Studio</p><h1 tabIndex={-1} ref={heading}>Activity</h1><p>Live Project observations, in journal order. <Link to={`/projects/${encodeURIComponent(project)}/catch-up`}>Catch up after an absence</Link>.</p></div>
    <Panel title="Project journal" accessory={<Status tone={buffer.paused ? 'warn' : 'accent'}>{buffer.paused ? 'Browsing paused' : 'Following latest'}</Status>}>
      <form className="activity-filters" onSubmit={event => {
        event.preventDefault(); const data = new FormData(event.currentTarget); const bead = String(data.get('bead') ?? '').trim();
        if (bead && !safeBead(bead)) { setError('Use an exact Bead ID with at most 128 letters, digits, dots, hyphens or underscores.'); return; }
        const query = new URLSearchParams(); const level = String(data.get('level') ?? '');
        if (level) query.set('level', level); if (bead) query.set('bead', bead);
        navigate(`?${query}`);
      }}><label>Record level<select aria-label="Record level" name="level" defaultValue={filters.level}><option value="">All levels</option><option value="work">Work</option><option value="warning">Warning</option><option value="error">Error</option></select></label><label>Exact Bead ID<input name="bead" maxLength={128} defaultValue={filters.bead} placeholder="All Beads" /></label><button className="button">Apply filters</button></form>
      <div className="activity-toolbar"><button className="button" onClick={pause} disabled={buffer.paused}>Pause live follow</button><button className="button" disabled={busy} onClick={() => void action.current()}>{busy ? 'Loading history…' : 'Return to latest'}</button><span role="status">{buffer.pending ? `${buffer.pending > ROW_LIMIT ? '500+' : buffer.pending} new matching records` : buffer.paused ? 'Position held. Live arrivals do not move focus.' : 'Live follow is on.'}</span></div>
      <p className="activity-health">Transport: {connection} · Journal: {status ? sourceOld && status.journalHealth === 'Fresh' ? 'Stale' : status.journalHealth : 'Unknown'} · Runtime: {status ? runtimeOld && status.runtimeHealth === 'Live' ? 'Stale' : status.runtimeHealth : 'Unknown'}</p>
      {error && <p role="alert">{error}</p>}
      {buffer.replaced && <p className="activity-notice">Runtime replaced. Prior records retain their original runtime identity.</p>}
      {buffer.gap && <p className="activity-notice">History gap. Retention, source loss or client limits removed part of the record. The displayed position stays under your control.</p>}
      <div className="activity-retention"><span>{buffer.rows.length} / {ROW_LIMIT} client rows · {buffer.expanded.size} / {EXPANSION_LIMIT} evidence sections</span><span>Retained offsets: {status?.oldest ?? 'unknown'}–{status?.newest ?? 'unknown'}{status?.loading ? ' · History still loading' : ''}</span><span>Journal observed: {stamp(status?.observedAt)}</span></div>
      <button className="button activity-older" disabled={busy || olderBound == null || olderBound <= (status?.oldest ?? 1) - 1 || buffer.rows.length >= ROW_LIMIT} onClick={() => void action.current(true)}>Load older records</button>
      {!buffer.rows.length && <p className="bead-empty">{!status || ['Unavailable', 'Unknown'].includes(status.journalHealth) ? 'Activity source unavailable. Other console views remain available.' : status.loading ? 'Loading retained history…' : 'No matching records in the loaded history.'}</p>}
      <div className="activity-scroll" ref={viewport} tabIndex={0} role="region" aria-label="Activity records" onFocusCapture={event => { if ((event.target as HTMLElement).closest('[data-activity-id]')) pause(); }} onWheel={event => { if (event.deltaY < 0) pause(); }} onKeyDown={event => { if (['PageUp', 'ArrowUp', 'Home'].includes(event.key)) pause(); }} onScroll={event => { if (!buffer.paused && event.currentTarget.scrollHeight - event.currentTarget.scrollTop - event.currentTarget.clientHeight > 48) pause(); }}>
        <ol className="activity-rows">{buffer.rows.map(row => <ActivityItem key={rowId(row)} row={row} project={project} detail={buffer.details.get(rowId(row))} open={buffer.expanded.has(rowId(row))} disabled={!buffer.expanded.has(rowId(row)) && buffer.expanded.size >= EXPANSION_LIMIT} toggle={() => { buffer.toggle(rowId(row)); update(); }} />)}</ol>
      </div>
      {buffer.rows.length >= ROW_LIMIT && <p className="activity-notice">Client row limit reached. Use a Bead or level filter to browse a smaller range.</p>}
      <p className="activity-footnote">This journal has finite retention. Observations grant no lifecycle authority. <Link to={`/projects/${encodeURIComponent(project)}/delivery`}>Invocation output and diagnostics</Link> remain on the Delivery floor and in each Bead Workspace.</p>
    </Panel>
  </div>;
}
function ActivityItem({ row, project, detail, open, disabled, toggle }: { row: ActivityRow; project: string; detail?: Detail; open: boolean; disabled: boolean; toggle(): void }) {
  const links = safeDetail(detail, project, row);
  const [output, setOutput] = useState(false);
  return <li data-activity-id={rowId(row)} className={`activity-row ${levelOf(row)}`}>
    <div className="activity-row-heading"><time dateTime={row.observedAt}>{stamp(row.observedAt)}</time><Status>{row.phase === 'Qa' ? 'QA' : row.phase}</Status><Status tone={levelOf(row) === 'work' ? 'neutral' : 'warn'}>{words(row.strength)} · {levelOf(row)}</Status>{row.bead && <Link to={`/projects/${encodeURIComponent(project)}/beads/${encodeURIComponent(row.bead)}`}>{row.bead}</Link>}</div>
    <p className="activity-message">{row.message}</p>
    <button className="text-link" aria-expanded={open} disabled={disabled} onClick={toggle}>{open ? 'Hide evidence' : 'Show evidence'}</button>
    {open && <div className="activity-evidence"><dl><div><dt>Source</dt><dd>{row.source} · {row.sourceReference}</dd></div><div><dt>Source time</dt><dd>{stamp(row.sourceTime)} · {observationDelay(row)}</dd></div><div><dt>Source freshness at observation</dt><dd>{row.freshness}</dd></div><div><dt>Reason</dt><dd>{words(row.reason)}</dd></div>{row.count != null && <div><dt>Count / limit</dt><dd>{row.count} / {row.limit ?? 'Unavailable'}</dd></div>}<div><dt>Runtime / invocation</dt><dd>{row.position.runtime} / {row.invocation ?? 'Unavailable'}</dd></div><div><dt>Journal position</dt><dd>{rowId(row)}</dd></div></dl>
      <div className="activity-links">{links.workspace && <Link to={links.workspace}>Open source in Workspace</Link>}{links.pullRequest && <a href={links.pullRequest} target="_blank" rel="noreferrer">Open verified pull request</a>}{links.invocation && <button className="button" onClick={() => setOutput(!output)}>{output ? 'Close invocation output' : 'Open exact invocation output'}</button>}</div>
      {!links.workspace && !links.pullRequest && !links.invocation && <p>Additional evidence unavailable. Use the retained source identity for diagnosis.</p>}
      {output && links.invocation && <ExactOutput url={links.invocation} />}
    </div>}
  </li>;
}

// Reuse the existing bounded invocation transport. It keeps the exact selected identity.
import { InvocationController, emptyLog, type Selection } from './delivery-model';
function ExactOutput({ url }: { url: string }) {
  const [log, setLog] = useState(emptyLog);
  useEffect(() => {
    const target = new URL(url, window.location.origin);
    const selected: Selection = { runtime: target.searchParams.get('runtime_id')!, slot: { slot_id: target.searchParams.get('slot_id')!, status: 'unknown', invocation: { invocation_id: target.searchParams.get('invocation_id')!, bead_id: '', cli: 'Unavailable', effort: 'Unavailable', status: 'unknown' } } };
    const controller = new InvocationController(() => new EventSource(url), setLog);
    controller.open(selected);
    return () => controller.close();
  }, [url]);
  return <><p role="status">{log.status}{log.truncated ? ' · Earlier output omitted' : ''}</p><pre className="activity-output" tabIndex={0} aria-label="Exact invocation output">{log.text}</pre></>;
}
