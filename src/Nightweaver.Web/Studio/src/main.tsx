import { observationStatus } from "./operations";
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
} from "react-router";
import { readContext, type StudioContext } from "./context";
import { Icon, Panel, Status } from "./primitives";
import "./studio.css";
import { useObservation } from "./use-observation";
import { operations } from "./operations-browser";
import { AttentionView, BeadWorkspace } from "./bead-views";
import { Explorer } from "./explorer";
import { DeliveryView, RuntimeSummary } from "./delivery-views";
import { CatchUpPreview, CatchUpView } from "./digest-views";
import { CleanupView, ProductIntake } from "./product-views";
import { ActivityView } from "./activity-views";

const sections = [
  ["overview", "Overview"],
  ["delivery", "Delivery floor"],
  ["activity", "Activity"],
  ["attention", "Attention"],
  ["catch-up", "Catch up"],
  ["beads", "Beads"],
  ["cleanup", "Housekeeping"],
] as const;
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
    if (!location.hash.startsWith("#bead/")) {
      const section = ({ "#factory": "delivery", "#attention": "attention", "#catch-up": "catch-up", "#beads": "beads", "#cleanup": "cleanup" } as Record<string, string>)[location.hash];
      if (section) navigate(`${base}/${section}${location.search}`, { replace: true });
      return;
    }
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
            <NavLink className="nav-link" key={id} to={`${base}/${id}`} aria-label={label}
              aria-describedby={id === "attention" ? "attention-navigation-count" : undefined}>
              <Icon name={id} />
              <span>{label}</span>
              {id === "attention" && location.pathname.startsWith(`${base}/`) && <AttentionCount projectId={context.projectId} />}
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
          <Status>Studio</Status>
          <p>
            Current project only.
            <br />
            Your local workspace.
          </p>
        </div>
      </aside>
      <div className="main">
        <header className="topbar">
          <div className="crumb">
            Workspace <span>/</span> <strong>{context.projectId}</strong>
          </div>
          {location.pathname.startsWith(`${base}/`) && <SynchronizationStatus projectId={context.projectId} />}
          <form className="project-search" role="search" onSubmit={event => {
            event.preventDefault(); const data = new FormData(event.currentTarget);
            navigate(`${base}/beads?${new URLSearchParams({ view: "all", q: String(data.get("q") ?? "").trim() })}`);
          }}><label><span className="sr-only">Search current project</span><input name="q" type="search" maxLength={200} placeholder={`Search ${context.projectId}`} /></label><button className="button" type="submit">Search</button></form>
        </header>
        <main className="canvas" id="content" ref={main}>
          <Routes>
            <Route path="/" element={<Navigate to={`${base}/overview${location.search}${location.hash}`} replace />} />
            <Route path="/index.html" element={<Navigate to={`${base}/overview${location.search}${location.hash}`} replace />} />
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
          <Status tone="warn">Unavailable</Status>
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

function AttentionCount({ projectId }: { projectId: string }) {
  const snapshot = useObservation<{ items: unknown[] }>(projectId, "base:attention");
  const data = snapshot.observation?.data?.items;
  const stale = snapshot.stale || snapshot.observation?.health !== "fresh";
  return <small id="attention-navigation-count" aria-label={Array.isArray(data) ? `${data.length} paused Beads${stale ? ", retained observation" : ""}` : "Attention count unavailable"}>
    {Array.isArray(data) ? `${data.length}${stale ? " · stale" : ""}` : "—"}
  </small>;
}

function SynchronizationStatus({ projectId }: { projectId: string }) {
  const snapshot = useObservation<{ items: unknown[] }>(projectId, "base:attention");
  const [error, setError] = useState<string>();
  const health = observationStatus(snapshot);
  return <div className="synchronization">
    <span role="status">{snapshot.connectionState === "connected" ? "Live connection" : snapshot.connectionState === "connecting" ? "Connecting" : snapshot.connectionState === "recovering" ? "Recovering updates channel" : "Disconnected"} · {health}</span>
    <button className="text-link" onClick={() => {
      setError(undefined);
      operations.refresh([{ projectId, key: "base:attention" }]).catch(reason => setError(
        String(reason).includes("outcome unknown") ? "Refresh outcome unknown. Try Refresh again." : "Refresh failed. Try Refresh again."));
    }}>{snapshot.refreshing ? "Refresh again" : "Refresh"}</button>
    {(error || snapshot.error) && <span role="alert">{error ?? (snapshot.error?.includes("outcome unknown")
      ? "Refresh outcome unknown. Try Refresh again." : "Observation unavailable. Try Refresh.")}</span>}
  </div>;
}

function AttentionMetric({ projectId }: { projectId: string }) {
  const snapshot = useObservation<{ items: unknown[] }>(projectId, "base:attention");
  const data = snapshot.observation?.data?.items;
  const health = observationStatus(snapshot);
  return <Link className="summary-attention" to={`${projectPath(projectId)}/attention`}>
    {Array.isArray(data) ? `${data.length} paused Beads` : "Attention unavailable"} · {health}
  </Link>;
}

function ProjectRoutes({
  context,
}: {
  context: StudioContext;
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
        element={<Overview context={context} />}
      />
      {sections.slice(1).map(([id]) => (
        <Route
          key={id}
          path={id}
          element={id === "activity" ? <ActivityView project={context.projectId} /> : id === "delivery" ? <DeliveryView project={context.projectId} /> : id === "attention" ? <AttentionView project={context.projectId} /> : id === "beads" ? <><Explorer project={context.projectId} /><ProductIntake project={context.projectId} /></> : id === "catch-up" ? <CatchUpView project={context.projectId} /> : id === "cleanup" ? <CleanupView project={context.projectId} /> : <NotFound base={base} />}
        />
      ))}
      <Route path="beads/:beadId" element={<BeadWorkspace project={context.projectId} />} />
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
        Your local workspace <span> / </span> Studio
      </p>
      <h1 tabIndex={-1}>{title}</h1>
      <p>{description}</p>
    </div>
  );
}

function Overview({
  context,
}: {
  context: StudioContext;
}) {
  const base = projectPath(context.projectId);
  return (
    <div className="overview">
      <PageHeading title={`${context.projectId} overview`} description="Observed execution and work awaiting your attention." />
      <div className="overview-summary" aria-label="Observation availability">
        <AttentionMetric projectId={context.projectId} />
        <RuntimeSummary project={context.projectId} />
        <Link className="text-link" to={`${base}/activity`}>Follow Project activity <Icon name="arrow" /></Link>
      </div>
      <div className="studio-overview">
        <section aria-label="Your attention" className="panel attention-panel"><div className="panel-body">
          <AttentionView project={context.projectId} compact />
        </div></section>
        <Panel title="On the delivery floor" accessory={<Link className="text-link" to={`${base}/delivery`}>View floor <Icon name="arrow" /></Link>} className="floor-panel">
          <DeliveryView project={context.projectId} compact />
        </Panel>
        <section aria-label="Catch up" className="panel catch-up-panel">
          <CatchUpPreview project={context.projectId} />
        </section>
      </div>
    </div>
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
