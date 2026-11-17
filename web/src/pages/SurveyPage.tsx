import { useEffect, useState } from 'react';
import { api, fmt, fmtTime, nowSec } from '../api';
import { useApp } from '../App';
import { Blueprint } from '../components/Blueprint';
import { Empty } from '../components/common';
import { useFetch } from '../hooks';
import type { Plan, SurveyView } from '../types';

export function SurveyPage() {
  const { live } = useApp();
  const { data: plan } = useFetch<Plan>('/api/hub/plan', 30000);
  const { data: surveys, reload: reloadSurveys } = useFetch<SurveyView[]>('/api/hub/surveys', 15000);
  const [floorIdx, setFloorIdx] = useState(0);
  const [zoneId, setZoneId] = useState<number | null>(null);
  const [rover, setRover] = useState('');
  const [minSec, setMinSec] = useState(60);
  const [msg, setMsg] = useState('');
  const [busy, setBusy] = useState(false);

  // Default rover: the mic marked "Roving" on the plan.
  useEffect(() => {
    if (rover || !plan) return;
    const r = plan.floors.flatMap((f) => f.mics).find((m) => m.role === 'Roving');
    if (r) setRover(r.micKey);
  }, [plan, rover]);

  if (!plan || !live) return <Empty>Loading…</Empty>;
  const zonesAll = plan.floors.flatMap((f) => f.zones);
  if (zonesAll.length === 0) return <Empty>No zones yet. <a href="#/plan">Draw zones in the Plan editor</a> first, and place your permanent mics.</Empty>;
  const floor = plan.floors[Math.min(floorIdx, plan.floors.length - 1)];
  const s = live.survey;
  const zone = zonesAll.find((z) => z.id === (s?.zoneId ?? zoneId));
  const zv = live.zones.find((z) => z.id === zone?.id);
  const refsOnPlan = plan.floors.flatMap((f) => f.mics).filter((m) => m.role === 'Permanent');
  const label = (key: string) => live.mics.find((m) => m.key === key)?.label ?? key;

  const run = async (fn: () => Promise<unknown>, ok?: string) => {
    setBusy(true); setMsg('');
    try { await fn(); if (ok) setMsg(ok); reloadSurveys(); } catch (e) { setMsg((e as Error).message); } finally { setBusy(false); }
  };

  return (
    <>
      <div className="page-head">
        <h2>Survey</h2>
        {plan.floors.length > 1 && plan.floors.map((f, i) => <button key={f.id} className={i === floorIdx ? 'seg active' : 'seg'} onClick={() => setFloorIdx(i)}>{f.name}</button>)}
      </div>
      <p className="muted">Take the roving mic (a laptop running as a node) to a seat in a zone, choose the zone, and press Start. Leave it for at least a minute while the service or pink noise plays; the permanent mics are recorded over exactly the same time. The confidence bar fills as the data steadies.</p>
      {refsOnPlan.length === 0 && <div className="notice">No permanent mics are placed on the plan. <a href="#/plan">Place them</a> first; they’re what predictions are made from.</div>}

      <div className="room-layout">
        <section className="panel room-plan">
          <Blueprint floor={floor} height={460}
            underlayUrl={floor.underlay ? `/api/hub/plan/images/${floor.underlay.imageId}` : undefined}
            selected={zone ? { type: 'zone', id: zone.id } : null}
            zoneStyle={(z) => {
              const v = live.zones.find((x) => x.id === z.id);
              const active = s?.zoneId === z.id;
              return {
                fill: active ? '#f2b84b' : v?.surveyed ? (v.stale ? '#f2b84b' : '#3ccf8e') : '#6f87a3',
                opacity: active ? 0.35 : 0.18,
                lines: [z.name, v?.surveyed ? `${v.surveys} survey${v.surveys > 1 ? 's' : ''}${v.stale ? ' · resurvey' : ''}` : 'not surveyed'],
              };
            }}
            micText={(m) => label(m.micKey) + (m.role === 'Roving' ? ' (roving)' : '')}
            micTone={(m) => (m.role === 'Roving' ? 'rover' : live.mics.find((x) => x.key === m.micKey)?.online ? 'ok' : 'off')}
            onPointer={(k, _p, hit) => { if (k === 'up' && hit.type === 'zone' && !s) setZoneId(hit.id); }} />
          <p className="muted small">Click a zone to choose it. Green = surveyed, grey = not yet, amber = needs a resurvey.</p>
        </section>

        <section className="panel room-list">
          {s ? (
            <>
              <h3>Surveying “{s.zoneName}”</h3>
              <div className="survey-progress">
                <div className="bar"><div style={{ width: `${Math.round(s.confidence * 100)}%` }} /></div>
                <b>{Math.round(s.confidence * 100)}%</b>
              </div>
              <p>{s.seconds < 60 ? `${Math.round(s.seconds)} s` : `${Math.floor(s.seconds / 60)} min ${Math.round(s.seconds % 60)} s`} of {s.minSeconds} s minimum · roving mic <b>{s.roverLabel}</b> {fmt(s.roverLC)} dBC</p>
              {s.refs.length > 0 && (
                <table className="grid">
                  <thead><tr><th>Permanent mic</th><th>Zone − mic</th><th>Steadiness</th></tr></thead>
                  <tbody>{s.refs.map((r) => <tr key={r.micKey}><td>{r.label}</td><td>{r.offset != null ? `${r.offset > 0 ? '+' : ''}${r.offset.toFixed(1)} dB` : '—'}</td><td>{r.chunks >= 2 ? `±${r.chunkStd?.toFixed(1)} dB` : '…'}</td></tr>)}</tbody>
                </table>
              )}
              {s.warnings.map((w) => <p key={w} className="warn small">{w}</p>)}
              <div className="actions">
                <button disabled={busy} className="ghost" onClick={() => confirm('Discard this survey?') && run(() => api('/api/hub/survey/stop', { method: 'POST', json: { save: false } }), 'Discarded.')}>Discard</button>
                <button disabled={busy} className="primary" onClick={() => {
                  if (s.seconds < s.minSeconds && !confirm(`Only ${Math.round(s.seconds)} s recorded (minimum ${s.minSeconds} s). Save anyway with lower confidence?`)) return;
                  const note = prompt('Note (optional, e.g. "row 12 seat 4, Sunday 11am"):') ?? '';
                  run(() => api('/api/hub/survey/stop', { method: 'POST', json: { save: true, note } }), 'Saved. The zone now uses this survey.');
                }}>Stop &amp; save</button>
              </div>
            </>
          ) : (
            <>
              <h3>New survey</h3>
              <div className="form-grid one">
                <label>Zone
                  <select value={zone?.id ?? ''} onChange={(e) => setZoneId(+e.target.value)}>
                    <option value="" disabled>Click a zone or choose…</option>
                    {zonesAll.map((z) => <option key={z.id} value={z.id}>{z.name}</option>)}
                  </select>
                </label>
                <label>Roving mic
                  <select value={rover} onChange={(e) => setRover(e.target.value)}>
                    <option value="" disabled>Choose…</option>
                    {live.mics.filter((m) => m.online).map((m) => <option key={m.key} value={m.key}>{m.label}{m.micLabel ? ` · ${m.micLabel}` : ''} ({m.nodeName})</option>)}
                  </select>
                </label>
                <label>Minimum length (s) <input type="number" min={10} max={1800} step={10} value={minSec} onChange={(e) => setMinSec(+e.target.value)} /></label>
              </div>
              <div className="actions">
                <button className="primary" disabled={busy || !zone || !rover} onClick={() => run(() => api('/api/hub/survey/start', { method: 'POST', json: { zoneId: zone!.id, roverMicKey: rover, minSeconds: minSec } }))}>Start survey</button>
              </div>
              <ManualEntry zoneId={zone?.id ?? null} onDone={(m) => { setMsg(m); reloadSurveys(); }} />
            </>
          )}
          {msg && <p className="muted">{msg}</p>}
          {zone && <ZoneSurveys zoneName={zone.name} mode={zone.mode} list={(surveys ?? []).filter((x) => x.zoneId === zone.id)} label={label} reload={reloadSurveys} />}
          {zv && zv.issues.length > 0 && <div className="small">{zv.issues.map((i) => <p key={i} className="warn">{i}</p>)}</div>}
        </section>
      </div>
    </>
  );
}

