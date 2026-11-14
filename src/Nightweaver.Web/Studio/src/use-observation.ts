import { useCallback, useState, useSyncExternalStore } from "react";
import { operations } from "./operations-browser";

export function useObservation<T = unknown>(projectId: string, key: string) {
  const [error, setError] = useState<string>();
  const subscribe = useCallback((listener: () => void) => {
    try { const off = operations.subscribe({ projectId, key }, listener); setError(undefined); return off; }
    catch (failure) { setError(String(failure)); return () => {}; }
  }, [projectId, key]);
  const snapshot = useCallback(() => operations.getSnapshot<T>({ projectId, key }), [projectId, key]);
  const value = useSyncExternalStore(subscribe, snapshot);
  return error ? { ...value, error } : value;
}
