import { StrictMode, useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  BrowserRouter,
  Link,
  Navigate,
  NavLink,
  Route,
  Routes,
  useLocation,
  useNavigate,
  useParams,
  useSearchParams,
} from "react-router";
import { readContext, type StudioContext } from "./context";
import { Icon, Panel, Status, Unavailable } from "./primitives";
import "./studio.css";

const sections = [
  ["overview", "Overview"],
  ["delivery", "Delivery floor"],
  ["attention", "Attention"],
  ["catch-up", "Catch up"],
  ["beads", "Beads"],
] as const;
const legacySection: Record<string, string> = {
  delivery: "factory",
  attention: "attention",
  "catch-up": "catch-up",
  beads: "beads",
};
const projectPath = (id: string) => `/projects/${encodeURIComponent(id)}`;

function Bootstrap() {
  const [context, setContext] = useState<StudioContext>();
  const [error, setError] = useState(false);
  useEffect(() => {
    const controller = new AbortController();
    readContext(controller.signal)
      .then(setContext)
      .catch(() => {
        if (!controller.signal.aborted) setError(true);
      });
    return () => controller.abort();
  }, []);
  if (!context)
    return (
      <main className="bootstrap">
        <Icon name="moon" />
        <h1>Nightweaver Studio</h1>
        <p role="status">
          {error
            ? "Project configuration is unavailable."
            : "Loading your project…"}
        </p>
        {error && (
          <>
            <button className="button" onClick={() => window.location.reload()}>
              Try again
            </button>
            <a className="button" href="/">
              Open existing console
            </a>
          </>
        )}
      </main>
    );
  return <AppShell context={context} />;
}

function AppShell({ context }: { context: StudioContext }) {
  const location = useLocation();
  const navigate = useNavigate();
  const main = useRef<HTMLElement>(null);
  const dialog = useRef<HTMLDialogElement>(null);
  const opener = useRef<HTMLElement | null>(null);
  const [placeholder, setPlaceholder] = useState("");
  const base = projectPath(context.projectId);
  const pathParts = location.pathname.split("/");
  const section = sections.find(([id]) => id === pathParts[3]);

  useEffect(() => {
    if (!location.hash.startsWith("#bead/")) return;
    try {
      const id = decodeURIComponent(location.hash.slice(6));
      if (id)
        navigate(`${base}/beads/${encodeURIComponent(id)}${location.search}`, {
          replace: true,
        });
    } catch {
      /* Malformed legacy IDs remain on a usable page. */
    }
  }, [base, location.hash, location.search, navigate]);

  useEffect(() => {
    document.title = `${section?.[1] ?? "Studio"} · Nightweaver`;
    main.current?.querySelector("h1")?.focus();
    window.scrollTo(0, 0);
  }, [location.pathname, section]);

  function showPlaceholder(title: string, trigger: HTMLElement) {
    opener.current = trigger;
    setPlaceholder(title);
    dialog.current?.showModal();
  }

  return (
    <div className="app">
      <a className="skip" href="#content">
        Skip to main content
      </a>
      <aside className="sidebar">
        <Link className="brand" to={`${base}/overview`}>
          <Icon name="moon" />
          <span>
            nightweaver<span className="brand-dot">.</span>
          </span>
        </Link>
        <nav aria-label="Project navigation">
          {sections.map(([id, label]) => (
            <NavLink className="nav-link" key={id} to={`${base}/${id}`}>
              <Icon name={id} />
              <span>{label}</span>
            </NavLink>
          ))}
        </nav>
        <div className="project-navigation">
          <p className="eyebrow">Your projects</p>
          <button
            className="nav-link"
            onClick={(event) =>
              showPlaceholder("All projects", event.currentTarget)
            }
          >
            <Icon name="overview" />
            All projects<small>Soon</small>
          </button>
          <Link className="nav-link current-project" to={`${base}/overview`}>
            <span className="project-icon">
              {context.projectId[0].toUpperCase()}
            </span>
            <span>{context.projectId}</span>
          </Link>
          <button
            className="nav-link"
            onClick={(event) =>
              showPlaceholder("Add project", event.currentTarget)
            }
          >
            <Icon name="plus" />
            Add project<small>Soon</small>
          </button>
        </div>
        <div className="side-bottom">
          <Status>Studio preview</Status>
          <p>
            Current project only.
            <br />
            Your local workspace.
          </p>
          <a href="/">
            Open existing console <Icon name="arrow" />
          </a>
        </div>
      </aside>
      <div className="main">
        <header className="topbar">
          <div className="crumb">
            Workspace <span>/</span> <strong>{context.projectId}</strong>
          </div>
          <Status>Live status not connected</Status>
          <a className="console-link" href="/">
            Existing console <Icon name="arrow" />
          </a>
        </header>
        <main className="canvas" id="content" ref={main}>
          <Routes>
            <Route
              path="/studio/*"
              element={
                <Navigate
                  to={`${base}/overview${location.search}${location.hash}`}
                  replace
                />
              }
            />
            <Route
              path="/projects/:projectId/*"
              element={
                <ProjectRoutes
                  context={context}
                  onAdd={(trigger) => showPlaceholder("Add project", trigger)}
                />
              }
            />
            <Route path="*" element={<NotFound base={base} />} />
          </Routes>
        </main>
      </div>
      <dialog
        ref={dialog}
        aria-labelledby="placeholder-title"
        onClose={() => opener.current?.focus()}
        onClick={(event) => {
          if (event.target === dialog.current) dialog.current?.close();
        }}
      >
        <div className="dialog-heading">
          <h2 id="placeholder-title">{placeholder}</h2>
          <button
            className="button"
            onClick={() => dialog.current?.close()}
            autoFocus
          >
            Close
          </button>
        </div>
        <div className="dialog-body">
          <Status tone="warn">Unavailable in this preview</Status>
          <p>
            Studio is connected to the configured current project only.
            Multi-project navigation and project setup will be available in a
            future release.
          </p>
          <p>No project has been added or changed.</p>
        </div>
      </dialog>
    </div>
  );
}

