import { useMemo, useState } from 'react';
import { fmt, fmtTime, nowSec } from '../api';
import { useApp } from '../App';
import { TimeChart } from '../components/Charts';
import { Empty } from '../components/common';
import { useFetch } from '../hooks';
import type { Series, ServiceRow } from '../types';

const PALETTE = ['#5fb4ff', '#f2b84b', '#3ccf8e', '#ff7ab6', '#b28dff', '#ff8f5a', '#5fe0d4', '#d6e05f'];
type Preset = '1h' | '6h' | '24h' | '7d' | 'service';

export function HistoryPage() {
  const { live } = useApp();
  const mics = (live?.mics ?? []).filter((m) => !m.hidden);
  const [selected, setSelected] = useState<string[] | null>(null);
  const [preset, setPreset] = useState<Preset>('6h');
  const [weighting, setWeighting] = useState<'lc' | 'la'>('lc');
  const [serviceId, setServiceId] = useState<number | null>(null);
  const keys = selected ?? mics.slice(0, 4).map((m) => m.key);

  const now = Math.floor(nowSec() / 3600) * 3600 + 3600;
  const { data: services } = useFetch<ServiceRow[]>(`/api/hub/services?from=${now - 90 * 86400}&to=${now + 1}`);
  const svc = services?.find((s) => s.id === serviceId) ?? services?.[0];
  const [from, to] = useMemo((): [number, number] => {
    const n = nowSec();
    switch (preset) {
      case '1h': return [n - 3600, n + 1];
      case '24h': return [n - 86400, n + 1];
      case '7d': return [n - 7 * 86400, n + 1];
      case 'service': return svc ? [svc.startTs, (svc.endTs ?? n) + 1] : [n - 3600, n + 1];
      default: return [n - 6 * 3600, n + 1];
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [preset, svc?.id, Math.floor(Date.now() / 60000)]);

  const url = keys.length ? `/api/hub/history?mics=${keys.map(encodeURIComponent).join(',')}&from=${from}&to=${to}` : null;
  const { data: series, loading } = useFetch<Series[]>(url);

  // Align all series on a common x axis.
  const chart = useMemo(() => {
    if (!series?.length) return null;
    const xs = [...new Set(series.flatMap((s) => s.ts))].sort((a, b) => a - b);
    const idx = new Map(xs.map((t, i) => [t, i]));
    const lines = series.map((s, k) => {
      const vals: (number | null)[] = new Array(xs.length).fill(null);
      s.ts.forEach((t, i) => { const v = s[weighting][i]; vals[idx.get(t)!] = v > -150 ? v : null; });
      const m = mics.find((x) => x.key === s.mic);
      return { label: m?.label ?? s.mic, color: PALETTE[k % PALETTE.length], values: vals };
    });
    return { xs, lines, resolution: series[0].resolution };
  }, [series, weighting, mics]);

  const leqs = series?.map((s) => {
    let e = 0, d = 0;
    s[weighting].forEach((v, i) => { if (v > -150) { e += 10 ** (v / 10) * s.coverage[i]; d += s.coverage[i]; } });
    return { key: s.mic, leq: d > 0 ? 10 * Math.log10(e / d) : null, seconds: d };
  });

  return (
    <>
      <div className="page-head"><h2>History</h2></div>
      <div className="toolbar wrap">
        {(['1h', '6h', '24h', '7d'] as Preset[]).map((p) => (
          <button key={p} className={preset === p ? 'seg active' : 'seg'} onClick={() => setPreset(p)}>Last {p}</button>
        ))}
        <button className={preset === 'service' ? 'seg active' : 'seg'} disabled={!services?.length} onClick={() => setPreset('service')}>Service…</button>
        {preset === 'service' && services && (
          <select value={svc?.id ?? ''} onChange={(e) => setServiceId(+e.target.value)}>
            {services.map((s) => <option key={s.id} value={s.id}>{s.name} — {fmtTime(s.startTs, true)}</option>)}
          </select>
        )}
        <span className="spacer" />
        <button className={weighting === 'lc' ? 'seg active' : 'seg'} onClick={() => setWeighting('lc')}>dBC</button>
        <button className={weighting === 'la' ? 'seg active' : 'seg'} onClick={() => setWeighting('la')}>dBA</button>
      </div>
      <div className="mic-picker">
        {mics.map((m) => (
          <label key={m.key} className="check">
            <input type="checkbox" checked={keys.includes(m.key)}
              onChange={(e) => setSelected(e.target.checked ? [...keys, m.key] : keys.filter((k) => k !== m.key))} />
            {m.label} <span className="muted small">({m.nodeName})</span>
          </label>
        ))}
      </div>
      <section className="panel">
        <h3>{weighting === 'lc' ? 'LCeq' : 'LAeq'} <span className="muted">({chart?.resolution === 'minute' ? 'per minute' : 'per second'}; ranges over 3 h use minute data)</span></h3>
        {loading && !chart ? <Empty>Loading…</Empty> : chart && chart.xs.length ? (
          <TimeChart x={chart.xs} series={chart.lines} height={320} xRange={[from, Math.min(to, nowSec())]} />
        ) : <Empty>No data for the selected mics and range.</Empty>}
      </section>
      {leqs && leqs.length > 0 && (
        <section className="panel">
          <h3>Leq over the range</h3>
          <table className="grid">
            <thead><tr><th>Mic</th><th>{weighting === 'lc' ? 'LCeq' : 'LAeq'}</th><th>Data</th></tr></thead>
            <tbody>
              {leqs.map((r) => (
                <tr key={r.key}><td>{mics.find((m) => m.key === r.key)?.label ?? r.key}</td><td><b>{fmt(r.leq)}</b></td><td className="muted">{Math.round(r.seconds / 60)} min</td></tr>
              ))}
            </tbody>
          </table>
        </section>
      )}
    </>
  );
}
