import { useEffect, useState } from "react";
import { Link, useSearchParams } from "react-router";
import { Panel, Status } from "./primitives";
import { FocusBoundary, Freshness } from "./bead-views";
import { beadPath } from "./bead-model";
import { useObservation } from "./use-observation";
import { explorerQuery, views, type ExplorerData } from "./explorer-model";

export function Explorer({ project }: { project: string }) {
  const [params, setParams] = useSearchParams();
  const query = explorerQuery(params);
  return <FocusBoundary><div className="page-heading"><p className="eyebrow">Current project / Explorer</p>
    <h1 tabIndex={-1}>Beads</h1><p>Manually planned by you. Carried through Delivery by Nightweaver.</p></div>
    <form className="explorer-controls" onSubmit={event => {
      event.preventDefault();
      const form = new FormData(event.currentTarget);
      const next = new URLSearchParams();
      next.set("q", String(form.get("q") ?? "").trim()); next.set("view", String(form.get("view")));
      setParams(next);
    }}>
      <label>Search Beads<input key={params.get("q")} name="q" type="search" maxLength={200}
        defaultValue={params.get("q") ?? ""} placeholder="Title or Bead ID" /></label>
      <label>View<select key={params.get("view")} name="view" defaultValue={query.valid ? query.view : "ready"}>
        {views.map(view => <option key={view} value={view}>{view[0].toUpperCase() + view.slice(1)}</option>)}
      </select></label><button className="button primary" type="submit">Search project</button>
    </form>
    {!query.valid ? <p role="alert" className="bead-notice">Invalid Explorer query. Choose a view, a search of up to 200 characters and a page from 1 to 250. <Link to="?view=ready">Reset view</Link></p>
      : <ExplorerResults key={`${project}:${query.view}:${query.search}`} project={project} />}
  </FocusBoundary>;
}

function ExplorerResults({ project }: { project: string }) {
  const [params, setParams] = useSearchParams();
  const query = explorerQuery(params);
  const snapshot = useObservation<ExplorerData>(project, query.key);
  const data = snapshot.observation?.data;
  const [notice, setNotice] = useState("");
  const current = !snapshot.stale && snapshot.observation?.health === "fresh";
  useEffect(() => {
    if (!current || !data || data.page === query.page) return;
    setNotice(`Page ${query.page} no longer exists. Showing page ${data.page} after the inventory changed.`);
    const next = new URLSearchParams(params); next.set("page", String(data.page)); setParams(next, { replace: true });
  }, [current, data, query.page, params, setParams]);
  const pageLink = (page: number) => { const next = new URLSearchParams(params); next.set("page", String(page)); return `?${next}`; };
  return <Panel title="Project inventory" accessory={<Status>Tracker observation</Status>}>
    <Freshness snapshot={snapshot} project={project} resource={query.key} />
    <p className="explorer-policy">Active means tracker status in progress. Tracker ownership does not establish a running Agent. Runtime execution is shown on the Delivery floor.</p>
    {notice && <p className="bead-notice" role="status">{notice}</p>}
    {!data ? <p className="bead-empty" role="status">{snapshot.observation?.health === "unavailable" ? "Project inventory unavailable." : "Loading project inventory…"}</p> : <>
      <div className="explorer-pagination" aria-label="Explorer pages">
        <p role="status">{data.total} matching Beads · Page {data.page} of {data.pages}</p>
        <nav aria-label="Inventory pagination">{data.page > 1 && <Link className="button" to={pageLink(data.page - 1)}>Previous page</Link>}
          {data.page < data.pages && <Link className="button" to={pageLink(data.page + 1)}>Next page</Link>}</nav>
      </div>
      {data.total === 0 && <p className="bead-empty" role="status">No Beads match this view{query.search ? ` and “${query.search}”` : ""}. Try another filter or search.</p>}
      <ul className="explorer-list">{data.items.slice(0, 40).map(item => <li key={item.id}>
        <div className="explorer-row-heading"><span className="eyebrow">{item.id}</span><Status>P{item.priority} · {item.type}</Status></div>
        <Link className="explorer-title" to={`${beadPath(project, item.id)}?${params}`}>{item.title}</Link>
        <p>Tracker: {item.status} · {item.stage ?? "No stage"}{item.assignee && ` · Assigned to ${item.assignee}`}</p>
      </li>)}</ul>
      {data.pages > 1 && <nav className="explorer-pagination" aria-label="Inventory pagination bottom">
        <p>Page {data.page} of {data.pages} · {data.total} matching Beads</p><div>
          {data.page > 1 && <Link className="button" to={pageLink(data.page - 1)}>Previous page (bottom)</Link>}{" "}
          {data.page < data.pages && <Link className="button" to={pageLink(data.page + 1)}>Next page (bottom)</Link>}
        </div>
      </nav>}
    </>}
  </Panel>;
}
