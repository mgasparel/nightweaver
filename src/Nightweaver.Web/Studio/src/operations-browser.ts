import { QueryCoordinator, InteractionStore, type Transport } from "./operations";

const base = (project: string) => `/api/operations/v1/projects/${encodeURIComponent(project)}`;
const reconcile = async (project: string, keys: string[], signal: AbortSignal) => {
  const response = await fetch(`${base(project)}/refresh`, { method: "POST", headers: {
    "Content-Type": "application/json", "X-Nightweaver-Request": "console-v1",
  }, body: JSON.stringify({ keys }), signal });
  if (response.status !== 202) throw Error(`Reconciliation request failed (${response.status})`);
};
const transport: Transport = {
  connect(project, keys, event, failed) {
    const source = new EventSource(`${base(project)}/events?${new URLSearchParams(keys.map(key => ["key", key]))}`);
    for (const name of ["hello", "invalidation", "resync", "heartbeat"]) source.addEventListener(name, message => {
      try { event(name, JSON.parse((message as MessageEvent).data)); } catch { failed(); }
    });
    source.onerror = failed;
    return () => source.close();
  },
  async read(identity, signal) {
    const response = await fetch(`${base(identity.projectId)}/resources?${new URLSearchParams({ key: identity.key })}`, { signal, cache: "no-store" });
    if (!response.ok) throw Error(`Observation read failed (${response.status})`);
    return response.json();
  },
  refresh: reconcile,
  wake: reconcile,
};
export const operations = new QueryCoordinator(transport, {
  now: () => performance.now(), random: () => Math.random(),
  later(callback, ms) { const timer = setTimeout(callback, ms); return () => clearTimeout(timer); },
});
export const interactions = new InteractionStore();
document.addEventListener("visibilitychange", () => operations.setVisible(!document.hidden));
window.addEventListener("focus", () => operations.wake());
window.addEventListener("pagehide", () => { operations.setVisible(false); operations.suspend(); });
window.addEventListener("pageshow", () => operations.setVisible(!document.hidden));
