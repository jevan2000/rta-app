import { useEffect, useMemo, useRef, useState } from 'react';
import { fmt, nowSec } from '../api';
import { useApp } from '../App';
import { Histogram, RtaChart, Spectrogram, TimeChart } from '../components/Charts';
import { Empty, QualityBadge, Tile, cssVar, levelClass, micTitle, thresholdsFor } from '../components/common';
import { useFetch, useRules } from '../hooks';
import type { Series } from '../types';

type Range = 'live' | 'service' | 'hour' | 'today';
const RANGE_LABEL: Record<Range, string> = { live: 'Last 15 min (live)', service: 'This service', hour: 'Last hour', today: 'Today' };
type Extra = 'none' | 'spectrogram' | 'histogram';

function rangeBounds(r: Range, serviceStart: number | null): [number, number] {
  const now = nowSec();
  switch (r) {
    case 'service': return [serviceStart ?? now - 900, now + 1];
    case 'hour': return [now - 3600, now + 1];
    case 'today': { const d = new Date(); d.setHours(0, 0, 0, 0); return [Math.floor(d.getTime() / 1000), now + 1]; }
    default: return [now - 900, now + 1];
  }
}

/** Energy average of (dB, weight) pairs. */
function leq(values: number[], weights: number[]) {
  let e = 0, d = 0;
  values.forEach((v, i) => { if (v > -150 && weights[i] > 0) { e += 10 ** (v / 10) * weights[i]; d += weights[i]; } });
  return d > 0 ? 10 * Math.log10(e / d) : null;
}

export function MicPage({ micKey }: { micKey: string }) {
  const { live } = useApp();
  const { data: rules } = useRules();
  const [range, setRange] = useState<Range>('live');
  const [extra, setExtra] = useState<Extra>('none');
  const m = live?.mics.find((x) => x.key === micKey);
  const serviceStart = live?.service?.startTs ?? null;
  const [from, to] = useMemo(() => rangeBounds(range, serviceStart),
    // recompute bounds every refresh tick (see below) or when the range changes
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [range, serviceStart, Math.floor(Date.now() / 5000)]);

  const { data: series } = useFetch<Series[]>(`/api/hub/history?mics=${encodeURIComponent(micKey)}&from=${from}&to=${to}`);
  const s = series?.[0];

  // RTA peak hold
  const peaks = useRef<number[] | null>(null);
  const bands = m?.live?.bands ?? null;
  if (bands) peaks.current = peaks.current && peaks.current.length === bands.length ? bands.map((v, i) => Math.max(v, peaks.current![i] - 0.15)) : bands;
  useEffect(() => { peaks.current = null; }, [micKey]);

  // C−A trend: 60-s rolling Leq difference from per-second data (or per-minute values directly).
  const ca = useMemo(() => {
    if (!s || !s.ts.length) return { x: [] as number[], y: [] as (number | null)[] };
    if (s.resolution === 'minute') return { x: s.ts, y: s.lc.map((c, i) => (c > -150 ? c - s.la[i] : null)) };
    const x: number[] = [], y: (number | null)[] = [];
    let eA = 0, eC = 0, n = 0, j = 0;
    for (let i = 0; i < s.ts.length; i++) {
      eA += 10 ** (s.la[i] / 10); eC += 10 ** (s.lc[i] / 10); n++;
      while (s.ts[i] - s.ts[j] >= 60) { eA -= 10 ** (s.la[j] / 10); eC -= 10 ** (s.lc[j] / 10); n--; j++; }
      if (i % 5 === 0) { x.push(s.ts[i]); y.push(n >= 10 ? 10 * Math.log10(eC / n) - 10 * Math.log10(eA / n) : null); }
    }
    return { x, y };
  }, [s]);

  if (!live) return <Empty>Connecting…</Empty>;
  if (!m) return <Empty>Mic not found. <a href="#/live">Back to Live</a></Empty>;

  const L = m.live;
  const tC = thresholdsFor(rules, m.key, 'LCeq1m') ?? thresholdsFor(rules, m.key, 'LCS');
  const guides = m.calibrated && tC ? [{ value: tC.warn, color: cssVar('--warn') }, { value: tC.alarm, color: cssVar('--alarm') }] : [];
  const rangeLeqC = s ? leq(s.lc, s.coverage) : null;
  const rangeLeqA = s ? leq(s.la, s.coverage) : null;

  return (
    <>
      <div className="page-head">
        <a href="#/live" className="muted">← Live</a>
        <h2>{micTitle(m)} <span className="muted small">· {m.nodeName}</span></h2>
        <QualityBadge quality={m.quality} uncertainty={m.uncertaintyDb} calibrated={m.calibrated} />
        {!m.online && <span className="pill err">offline</span>}
      </div>
      {!m.calibrated && <div className="notice">This mic is uncalibrated: values are dBFS, not dB SPL. Calibrate it on its node’s page.</div>}
      {(m.quality === 'Low' || m.quality === 'VeryLow') && <div className="notice">Calibrated with an unreliable method ({m.profileName}). Treat values as approximate.</div>}

      <div className="toolbar">
        {(Object.keys(RANGE_LABEL) as Range[]).map((r) => (
          <button key={r} className={range === r ? 'seg active' : 'seg'} disabled={r === 'service' && !serviceStart} onClick={() => setRange(r)}>
            {RANGE_LABEL[r]}
          </button>
        ))}
        <span className="spacer" />
        <button className={extra === 'spectrogram' ? 'seg active' : 'seg'} onClick={() => setExtra(extra === 'spectrogram' ? 'none' : 'spectrogram')}>Spectrogram</button>
        <button className={extra === 'histogram' ? 'seg active' : 'seg'} onClick={() => setExtra(extra === 'histogram' ? 'none' : 'histogram')}>Histogram</button>
      </div>

      <div className="detail-grid">
        <section>
          <h3>Level vs time <span className="muted">({s?.resolution === 'minute' ? '1-min' : '1-s'} LCeq / LAeq)</span></h3>
          {s && s.ts.length ? (
            <TimeChart x={s.ts} xRange={[from, Math.min(to, nowSec())]} guides={guides}
              series={[{ label: 'LCeq', color: cssVar('--c'), values: s.lc }, { label: 'LAeq', color: cssVar('--a'), values: s.la }]} />
          ) : <Empty>No stored data in this range yet.</Empty>}
        </section>
        <section>
          <h3>Leq</h3>
          <div className="tiles">
            <Tile label="LCeq 1 min" value={L?.lCeq1m} cls={m.calibrated ? levelClass(L?.lCeq1m, tC) : ''} />
            <Tile label="LCeq 15 min" value={L?.lCeq15m} />
            <Tile label="LCeq service" value={m.lCeqService} />
            <Tile label="LAeq 1 min" value={L?.lAeq1m} />
            <Tile label="LAeq 15 min" value={L?.lAeq15m} />
            <Tile label="LAeq service" value={m.lAeqService} />
            <Tile label="LC Slow" value={L?.lcs} />
            <Tile label="LCpeak max 1 min" value={L?.lCpeakMax1m} />
            <Tile label="C − A 1 min" value={L && L.coverage1m > 5 ? L.cMinusA : null} unit=" dB" />
          </div>
          <p className="muted small">{RANGE_LABEL[range]}: LCeq <b>{fmt(rangeLeqC)}</b> · LAeq <b>{fmt(rangeLeqA)}</b></p>
        </section>
        <section>
          <h3>1/3-octave RTA <span className="muted">(live, peak hold)</span></h3>
          <RtaChart bandsHz={live.bandsHz} levels={bands} peaks={peaks.current} height={220} />
        </section>
        <section>
          <h3>C − A trend <span className="muted">(bass heaviness, 1-min)</span></h3>
          {ca.x.length ? <TimeChart x={ca.x} xRange={[from, Math.min(to, nowSec())]} series={[{ label: 'C − A', color: cssVar('--accent'), values: ca.y }]} yLabel="dB" /> : <Empty>Not enough data yet.</Empty>}
        </section>
        {extra === 'spectrogram' && <SpectrogramSection micKey={m.key} from={from} to={to} bandsHz={live.bandsHz} />}
        {extra === 'histogram' && <HistogramSection micKey={m.key} from={from} to={to} guide={m.calibrated ? tC?.warn ?? null : null} />}
      </div>
    </>
  );
}