function ManualEntry({ zoneId, onDone }: { zoneId: number | null; onDone: (msg: string) => void }) {
  const [open, setOpen] = useState(false);
  const [la, setLa] = useState('');
  const [lc, setLc] = useState('');
  const [mins, setMins] = useState(1);
  const [ago, setAgo] = useState(0);
  if (!open) return <p className="small"><button className="ghost" onClick={() => setOpen(true)}>Enter a handheld meter reading instead…</button></p>;
  const submit = async () => {
    const to = nowSec() - ago * 60;
    try {
      await api('/api/hub/survey/manual', { method: 'POST', json: { zoneId, lA: la ? +la : null, lC: lc ? +lc : null, fromTs: to - mins * 60, toTs: to } });
      onDone('Manual reading saved (single-number, lower confidence).');
      setOpen(false); setLa(''); setLc('');
    } catch (e) { onDone((e as Error).message); }
  };
  return (
    <div className="manual">
      <h3>Handheld meter reading</h3>
      <p className="muted small">Less accurate than a roving mic: it gives one number, so predictions can’t follow changes in the mix. Use the meter’s Leq over the period below.</p>
      <div className="form-grid one">
        <div className="two-col">
          <label>dBA Leq <input type="number" step={0.1} value={la} onChange={(e) => setLa(e.target.value)} /></label>
          <label>dBC Leq <input type="number" step={0.1} value={lc} onChange={(e) => setLc(e.target.value)} /></label>
        </div>
        <div className="two-col">
          <label>Measured over (min) <input type="number" min={0.25} max={60} step={0.25} value={mins} onChange={(e) => setMins(+e.target.value)} /></label>
          <label>Ending (min ago) <input type="number" min={0} max={600} value={ago} onChange={(e) => setAgo(+e.target.value)} /></label>
        </div>
      </div>
      <div className="actions">
        <button className="ghost" onClick={() => setOpen(false)}>Cancel</button>
        <button className="primary" disabled={!zoneId || (!la && !lc)} onClick={submit}>Save reading</button>
      </div>
    </div>
  );
}

