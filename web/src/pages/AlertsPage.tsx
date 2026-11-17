import { useEffect, useState } from 'react';
import { METRIC_LABEL, api, fmtDuration, fmtTime, nowSec } from '../api';
import { useApp } from '../App';
import { Empty } from '../components/common';
import { useFetch } from '../hooks';
import type { AlertEvent, AlertMetric, AlertRule } from '../types';

const METRICS = Object.keys(METRIC_LABEL) as AlertMetric[];

export function AlertsPage() {
  const { live } = useApp();
  const label = (key: string) => live?.mics.find((m) => m.key === key)?.label ?? live?.zones.find((z) => z.key === key)?.name ?? key;
  return (
    <>
      <div className="page-head"><h2>Alerts</h2></div>
      <section className="panel">
        <h3>Active now</h3>
        {!live?.alerts.length ? <Empty>No active alerts.</Empty> : (
          <table className="grid">
            <thead><tr><th>Mic</th><th>Rule</th><th>Level</th><th>Value</th><th>Since</th></tr></thead>
            <tbody>
              {live.alerts.map((a) => (
                <tr key={`${a.ruleId}-${a.micKey}`}>
                  <td>{label(a.micKey)}</td><td>{a.ruleName}</td>
                  <td><span className={`pill ${a.level === 'alarm' ? 'err' : 'wait'}`}>{a.level}</span></td>
                  <td>{a.value.toFixed(1)} <span className="muted">(limit {a.threshold})</span></td><td>{fmtTime(a.since)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
      <RulesEditor />
      <EventLog label={label} />
    </>
  );
}

function RulesEditor() {
  const { live } = useApp();
  const { data, reload } = useFetch<AlertRule[]>('/api/hub/alerts/rules');
  const [rules, setRules] = useState<AlertRule[]>([]);
  const [msg, setMsg] = useState('');
  useEffect(() => { if (data) setRules(data); }, [data]);
  const update = (i: number, patch: Partial<AlertRule>) => setRules(rules.map((r, k) => (k === i ? { ...r, ...patch } : r)));
  const save = async () => {
    try { await api('/api/hub/alerts/rules', { method: 'PUT', json: rules }); setMsg('Saved.'); reload(); }
    catch (e) { setMsg((e as Error).message); }
  };
  return (
    <section className="panel">
      <h3>Rules</h3>
      <p className="muted small">A rule turns a mic amber at the warn level and red at the alarm level. The level must stay over the limit for the minimum time before an alert starts, and fall below the limit minus the hysteresis before it clears. Alerts show here, on the Live page and in service reports. Zone-based rules arrive with zone prediction.</p>
      <div className="table-scroll">
        <table className="grid">
          <thead><tr><th>Name</th><th>Mic</th><th>Measure</th><th>Warn</th><th>Alarm</th><th>Hold (s)</th><th>Hysteresis (dB)</th><th>On</th><th /></tr></thead>
          <tbody>
            {rules.map((r, i) => (
              <tr key={r.id || `new${i}`}>
                <td><input value={r.name} onChange={(e) => update(i, { name: e.target.value })} /></td>
                <td>
                  <select value={r.target} onChange={(e) => update(i, { target: e.target.value })}>
                    <option value="*">Every mic</option>
                    <option value="zones:*">Every zone (predicted)</option>
                    {live?.mics.map((m) => <option key={m.key} value={m.key}>{m.label} ({m.nodeName})</option>)}
                    {live?.zones.map((z) => <option key={z.key} value={z.key}>Zone: {z.name}</option>)}
                  </select>
                </td>
                <td><select value={r.metric} onChange={(e) => update(i, { metric: e.target.value as AlertMetric })}>{METRICS.map((m) => <option key={m} value={m}>{METRIC_LABEL[m]}</option>)}</select></td>
                <td><input type="number" step={0.5} className="num" value={r.warn} onChange={(e) => update(i, { warn: +e.target.value })} /></td>
                <td><input type="number" step={0.5} className="num" value={r.alarm} onChange={(e) => update(i, { alarm: +e.target.value })} /></td>
                <td><input type="number" min={0} max={600} className="num" value={r.minDurationSeconds} onChange={(e) => update(i, { minDurationSeconds: +e.target.value })} /></td>
                <td><input type="number" min={0} max={10} step={0.5} className="num" value={r.hysteresisDb} onChange={(e) => update(i, { hysteresisDb: +e.target.value })} /></td>
                <td><input type="checkbox" checked={r.enabled} onChange={(e) => update(i, { enabled: e.target.checked })} /></td>
                <td><button className="ghost" onClick={() => setRules(rules.filter((_, k) => k !== i))}>Remove</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <div className="actions">
        <span className="muted">{msg}</span>
        <button onClick={() => setRules([...rules, { id: 0, name: 'New rule', target: '*', metric: 'LCeq1m', warn: 98, alarm: 103, hysteresisDb: 1, minDurationSeconds: 5, enabled: true }])}>Add rule</button>
        <button className="primary" onClick={save}>Save rules</button>
      </div>
    </section>
  );
}

function EventLog({ label }: { label: (k: string) => string }) {
  const now = Math.floor(nowSec() / 60) * 60 + 60;
  const { data } = useFetch<AlertEvent[]>(`/api/hub/alerts/events?from=${now - 7 * 86400}&to=${now + 1}`, 15000);
  return (
    <section className="panel">
      <h3>History <span className="muted">(last 7 days)</span></h3>
      {!data ? <Empty>Loading…</Empty> : data.length === 0 ? <Empty>No alerts in the last week.</Empty> : (
        <table className="grid">
          <thead><tr><th>Started</th><th>Mic</th><th>Rule</th><th>Level</th><th>Peak</th><th>Lasted</th></tr></thead>
          <tbody>
            {data.map((a) => (
              <tr key={a.id}>
                <td>{fmtTime(a.startTs, true)}</td><td>{label(a.micKey)}</td><td>{a.ruleName}</td>
                <td><span className={`pill ${a.level === 'alarm' ? 'err' : 'wait'}`}>{a.level}</span></td>
                <td>{a.peakValue.toFixed(1)}</td><td>{a.endTs ? fmtDuration(Math.max(60, a.endTs - a.startTs)) : 'ongoing'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}
