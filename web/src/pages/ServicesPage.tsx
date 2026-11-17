import { useEffect, useState } from 'react';
import { api, fmt, fmtDuration, fmtTime, nowSec } from '../api';
import { useApp } from '../App';
import { Empty } from '../components/common';
import { useFetch } from '../hooks';
import type { ScheduleRow, ServiceRow, ServiceSummary } from '../types';

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const toTime = (min: number) => `${String(Math.floor(min / 60)).padStart(2, '0')}:${String(min % 60).padStart(2, '0')}`;
const fromTime = (s: string) => { const [h, m] = s.split(':').map(Number); return (h || 0) * 60 + (m || 0); };

export function ServicesPage({ id }: { id?: string }) {
  if (id) return <ServiceDetail id={+id} />;
  return (
    <>
      <div className="page-head"><h2>Services</h2></div>
      <ScheduleEditor />
      <ServiceList />
    </>
  );
}

function ScheduleEditor() {
  const { data, reload } = useFetch<ScheduleRow[]>('/api/hub/schedule');
  const [rows, setRows] = useState<ScheduleRow[]>([]);
  const [msg, setMsg] = useState('');
  useEffect(() => { if (data) setRows(data); }, [data]);
  const update = (i: number, patch: Partial<ScheduleRow>) => setRows(rows.map((r, k) => (k === i ? { ...r, ...patch } : r)));
  const save = async () => {
    try { await api('/api/hub/schedule', { method: 'PUT', json: rows }); setMsg('Saved.'); reload(); }
    catch (e) { setMsg((e as Error).message); }
  };
  return (
    <section className="panel">
      <h3>Weekly schedule <span className="muted">(times are this Hub PC’s local time)</span></h3>
      <p className="muted small">Each entry starts a service automatically and ends it after the duration. Logging runs all the time regardless; services just label the time ranges for reports and the “service” Leq. You can also start and stop services by hand on the Live page.</p>
      <table className="grid">
        <thead><tr><th>Name</th><th>Day</th><th>Start</th><th>Duration</th><th>On</th><th /></tr></thead>
        <tbody>
          {rows.map((r, i) => (
            <tr key={r.id || `new${i}`}>
              <td><input value={r.name} onChange={(e) => update(i, { name: e.target.value })} /></td>
              <td><select value={r.day} onChange={(e) => update(i, { day: +e.target.value })}>{DAYS.map((d, k) => <option key={k} value={k}>{d}</option>)}</select></td>
              <td><input type="time" value={toTime(r.startMinute)} onChange={(e) => update(i, { startMinute: fromTime(e.target.value) })} /></td>
              <td><input type="number" min={5} max={720} step={5} value={r.durationMinutes} onChange={(e) => update(i, { durationMinutes: +e.target.value })} /> min</td>
              <td><input type="checkbox" checked={r.enabled} onChange={(e) => update(i, { enabled: e.target.checked })} /></td>
              <td><button className="ghost" onClick={() => setRows(rows.filter((_, k) => k !== i))}>Remove</button></td>
            </tr>
          ))}
          {rows.length === 0 && <tr><td colSpan={6} className="muted">No scheduled services.</td></tr>}
        </tbody>
      </table>
      <div className="actions">
        <span className="muted">{msg}</span>
        <button onClick={() => setRows([...rows, { id: 0, name: 'Sunday service', day: 0, startMinute: 10 * 60, durationMinutes: 90, enabled: true }])}>Add</button>
        <button className="primary" onClick={save}>Save schedule</button>
      </div>
    </section>
  );
}