function ProjectRoutes({
  context,
  onAdd,
}: {
  context: StudioContext;
  onAdd: (trigger: HTMLElement) => void;
}) {
  const { projectId } = useParams();
  const base = projectPath(context.projectId);
  if (projectId !== context.projectId)
    return (
      <>
        <PageHeading
          title="Project unavailable"
          description="This host serves only its configured current project."
        />
        <Panel title="Current project">
          <p>No data or actions are available for this project.</p>
          <Link className="button" to={`${base}/overview`}>
            Open {context.projectId}
          </Link>
        </Panel>
      </>
    );
  return (
    <Routes>
      <Route index element={<Navigate to="overview" replace />} />
      <Route
        path="overview"
        element={<Overview context={context} onAdd={onAdd} />}
      />
      {sections.slice(1).map(([id, title]) => (
        <Route
          key={id}
          path={id}
          element={<FeatureRoute id={id} title={title} />}
        />
      ))}
      <Route path="beads/:beadId" element={<Workspace />} />
      <Route path="*" element={<NotFound base={base} />} />
    </Routes>
  );
}

function PageHeading({
  title,
  description,
}: {
  title: string;
  description: string;
}) {
  return (
    <div className="page-heading">
      <p className="eyebrow">
        Your local workspace <span> / </span> Studio preview
      </p>
      <h1 tabIndex={-1}>{title}</h1>
      <p>{description}</p>
    </div>
  );
}

function Overview({
  context,
  onAdd,
}: {
  context: StudioContext;
  onAdd: (trigger: HTMLElement) => void;
}) {
  const base = projectPath(context.projectId);
  return (
    <>
      <div className="heading-row">
        <PageHeading
          title={`${context.projectId}, at a glance.`}
          description="Your agents have the details. You have the bigger picture."
        />
        <Link className="button" to={`${base}/catch-up`}>
          <Icon name="catch-up" />
          Catch me up
        </Link>
      </div>
      <div className="metrics" aria-label="Observation availability">
        {[
          ["Delivery", "Runtime not connected"],
          ["Attention", "Tracker not connected"],
          ["Merged", "Digest not connected"],
          ["Ready", "Tracker not connected"],
        ].map(([label, detail]) => (
          <div className="metric" key={label}>
            <span className="eyebrow">{label}</span>
            <strong aria-label="Unavailable">—</strong>
            <small>{detail}</small>
          </div>
        ))}
      </div>
      <div className="studio-overview">
        <Panel
          title="Delivery floor"
          accessory={
            <Link className="text-link" to={`${base}/delivery`}>
              View floor <Icon name="arrow" />
            </Link>
          }
          className="floor-panel"
        >
          <div className="floor-intro">
            <Icon name="delivery" />
            <span>Parallel Delivery. One careful merge at a time.</span>
          </div>
          <Unavailable title="A place for work in motion." href="/#factory">
            Execution bays and invocation inspection will appear here when
            runtime observations are connected. No occupancy is inferred.
          </Unavailable>
          <div className="merge-note">
            <Icon name="arrow" />
            <div>
              <h3>Singleton Merger</h3>
              <p>Runtime observation unavailable</p>
            </div>
            <Status>Not connected</Status>
          </div>
        </Panel>
        <Panel
          title="Your attention"
          accessory={<Icon name="attention" />}
          className="attention-panel"
        >
          <Unavailable
            title="Decisions deserve a clear place."
            href="/#attention"
            action="Open Attention Desk"
          >
            Paused Beads and guarded guidance, Retry and Abort actions remain
            available in the existing console.
          </Unavailable>
        </Panel>
        <Panel
          title="Your projects"
          accessory={
            <button
              className="text-link"
              onClick={(event) => onAdd(event.currentTarget)}
            >
              ＋ Add project
            </button>
          }
        >
          <Link className="project-card" to={`${base}/overview`}>
            <div className="project-card-heading">
              <span className="project-icon">
                {context.projectId[0].toUpperCase()}
              </span>
              <h3>{context.projectId}</h3>
              <Status tone="accent">Current project</Status>
            </div>
            <p>The project configured for this local host.</p>
            <div className="project-summary">
              <span>Runtime status unavailable</span>
              <Icon name="arrow" />
            </div>
          </Link>
          <div className="future-project">
            <Icon name="plus" />
            <div>
              <h3>Room for your next project.</h3>
              <p>Multi-project support is coming later.</p>
            </div>
          </div>
        </Panel>
        <Panel
          title="Catch up"
          accessory={<span className="eyebrow">Meaningful changes</span>}
        >
          <Unavailable
            title="Pick up where you left off."
            href="/#catch-up"
            action="Open existing digest"
          >
            Milestones, bounded history and your catch-up checkpoint remain in
            the existing console until the digest is connected.
          </Unavailable>
          <Link className="text-link" to={`${base}/catch-up`}>
            Visit catch up <Icon name="arrow" />
          </Link>
        </Panel>
      </div>
    </>
  );
}

