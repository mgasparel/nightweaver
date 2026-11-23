import { useLayoutEffect, useRef, useState } from "react";
import { Link } from "react-router";
import { beadPath, type WorkspaceData } from "./bead-model";

const pageSize = 15;
const names: Record<string, Record<string, string>> = {
  "parent-child": { outgoing: "Parent", incoming: "Children" },
  blocks: { outgoing: "Blocked by", incoming: "Blocks" },
  "discovered-from": { outgoing: "Discovered from", incoming: "Discovered work" }
};

export function Relations({ data, project }: { data: Pick<WorkspaceData, "relations" | "relationObservation">; project: string }) {
  const [pages, setPages] = useState<Record<string, number>>({});
  const host = useRef<HTMLDetailsElement>(null);
  const focused = useRef<{ node: HTMLElement; group?: string } | null>(null);
  const groups = new Map<string, { name: string; rows: WorkspaceData["relations"] }>();
  for (const row of data.relations) {
    const key = JSON.stringify([row.type, row.direction]);
    if (!groups.has(key)) groups.set(key, { name: names[row.type]?.[row.direction] ?? `${row.type} · ${row.direction}`, rows: [] });
    groups.get(key)!.rows.push(row);
  }
  const current = (key: string, total: number) => Math.min(pages[key] ?? 0, Math.max(0, Math.ceil(total / pageSize) - 1));
  useLayoutEffect(() => {
    // Persist the clamped page so later growth cannot resurrect an invalid page.
    const next = { ...pages };
    let changed = false;
    for (const key of Object.keys(pages)) {
      const page = current(key, groups.get(key)?.rows.length ?? 0);
      if (page !== pages[key]) { next[key] = page; changed = true; }
    }
    if (changed) setPages(next);
    const previous = focused.current;
    if (!previous || previous.node.isConnected && !previous.node.matches(":disabled")) return;
    // Do not reclaim focus if the user has moved outside this inventory.
    if (document.activeElement !== document.body && document.activeElement !== previous.node) return;
    const group = Array.from(host.current?.querySelectorAll<HTMLElement>("[data-relation-group]") ?? [])
      .find(node => node.dataset.relationGroup === previous.group);
    const target = group?.querySelector<HTMLElement>("button:not(:disabled)")
      ?? group?.querySelector<HTMLElement>("[data-relation-range]")
      ?? host.current?.querySelector<HTMLElement>(":scope > summary");
    target?.focus();
  });
  return <details ref={host} className="workspace-relations" onFocusCapture={event => {
    const node = event.target as HTMLElement;
    focused.current = { node, group: node.closest<HTMLElement>("[data-relation-group]")?.dataset.relationGroup };
  }}><summary>Relations · {data.relations.length}</summary>
    <p>Incoming {data.relationObservation.incoming} · Outgoing {data.relationObservation.outgoing}</p>
    {!data.relations.length && <p>{data.relationObservation.incoming === "complete" && data.relationObservation.outgoing === "complete"
      ? "No recorded relations." : "Relation coverage is incomplete; no relations could be displayed."}</p>}
    {Array.from(groups, ([key, group]) => {
      const page = current(key, group.rows.length);
      const start = page * pageSize;
      return <section key={key} data-relation-group={key} aria-label={group.name}>
        <h3>{group.name} · {group.rows.length}</h3>
        <p data-relation-range tabIndex={-1} aria-live="polite">Showing {start + 1}–{Math.min(start + pageSize, group.rows.length)} of {group.rows.length}</p>
        <ul>{group.rows.slice(start, start + pageSize).map(row => <li key={row.id}>
          <Link to={beadPath(project, row.id)}>{row.id} · {row.title ?? "Title unavailable"}</Link> · {row.type} · {row.direction} · {row.role} · Type: {row.issueType ?? "Unavailable"} · {row.status ?? "Status unavailable"}
        </li>)}</ul>
        <div className="bead-actions">
          <button className="button" disabled={page === 0} onClick={() => setPages(value => ({ ...value, [key]: page - 1 }))}>Previous</button>
          <button className="button" disabled={start + pageSize >= group.rows.length} onClick={() => setPages(value => ({ ...value, [key]: page + 1 }))}>Next</button>
        </div>
      </section>;
    })}
  </details>;
}