function SpectrogramSection({ micKey, from, to, bandsHz }: { micKey: string; from: number; to: number; bandsHz: number[] }) {
  const [f, t] = [Math.floor(from / 60) * 60 - 60, Math.ceil(to / 60) * 60];
  const { data } = useFetch<{ ts: number[]; bands: number[][] }>(`/api/hub/spectrogram?mic=${encodeURIComponent(micKey)}&from=${f}&to=${t}`, 60000);
  return (
    <section className="wide">
      <h3>Spectrogram <span className="muted">(1-min 1/3-octave Leq)</span></h3>
      {data ? <Spectrogram ts={data.ts} bands={data.bands} bandsHz={bandsHz} /> : <Empty>Loading…</Empty>}
    </section>
  );
}

function HistogramSection({ micKey, from, to, guide }: { micKey: string; from: number; to: number; guide: number | null }) {
  const [f, t] = [Math.floor(from / 30) * 30, Math.ceil(to / 30) * 30];
  const { data } = useFetch<{ db: number[]; secondsA: number[]; secondsC: number[] }>(`/api/hub/histogram?mic=${encodeURIComponent(micKey)}&from=${f}&to=${t}`, 30000);
  return (
    <section className="wide">
      <h3>Time at each level <span className="muted">(seconds per dB, 1-s Leq; <span style={{ color: 'var(--c)' }}>C</span> / <span style={{ color: 'var(--a)' }}>A</span>)</span></h3>
      {data ? <Histogram db={data.db} a={data.secondsA} c={data.secondsC} guides={guide != null ? [{ value: guide, color: cssVar('--warn') }] : []} /> : <Empty>Loading…</Empty>}
    </section>
  );
}
