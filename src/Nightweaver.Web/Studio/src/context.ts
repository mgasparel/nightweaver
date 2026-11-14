export type StudioContext = { projectId: string };

// Bootstrap configuration only. Tracker/runtime health and observations belong to
// the shared reconciliation coordinator; a successful bootstrap is not live health.
export async function readContext(signal: AbortSignal): Promise<StudioContext> {
  const response = await fetch("/api/studio/context", {
    signal,
    cache: "no-store",
  });
  if (!response.ok) throw new Error("Project configuration is unavailable.");
  const data: unknown = await response.json();
  if (
    !data ||
    typeof data !== "object" ||
    !("projectId" in data) ||
    typeof data.projectId !== "string" ||
    !data.projectId
  )
    throw new Error("Project configuration is invalid.");
  return { projectId: data.projectId };
}
