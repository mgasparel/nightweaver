import { queryKey } from "./operations.ts";

export const views = ["ready", "active", "blocked", "all", "closed"] as const;
export function explorerQuery(params: URLSearchParams) {
  const view = params.get("view") ?? "ready";
  const search = (params.get("q") ?? "").trim();
  const text = params.get("page") ?? "1";
  const page = Number(text);
  const valid = views.some(item => item === view) && search.length <= 200
    && /^[1-9][0-9]*$/.test(text) && page <= 250
    && ["view", "q", "page"].every(name => params.getAll(name).length <= 1);
  return { view, search, page, valid, key: valid ? `${queryKey("explorer", view, search)}:${page}` : "" };
}
export type ExplorerData = { items: { id: string; title: string; type: string; priority: number;
  status: string; assignee: string | null; stage: string | null }[]; total: number; page: number; pages: number; pageSize: number };