function ServiceList() {
  const now = Math.floor(nowSec() / 60) * 60 + 60; // whole minutes, so the URL is stable between renders
  const { data, error } = useFetch<ServiceRow[]>(`/api/hub/services?from=${now - 60 * 86400}&to=${now + 1}`, 30000);
  return (
    <section className="panel">
      <h3>Past and current services <span className="muted">(last 60 days)</span></h3>
      {error && <p className="warn">{error}</p>}
      {!data ? <Empty>Loading…</Empty> : data.length === 0 ? <Empty>No services yet.</Empty> : (
        <table className="grid">
          <thead><tr><th>Service</th><th>Started</th><th>Length</th><th>How</th></tr></thead>
          <tbody>
            {data.map((s) => (
              <tr key={s.id}>
                <td><a href={`#/services/${s.id}`}>{s.name}</a>{s.endTs == null && <span className="pill ok">running</span>}</td>
                <td>{fmtTime(s.startTs, true)}</td>
                <td>{fmtDuration((s.endTs ?? now) - s.startTs)}</td>
                <td className="muted">{s.source === 'schedule' ? 'Scheduled' : 'Manual'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

function ServiceDetail({ id }: { id: number }) {
  const { live } = useApp();
  const running = live?.service?.id === id;
  const { data, error, reload } = useFetch<ServiceSummary>(`/api/hub/services/${id}`, running ? 15000 : undefined);
  if (error) return <Empty>{error} <a href="#/services">Back</a></Empty>;
  if (!data) return <Empty>Loading…</Empty>;
  const s = data.service;
  const label = (key: string) => live?.mics.find((m) => m.key === key)?.label ?? data.mics.find((m) => m.micKey === key)?.label ?? key;

  const rename = async () => {
    const name = prompt('Service name:', s.name);
    if (!name) return;
    try { await api(`/api/hub/services/${id}`, { method: 'PUT', json: { name, startTs: s.startTs, endTs: s.endTs } }); reload(); }
    catch (e) { alert((e as Error).message); }
  };
  const remove = async () => {
    if (!confirm(`Delete the service "${s.name}"? The logged levels are kept; only the label is removed.`)) return;
    try { await api(`/api/hub/services/${id}`, { method: 'DELETE' }); location.hash = '#/services'; }
    catch (e) { alert((e as Error).message); }
  };

  return (
    <>
      <div className="page-head">
        <a href="#/services" className="muted">← Services</a>
        <h2>{s.name}</h2>
        {running && <span className="pill ok">running</span>}
        <span className="spacer" />
        <button className="ghost" onClick={rename}>Rename</button>
        <button className="ghost" onClick={remove}>Delete</button>
      </div>
      <p className="muted">{fmtTime(s.startTs, true)} – {s.endTs ? fmtTime(s.endTs) : 'now'} · {fmtDuration((s.endTs ?? nowSec()) - s.startTs)} · {s.source === 'schedule' ? 'scheduled' : 'started manually'}</p>
      <section className="panel">
        <h3>Levels per mic</h3>
        {data.mics.length === 0 ? <Empty>No data was logged during this service.</Empty> : (
          <table className="grid">
            <thead><tr>
              <th>Mic</th><th>LCeq</th><th>LAeq</th><th>Max 1-min LCeq</th><th>Max 1-min LAeq</th><th>LCpeak max</th>
              <th>Time ≥ {data.warnC} dBC</th><th>Time ≥ {data.alarmC} dBC</th><th>Data</th>
            </tr></thead>
            <tbody>
              {data.mics.map((m) => (
                <tr key={m.micKey}>
                  <td>{label(m.micKey)}</td>
                  <td><b className="cc">{fmt(m.lCeq)}</b></td><td><b className="aa">{fmt(m.lAeq)}</b></td>
                  <td>{fmt(m.maxLCeq1m)}</td><td>{fmt(m.maxLAeq1m)}</td><td>{fmt(m.lCpeakMax)}</td>
                  <td>{fmtDuration(m.secondsAboveWarnC)}</td><td>{fmtDuration(m.secondsAboveAlarmC)}</td>
                  <td className="muted">{fmtDuration(m.seconds)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        <p className="muted small">“Time ≥” counts seconds whose 1-second LCeq reached the level, using the first dBC alert rule’s warn and alarm levels. Uncalibrated mics show dBFS.</p>
      </section>
      <section className="panel">
        <h3>Alerts during this service</h3>
        {data.alerts.length === 0 ? <Empty>None.</Empty> : (
          <table className="grid">
            <thead><tr><th>When</th><th>Mic</th><th>Rule</th><th>Level</th><th>Peak</th><th>Lasted</th></tr></thead>
            <tbody>
              {data.alerts.map((a) => (
                <tr key={a.id}>
                  <td>{fmtTime(a.startTs)}</td><td>{label(a.micKey)}</td><td>{a.ruleName}</td>
                  <td><span className={`pill ${a.level === 'alarm' ? 'err' : 'wait'}`}>{a.level}</span></td>
                  <td>{a.peakValue.toFixed(1)}</td><td>{a.endTs ? fmtDuration(a.endTs - a.startTs) : 'ongoing'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
      <p><a href="#/history">Open the History page</a> and choose “Service…” to graph this service.</p>
    </>
  );
}
