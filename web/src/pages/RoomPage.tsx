import { useState } from 'react';
import { fmt } from '../api';
import { useApp } from '../App';
import { Blueprint, levelColor } from '../components/Blueprint';
import { Empty, thresholdsFor } from '../components/common';
import { useFetch, useRules } from '../hooks';
import type { Plan, ZoneView } from '../types';

type Metric = 'lC1s' | 'lCeq1m' | 'lCeq15m' | 'lCeqService' | 'lA1s' | 'lAeq1m';
const METRICS: [Metric, string][] = [
  ['lCeq1m', 'dBC 1 min'], ['lC1s', 'dBC now'], ['lCeq15m', 'dBC 15 min'], ['lCeqService', 'dBC service'], ['lAeq1m', 'dBA 1 min'], ['lA1s', 'dBA now'],
];

export function RoomPage() {
  const { live } = useApp();
  const { data: plan } = useFetch<Plan>('/api/hub/plan', 30000);
  const { data: rules } = useRules();
  const [floorId, setFloorId] = useState<string | null>(null);
  const [metric, setMetric] = useState<Metric>('lCeq1m');
  if (!plan || !live) return <Empty>Loading…</Empty>;

  const floor = plan.floors.find((f) => f.id === floorId) ?? plan.floors[0];
  const zones = new Map(live.zones.map((z) => [z.id, z]));
  const isA = metric.startsWith('lA');
  const th = (z: ZoneView) =>
    thresholdsFor(rules, z.key, isA ? 'LAeq1m' : 'LCeq1m') ?? thresholdsFor(rules, 'zones:*', isA ? 'LAeq1m' : 'LCeq1m')
    ?? (isA ? { warn: 92, alarm: 97 } : { warn: 98, alarm: 103 });
  const micMap = new Map(live.mics.map((m) => [m.key, m]));
  const hasContent = floor.zones.length > 0 || floor.shapes.length > 0;
  const surveyedAny = live.zones.some((z) => z.surveyed);

  return (
    <>
      <div className="page-head">
        <h2>Room</h2>
        {plan.floors.length > 1 && plan.floors.map((f) => (
          <button key={f.id} className={f.id === floor.id ? 'seg active' : 'seg'} onClick={() => setFloorId(f.id)}>{f.name}</button>
        ))}
        <span className="spacer" />
        {METRICS.map(([m, label]) => <button key={m} className={metric === m ? 'seg active' : 'seg'} onClick={() => setMetric(m)}>{label}</button>)}
      </div>
      {!hasContent && <div className="notice">No plan yet. <a href="#/plan">Draw the room and its zones in the Plan editor</a>, place your mics, then <a href="#/survey">survey each zone</a>.</div>}
      {hasContent && !surveyedAny && <div className="notice">Zones show predicted levels once they’ve been surveyed. <a href="#/survey">Start a survey</a>.</div>}
      <div className="room-layout">
        <section className="panel room-plan">
          <Blueprint
            floor={floor}
            height={560}
            underlayUrl={floor.underlay ? `/api/hub/plan/images/${floor.underlay.imageId}` : undefined}
            zoneStyle={(z) => {
              const v = zones.get(z.id);
              const val = v?.[metric] ?? null;
              const t = v ? th(v) : { warn: 98, alarm: 103 };
              const color = v?.surveyed ? levelColor(val, t.warn, t.alarm) : '#6f87a3';
              return {
                fill: color,
                opacity: v?.surveyed && val != null ? 0.38 : 0.08,
                outline: color,
                lines: [z.name, v?.surveyed ? (val != null ? fmt(val) : '—') : 'not surveyed', v?.surveyed ? (v.stale ? '⚠ resurvey' : `${Math.round(v.confidence * 100)}% conf.`) : ''].filter(Boolean),
              };
            }}
            micText={(m) => {
              const mv = micMap.get(m.micKey);
              if (!mv) return '?';
              const v = mv.live ? (isA ? mv.live.las : mv.live.lcs) : null;
              return `${mv.label}${v != null && v > -150 ? ` ${v.toFixed(0)}` : ''}${m.role === 'Roving' ? ' (roving)' : ''}`;
            }}
            micTone={(m) => (m.role === 'Roving' ? 'rover' : micMap.get(m.micKey)?.online ? 'ok' : 'off')}
            onPointer={(kind, _pt, hit) => { if (kind === 'up' && hit.type === 'zone') location.hash = `#/zone/${hit.id}`; }}
          />
          <p className="muted small">Colour: blue = quiet, green = approaching the warn level, amber = warn, red = alarm (from the zone alert rules, or 98/103 dBC). Scroll to zoom, Shift+drag to pan, click a zone for details.</p>
        </section>
        <section className="panel room-list">
          <h3>Zones</h3>
          {floor.zones.length === 0 ? <Empty>No zones on this floor.</Empty> : (
            <table className="grid">
              <thead><tr><th>Zone</th><th>Now</th><th>1 min</th><th>Service</th><th>Conf.</th></tr></thead>
              <tbody>
                {floor.zones.map((pz) => {
                  const z = zones.get(pz.id);
                  return (
                    <tr key={pz.id}>
                      <td><a href={`#/zone/${pz.id}`}>{pz.name}</a>{z?.issues.length ? <div className="warn small">{z.issues[0]}</div> : null}</td>
                      <td>{fmt(isA ? z?.lA1s : z?.lC1s)}</td>
                      <td><b>{fmt(isA ? z?.lAeq1m : z?.lCeq1m)}</b></td>
                      <td>{fmt(isA ? z?.lAeqService : z?.lCeqService)}</td>
                      <td className="muted">{z?.surveyed ? `${Math.round(z.confidence * 100)}%` : '—'}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          )}
        </section>
      </div>
    </>
  );
}