function FeatureRoute({ id, title }: { id: string; title: string }) {
  return (
    <>
      <PageHeading
        title={title}
        description={
          id === "beads"
            ? "Manually planned by you. Carried through Delivery by Nightweaver."
            : "A dedicated place for your current project."
        }
      />
      {id === "beads" && <ExplorerContext />}
      <Panel title={title} accessory={<Status>Not connected</Status>}>
        <Unavailable
          title={`${title} is taking shape.`}
          href={`/#${legacySection[id]}`}
        >
          This Studio route is ready for integration. Use the existing console
          for current observations and operational actions.
        </Unavailable>
      </Panel>
    </>
  );
}

function ExplorerContext() {
  const [params, setParams] = useSearchParams();
  return (
    <form
      className="explorer-controls"
      onSubmit={(event) => {
        event.preventDefault();
        const data = new FormData(event.currentTarget);
        const next = new URLSearchParams(params);
        next.set("q", String(data.get("q") ?? ""));
        next.set("view", String(data.get("view") ?? "ready"));
        next.delete("page");
        setParams(next);
      }}
    >
      <label>
        Search Beads
        <input
          key={params.get("q")}
          name="q"
          type="search"
          defaultValue={params.get("q") ?? ""}
          placeholder="Search by title or Bead ID"
        />
      </label>
      <label>
        View
        <select
          key={params.get("view")}
          name="view"
          defaultValue={params.get("view") ?? "ready"}
        >
          <option value="ready">Ready</option>
          <option value="all">All</option>
          <option value="active">Active</option>
          <option value="closed">Closed</option>
        </select>
      </label>
      <button className="button" type="submit">
        Save view in URL
      </button>
      <p>View preferences only; tracker results are not connected.</p>
    </form>
  );
}

function Workspace() {
  const { beadId } = useParams();
  const [params] = useSearchParams();
  const tab = ["summary", "history", "output"].includes(params.get("tab") ?? "")
    ? params.get("tab")!
    : "summary";
  return (
    <>
      <PageHeading
        title={`Bead ${beadId}`}
        description="Full Bead Workspace · current project"
      />
      <nav className="workspace-tabs" aria-label="Workspace sections">
        {["summary", "history", "output"].map((value) => {
          const next = new URLSearchParams(params);
          next.set("tab", value);
          return (
            <Link
              key={value}
              to={`?${next}`}
              aria-current={tab === value ? "page" : undefined}
            >
              {value[0].toUpperCase() + value.slice(1)}
            </Link>
          );
        })}
      </nav>
      <Panel title={tab[0].toUpperCase() + tab.slice(1)}>
        <Unavailable
          title="Bead details are not connected."
          href={`/#bead/${encodeURIComponent(beadId ?? "")}`}
          action="Open existing Bead Workspace"
        >
          This route identifies the selected Bead only. It does not establish
          that the Bead exists, authorize work, or imply a running Agent.
        </Unavailable>
      </Panel>
    </>
  );
}

function NotFound({ base }: { base: string }) {
  return (
    <>
      <PageHeading
        title="Page not found"
        description="This Studio route is unavailable."
      />
      <Link className="button" to={`${base}/overview`}>
        Back to overview
      </Link>
    </>
  );
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <BrowserRouter>
      <Bootstrap />
    </BrowserRouter>
  </StrictMode>,
);