function ZoneSurveys({ zoneName, mode, list, label, reload }: { zoneName: string; mode: string; list: SurveyView[]; label: (k: string) => string; reload: () => void }) {
  const act = async (fn: () => Promise<unknown>) => { try { await fn(); reload(); } catch (e) { alert((e as Error).message); } };
  return (
    <>
      <h3>Surveys of {zoneName}</h3>
      <p className="muted small">{mode === 'Average' ? 'All included surveys are averaged.' : 'The newest included survey is used.'} Change this on the zone in the Plan editor.</p>
      {list.length === 0 ? <p className="muted small">None yet.</p> : (
        <table className="grid">
          <thead><tr><th>When</th><th>Type</th><th>Zone level</th><th>Conf.</th><th /></tr></thead>
          <tbody>
            {list.map((s) => (
              <tr key={s.id} className={s.excluded ? 'dim' : ''}>
                <td>{fmtTime(s.startTs, true)}{s.note && <div className="muted small">{s.note}</div>}{s.inModel && <span className="pill ok">in use</span>}</td>
                <td>{s.kind === 'manual' ? 'Meter' : `${Math.round(s.seconds)} s${s.roverKey ? ` · ${label(s.roverKey)}` : ''}`}</td>
                <td>{fmt(s.roverLC)} dBC<div className="muted small">{s.refs.map((r) => `${label(r.micKey)} ${r.offsetC != null ? (r.offsetC > 0 ? '+' : '') + r.offsetC.toFixed(1) : '—'}`).join(', ')}</div></td>
                <td>{Math.round(s.confidence * 100)}%</td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <button className="ghost" onClick={() => act(() => api(`/api/hub/surveys/${s.id}`, { method: 'PUT', json: { excluded: !s.excluded, note: s.note } }))}>{s.excluded ? 'Include' : 'Exclude'}</button>
                  <button className="ghost" onClick={() => confirm('Delete this survey permanently?') && act(() => api(`/api/hub/surveys/${s.id}`, { method: 'DELETE' }))}>Delete</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}
