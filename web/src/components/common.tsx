import type { ReactNode } from 'react';
import { QUALITY_LABEL, fmt } from '../api';
import type { AlertRule, MicView, Quality } from '../types';

export function QualityBadge({ quality, uncertainty, calibrated }: { quality: Quality | null; uncertainty: number | null; calibrated: boolean }) {
  if (!calibrated) return <span className="badge uncal" title="No calibration profile — readings are dBFS">UNCAL · dBFS</span>;
  const rough = quality === 'Low' || quality === 'VeryLow';
  const label = QUALITY_LABEL[quality ?? 'Unknown'];
  return (
    <span className={`badge ${rough ? 'rough' : quality === 'High' || quality === 'Medium' ? 'cal' : ''}`} title={label}>
      {rough ? '≈ ' : ''}{label}{uncertainty != null ? ` ±${uncertainty.toFixed(rough ? 0 : 1)} dB` : ''}
    </span>
  );
}

/** Warn/alarm thresholds for a mic+metric from the alert rules (first matching rule wins). */
export function thresholdsFor(rules: AlertRule[] | null, micKey: string, metric: string) {
  const r = rules?.find((x) => x.enabled && x.metric === metric && (x.target === micKey || x.target === '*'));
  return r ? { warn: r.warn, alarm: r.alarm } : null;
}

export function levelClass(v: number | null | undefined, t: { warn: number; alarm: number } | null) {
  if (v == null || t == null || v < -150) return '';
  return v >= t.alarm ? 'alarm-level' : v >= t.warn ? 'warn-level' : '';
}

export function Tile({ label, value, cls = '', unit }: { label: string; value: number | null | undefined; cls?: string; unit?: string }) {
  return (
    <div className="tile">
      <div className="l">{label}</div>
      <div className={`n ${cls}`}>{fmt(value)}{unit && value != null && value > -150 ? <span className="u">{unit}</span> : null}</div>
    </div>
  );
}

export function micTitle(m: MicView) {
  return `${m.label}${m.micLabel ? ` · ${m.micLabel}` : ''}`;
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty muted">{children}</div>;
}

export const cssVar = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
