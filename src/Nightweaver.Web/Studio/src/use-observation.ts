import { useCallback, useState, useSyncExternalStore } from "react";
import { operations } from "./operations-browser";
import type { Snapshot } from "./operations";

const inactive: Snapshot = { connectionState: "disconnected", refreshing: false, stale: true };

export function useObservation<T = unknown>(projectId: string, key?: string) {
  const [error, setError] = useState<string>();
  const subscribe = useCallback((listener: () => void) => {
    if (!key) return () => {};
    try { const off = operations.subscribe({ projectId, key }, listener); setError(undefined); return off; }
    catch (failure) { setError(String(failure)); return () => {}; }
  }, [projectId, key]);
  const snapshot = useCallback(() => key ? operations.getSnapshot<T>({ projectId, key }) : inactive as Snapshot<T>, [projectId, key]);
  const value = useSyncExternalStore(subscribe, snapshot);
  return error ? { ...value, error } : value;
}
