import { useCallback, useEffect, useState } from 'react';
import { api } from './api';
import type { AlertRule } from './types';

/** Fetches JSON and refetches on demand (and optionally on an interval). */
export function useFetch<T>(url: string | null, intervalMs?: number) {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const reload = useCallback(async () => {
    if (!url) return;
    setLoading(true);
    try { setData(await api<T>(url)); setError(null); }
    catch (e) { setError((e as Error).message); }
    finally { setLoading(false); }
  }, [url]);
  useEffect(() => {
    reload();
    if (!intervalMs) return;
    const id = window.setInterval(reload, intervalMs);
    return () => window.clearInterval(id);
  }, [reload, intervalMs]);
  return { data, error, loading, reload, setData };
}

export const useRules = () => useFetch<AlertRule[]>('/api/hub/alerts/rules', 30000);
