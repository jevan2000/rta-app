import { useEffect, useRef, useState } from 'react';
import type { AuthStatus, HubLive } from './types';

export class ApiError extends Error {
  constructor(message: string, public status: number) { super(message); }
}

type SignInHandler = () => Promise<boolean>;
let signInHandler: SignInHandler | null = null;
/** The app registers its sign-in dialog here; a 401 opens it and retries once. */
export function onSignInRequired(h: SignInHandler) { signInHandler = h; }

export async function api<T = unknown>(path: string, init: RequestInit & { json?: unknown } = {}, retried = false): Promise<T> {
  const { json, ...rest } = init;
  const res = await fetch(path, {
    ...rest,
    headers: json !== undefined ? { 'Content-Type': 'application/json', ...(rest.headers ?? {}) } : rest.headers,
    body: json !== undefined ? JSON.stringify(json) : rest.body,
  });
  if (res.status === 401 && !retried && !path.startsWith('/api/auth') && signInHandler) {
    if (await signInHandler()) return api<T>(path, init, true);
  }
  if (!res.ok) {
    let msg = res.statusText;
    try { const j = await res.json(); msg = j.detail || j.title || msg; } catch { /* not json */ }
    throw new ApiError(msg, res.status);
  }
  if (res.status === 204) return undefined as T;
  const text = await res.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const getAuth = () => api<AuthStatus>('/api/auth/status');

/** Live Hub snapshot over WebSocket (5×/s), reconnecting automatically. */
export function useHubLive(): { live: HubLive | null; connected: boolean } {
  const [live, setLive] = useState<HubLive | null>(null);
  const [connected, setConnected] = useState(false);
  useEffect(() => {
    let ws: WebSocket | null = null;
    let stop = false;
    let timer: number | undefined;
    const open = () => {
      ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/hub`);
      ws.onopen = () => setConnected(true);
      ws.onmessage = (e) => setLive(JSON.parse(e.data));
      ws.onclose = () => {
        setConnected(false);
        if (!stop) timer = window.setTimeout(open, 1500);
      };
    };
    open();
    return () => { stop = true; window.clearTimeout(timer); ws?.close(); };
  }, []);
  return { live, connected };
}

export interface LivePoint { t: number; lcs: number; las: number; lCeq1m: number; lAeq1m: number; cMinusA: number | null }

/**
 * Keeps a rolling client-side history (2 points/s, 15 min) of each mic's live values so graphs
 * have something to show immediately; stored history comes from /api/hub/history.
 */
export function useLiveHistory(live: HubLive | null, seconds = 900) {
  const ref = useRef(new Map<string, LivePoint[]>());
  const last = useRef(0);
  if (live && live.t - last.current >= 450) {
    last.current = live.t;
    const t = live.t / 1000;
    for (const m of live.mics) {
      if (!m.live) continue;
      const arr = ref.current.get(m.key) ?? [];
      arr.push({ t, lcs: m.live.lcs, las: m.live.las, lCeq1m: m.live.lCeq1m, lAeq1m: m.live.lAeq1m, cMinusA: m.live.coverage1m > 5 ? m.live.cMinusA : null });
      while (arr.length && arr[0].t < t - seconds) arr.shift();
      ref.current.set(m.key, arr);
    }
  }
  return ref.current;
}

export const fmt = (v: number | null | undefined, d = 1) => (v == null || !Number.isFinite(v) || v < -150 ? '—' : v.toFixed(d));

export const nowSec = () => Math.floor(Date.now() / 1000);

export function fmtTime(ts: number, withDate = false) {
  const d = new Date(ts * 1000);
  return withDate
    ? d.toLocaleString(undefined, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
    : d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' });
}

export function fmtDuration(seconds: number) {
  const h = Math.floor(seconds / 3600), m = Math.floor((seconds % 3600) / 60);
  return h > 0 ? `${h} h ${m} min` : `${m} min`;
}

export const QUALITY_LABEL: Record<string, string> = {
  High: 'Reference grade', Medium: 'Good', Low: 'Unreliable', VeryLow: 'Very rough', Unknown: 'Accuracy unknown',
};

export const METRIC_LABEL: Record<string, string> = {
  LCS: 'dBC Slow', LAS: 'dBA Slow', LCeq1m: 'LCeq 1 min', LAeq1m: 'LAeq 1 min', LCeq15m: 'LCeq 15 min',
  LAeq15m: 'LAeq 15 min', LCeqService: 'LCeq service', LAeqService: 'LAeq service', LCpeakMax1m: 'LCpeak (1 min max)',
};
