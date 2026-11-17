import { useEffect, useRef } from 'react';
import uPlot from 'uplot';
import 'uplot/dist/uPlot.min.css';

const cssVar = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

export interface LineSeries { label: string; color: string; values: (number | null)[]; width?: number; dash?: number[] }

/** Time-series chart (uPlot). x in Unix seconds. Optional horizontal guide lines (warn/alarm). */
export function TimeChart({ x, series, height = 220, guides = [], yLabel, xRange }: {
  x: number[];
  series: LineSeries[];
  height?: number;
  guides?: { value: number; color: string }[];
  yLabel?: string;
  xRange?: [number, number];
}) {
  const el = useRef<HTMLDivElement>(null);
  const plot = useRef<uPlot | null>(null);
  const guidesRef = useRef(guides);
  guidesRef.current = guides;
  const key = series.map((s) => s.label + s.color).join('|');

  useEffect(() => {
    if (!el.current) return;
    const grid = { stroke: cssVar('--grid'), width: 1 };
    const axis = { stroke: cssVar('--muted'), grid, ticks: grid, font: '11px Segoe UI, system-ui' };
    const opts: uPlot.Options = {
      width: el.current.clientWidth || 600,
      height,
      legend: { show: series.length > 1 },
      cursor: { drag: { x: false, y: false } },
      scales: {
        x: { time: true, ...(xRange ? { range: () => xRange } : {}) },
        y: {
          range: (_u, min, max) => {
            const vals = [min, max, ...guidesRef.current.map((g) => g.value)].filter((v) => v != null && Number.isFinite(v));
            if (!vals.length) return [40, 110];
            let lo = Math.min(...vals) - 3, hi = Math.max(...vals) + 3;
            if (hi - lo < 20) { const m = (hi + lo) / 2; lo = m - 10; hi = m + 10; }
            return [Math.floor(lo / 5) * 5, Math.ceil(hi / 5) * 5];
          },
        },
      },
      axes: [axis, { ...axis, label: yLabel, size: 48 }],
      series: [
        {},
        ...series.map((s) => ({ label: s.label, stroke: s.color, width: s.width ?? 1.6, dash: s.dash, spanGaps: false, points: { show: false } })),
      ],
      hooks: {
        draw: [(u) => {
          const ctx = u.ctx;
          for (const g of guidesRef.current) {
            const y = u.valToPos(g.value, 'y', true);
            if (y < u.bbox.top || y > u.bbox.top + u.bbox.height) continue;
            ctx.save();
            ctx.strokeStyle = g.color;
            ctx.setLineDash([5, 5]);
            ctx.lineWidth = 1;
            ctx.beginPath(); ctx.moveTo(u.bbox.left, y); ctx.lineTo(u.bbox.left + u.bbox.width, y); ctx.stroke();
            ctx.restore();
          }
        }],
      },
    };
    plot.current = new uPlot(opts, [x, ...series.map((s) => s.values)] as uPlot.AlignedData, el.current);
    const ro = new ResizeObserver(() => plot.current?.setSize({ width: el.current!.clientWidth, height }));
    ro.observe(el.current);
    return () => { ro.disconnect(); plot.current?.destroy(); plot.current = null; };
    // Re-create only when the series set or height changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key, height, yLabel, xRange?.[0], xRange?.[1]]);

  useEffect(() => {
    plot.current?.setData([x, ...series.map((s) => s.values)] as uPlot.AlignedData);
  });

  return <div ref={el} className="chart" />;
}

function useCanvas(draw: (g: CanvasRenderingContext2D, w: number, h: number) => void, height: number, deps: unknown[]) {
  const ref = useRef<HTMLCanvasElement>(null);
  useEffect(() => {
    const cv = ref.current;
    if (!cv) return;
    const dpr = window.devicePixelRatio || 1;
    const w = cv.clientWidth;
    if (cv.width !== Math.round(w * dpr) || cv.height !== Math.round(height * dpr)) {
      cv.width = Math.round(w * dpr); cv.height = Math.round(height * dpr);
    }
    const g = cv.getContext('2d')!;
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, height);
    draw(g, w, height);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);
  return ref;
}

const LABELLED = new Set([31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000]);
const hzLabel = (f: number) => (f >= 1000 ? `${f / 1000}k` : `${f}`);

/** 1/3-octave bar chart with optional peak-hold markers. */
export function RtaChart({ bandsHz, levels, peaks, height = 200, labels = true }: {
  bandsHz: number[]; levels: number[] | null; peaks?: number[] | null; height?: number; labels?: boolean;
}) {
  const ref = useCanvas((g, w, h) => {
    if (!levels || !levels.length) return;
    const left = labels ? 30 : 0, bottom = labels ? 14 : 0, ph = h - bottom;
    const vals = (peaks ?? levels).filter((v) => v > -150);
    let hi = Math.ceil((Math.max(...vals, -100) + 3) / 10) * 10;
    let lo = hi - Math.max(50, Math.ceil((hi - Math.min(...vals, hi)) / 10) * 10);
    if (!vals.length) { lo = 0; hi = 100; }
    const y = (v: number) => ph - ((Math.max(v, lo) - lo) / (hi - lo)) * ph;
    g.strokeStyle = cssVar('--grid'); g.fillStyle = cssVar('--muted'); g.font = '10px Segoe UI, system-ui';
    for (let v = lo; v <= hi; v += 10) {
      g.beginPath(); g.moveTo(left, y(v)); g.lineTo(w, y(v)); g.stroke();
      if (labels) g.fillText(String(v), 2, y(v) + 3);
    }
    const n = levels.length, bw = (w - left) / n;
    g.fillStyle = cssVar('--c');
    for (let i = 0; i < n; i++) g.fillRect(left + i * bw + 1, y(levels[i]), Math.max(1, bw - 2), ph - y(levels[i]));
    if (peaks) {
      g.fillStyle = cssVar('--text');
      for (let i = 0; i < n; i++) g.fillRect(left + i * bw + 1, y(peaks[i]) - 1, Math.max(1, bw - 2), 2);
    }
    if (labels) {
      g.fillStyle = cssVar('--muted'); g.textAlign = 'center';
      bandsHz.forEach((f, i) => { if (LABELLED.has(f)) g.fillText(hzLabel(f), left + (i + 0.5) * bw, h - 2); });
      g.textAlign = 'left';
    }
  }, height, [levels, peaks, bandsHz, height, labels]);
  return <canvas ref={ref} style={{ width: '100%', height }} />;
}

/** Spectrogram from per-minute band levels: time left→right, frequency bottom→top. */
export function Spectrogram({ ts, bands, bandsHz, height = 240 }: { ts: number[]; bands: number[][]; bandsHz: number[]; height?: number }) {
  const ref = useCanvas((g, w, h) => {
    if (!ts.length) {
      g.fillStyle = cssVar('--muted'); g.font = '13px Segoe UI, system-ui';
      g.fillText('No minute data in this range yet (minutes are logged as they complete).', 12, 24);
      return;
    }
    const left = 34, bottom = 16, ph = h - bottom, pw = w - left;
    const nb = Math.max(...bands.map((b) => b.length), 1);
    const all = bands.flat().filter((v) => v > -150);
    const hi = Math.max(...all), lo = Math.max(hi - 60, Math.min(...all));
    const cw = pw / ts.length, bh = ph / nb;
    for (let i = 0; i < ts.length; i++)
      for (let b = 0; b < bands[i].length; b++) {
        const t = Math.min(1, Math.max(0, (bands[i][b] - lo) / (hi - lo || 1)));
        g.fillStyle = heat(t);
        g.fillRect(left + i * cw, ph - (b + 1) * bh, Math.ceil(cw), Math.ceil(bh));
      }
    g.fillStyle = cssVar('--muted'); g.font = '10px Segoe UI, system-ui';
    bandsHz.forEach((f, b) => { if (LABELLED.has(f) && b < nb) g.fillText(hzLabel(f), 2, ph - b * bh - bh / 2 + 3); });
    const step = Math.max(1, Math.floor(ts.length / 6));
    for (let i = 0; i < ts.length; i += step)
      g.fillText(new Date(ts[i] * 1000).toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' }), left + i * cw, h - 3);
    g.fillText(`${lo.toFixed(0)}–${hi.toFixed(0)} dB`, w - 80, 12);
  }, height, [ts, bands, bandsHz, height]);
  return <canvas ref={ref} style={{ width: '100%', height }} />;
}

function heat(t: number) {
  // dark blue → cyan → yellow → red
  const stops = [[11, 22, 38], [30, 90, 160], [60, 200, 210], [240, 200, 70], [255, 80, 70]];
  const p = t * (stops.length - 1), i = Math.min(stops.length - 2, Math.floor(p)), f = p - i;
  const c = stops[i].map((v, k) => Math.round(v + (stops[i + 1][k] - v) * f));
  return `rgb(${c[0]},${c[1]},${c[2]})`;
}

/** Bars of seconds spent at each dB level, A and C side by side. */
export function Histogram({ db, a, c, height = 220, guides = [] }: { db: number[]; a: number[]; c: number[]; height?: number; guides?: { value: number; color: string }[] }) {
  const ref = useCanvas((g, w, h) => {
    if (!db.length) {
      g.fillStyle = cssVar('--muted'); g.font = '13px Segoe UI, system-ui';
      g.fillText('No per-second data in this range.', 12, 24);
      return;
    }
    const bottom = 16, ph = h - bottom;
    const max = Math.max(...a, ...c, 1);
    const bw = w / db.length;
    for (let i = 0; i < db.length; i++) {
      g.fillStyle = cssVar('--a'); g.fillRect(i * bw + 1, ph - (a[i] / max) * ph, bw / 2 - 1, (a[i] / max) * ph);
      g.fillStyle = cssVar('--c'); g.fillRect(i * bw + bw / 2, ph - (c[i] / max) * ph, bw / 2 - 1, (c[i] / max) * ph);
    }
    for (const gd of guides) {
      const i = db.indexOf(Math.floor(gd.value));
      if (i < 0) continue;
      g.strokeStyle = gd.color; g.setLineDash([4, 4]);
      g.beginPath(); g.moveTo(i * bw, 0); g.lineTo(i * bw, ph); g.stroke(); g.setLineDash([]);
    }
    g.fillStyle = cssVar('--muted'); g.font = '10px Segoe UI, system-ui';
    const step = Math.max(1, Math.ceil(db.length / 12));
    for (let i = 0; i < db.length; i += step) g.fillText(String(db[i]), i * bw, h - 3);
  }, height, [db, a, c, height, guides]);
  return <canvas ref={ref} style={{ width: '100%', height }} />;
}

/** Tiny line for cards. */
export function Sparkline({ values, values2, height = 48, warn }: { values: (number | null)[]; values2?: (number | null)[]; height?: number; warn?: number | null }) {
  const ref = useCanvas((g, w, h) => {
    const all = [...values, ...(values2 ?? [])].filter((v): v is number => v != null && v > -150);
    if (all.length < 2) return;
    let lo = Math.min(...all) - 2, hi = Math.max(...all) + 2;
    if (hi - lo < 10) { const m = (lo + hi) / 2; lo = m - 5; hi = m + 5; }
    const y = (v: number) => h - ((v - lo) / (hi - lo)) * h;
    if (warn != null && warn > lo && warn < hi) {
      g.strokeStyle = cssVar('--warn'); g.setLineDash([3, 3]);
      g.beginPath(); g.moveTo(0, y(warn)); g.lineTo(w, y(warn)); g.stroke(); g.setLineDash([]);
    }
    const line = (vals: (number | null)[], color: string) => {
      g.strokeStyle = color; g.lineWidth = 1.4; g.beginPath();
      let started = false;
      vals.forEach((v, i) => {
        const x = (i / Math.max(1, vals.length - 1)) * w;
        if (v == null || v < -150) { started = false; return; }
        if (!started) { g.moveTo(x, y(v)); started = true; } else g.lineTo(x, y(v));
      });
      g.stroke();
    };
    if (values2) line(values2, cssVar('--a'));
    line(values, cssVar('--c'));
  }, height, [values, values2, height, warn]);
  return <canvas ref={ref} style={{ width: '100%', height }} />;
}
