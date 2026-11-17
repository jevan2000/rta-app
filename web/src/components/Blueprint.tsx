import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react';
import type { Floor, MicPlacement, PlanShape, PlanZone, Pt } from '../types';

export const SHAPE_STYLE: Record<string, { fill: string; stroke: string; dash?: string; label: string }> = {
  room: { fill: 'none', stroke: '#cfe3ff', label: 'Room outline' },
  stage: { fill: 'rgba(160,190,255,.10)', stroke: '#9fc0ff', label: 'Stage / platform' },
  seating: { fill: 'rgba(207,227,255,.05)', stroke: '#7fa6d6', dash: '0.3 0.2', label: 'Seating block' },
  aisle: { fill: 'none', stroke: '#5f86b8', dash: '0.15 0.25', label: 'Aisle' },
  wall: { fill: 'none', stroke: '#e6f0ff', label: 'Wall' },
  other: { fill: 'none', stroke: '#8fb0d8', dash: '0.4 0.2', label: 'Other' },
  label: { fill: 'none', stroke: 'none', label: 'Text label' },
};

export interface ZoneStyle { fill: string; opacity: number; lines: string[]; outline?: string }

export type Hit =
  | { type: 'zone'; id: number }
  | { type: 'shape'; id: string }
  | { type: 'mic'; key: string }
  | { type: 'vertex'; owner: 'zone' | 'shape'; id: string; index: number }
  | { type: 'underlay' }
  | { type: 'none' };

export const centroid = (pts: Pt[]): Pt => {
  if (!pts.length) return { x: 0, y: 0 };
  // Area-weighted centroid; falls back to the vertex mean for degenerate polygons.
  let a = 0, cx = 0, cy = 0;
  for (let i = 0; i < pts.length; i++) {
    const p = pts[i], q = pts[(i + 1) % pts.length];
    const f = p.x * q.y - q.x * p.y;
    a += f; cx += (p.x + q.x) * f; cy += (p.y + q.y) * f;
  }
  if (Math.abs(a) < 1e-6) return { x: pts.reduce((s, p) => s + p.x, 0) / pts.length, y: pts.reduce((s, p) => s + p.y, 0) / pts.length };
  return { x: cx / (3 * a), y: cy / (3 * a) };
};

const pathOf = (pts: Pt[], closed: boolean) =>
  pts.map((p, i) => `${i ? 'L' : 'M'}${p.x},${p.y}`).join(' ') + (closed ? ' Z' : '');

/**
 * Blueprint-style floor view in metres. Read-only by default; the editor passes pointer handlers,
 * a selection and an in-progress drawing. Wheel zooms; drag with the middle button (or Shift+drag) pans.
 */
export function Blueprint({
  floor, zoneStyle, micText, micTone, selected, draft, draftClosed = true, showVertices = false, onPointer, overlay, height = 520, underlayUrl,
}: {
  floor: Floor;
  zoneStyle?: (z: PlanZone) => ZoneStyle;
  micText?: (m: MicPlacement) => string | null;
  micTone?: (m: MicPlacement) => 'ok' | 'off' | 'rover';
  selected?: Hit | null;
  draft?: Pt[] | null;
  draftClosed?: boolean;
  showVertices?: boolean;
  onPointer?: (kind: 'down' | 'move' | 'up' | 'dblclick', pt: Pt, hit: Hit, e: React.PointerEvent | React.MouseEvent) => void;
  overlay?: ReactNode;
  height?: number;
  underlayUrl?: string;
}) {
  const svg = useRef<SVGSVGElement>(null);
  const pad = 1;
  const fit = useCallback(() => ({ x: -pad, y: -pad, w: floor.width + 2 * pad, h: floor.height + 2 * pad }), [floor.width, floor.height]);
  const [vb, setVb] = useState(fit);
  useEffect(() => setVb(fit()), [fit, floor.id]);
  const pan = useRef<{ x: number; y: number; vb: typeof vb } | null>(null);

  const toPt = (e: { clientX: number; clientY: number }): Pt => {
    const s = svg.current!;
    const r = s.getBoundingClientRect();
    // preserveAspectRatio="xMidYMid meet": compute the actual scale and offsets.
    const scale = Math.min(r.width / vb.w, r.height / vb.h);
    const ox = (r.width - vb.w * scale) / 2, oy = (r.height - vb.h * scale) / 2;
    return { x: vb.x + (e.clientX - r.left - ox) / scale, y: vb.y + (e.clientY - r.top - oy) / scale };
  };

  const hitOf = (e: React.SyntheticEvent): Hit => {
    const t = (e.target as Element).closest('[data-hit]');
    if (!t) return { type: 'none' };
    const [type, a, b, c] = (t.getAttribute('data-hit') ?? '').split('|');
    if (type === 'zone') return { type, id: +a };
    if (type === 'shape') return { type, id: a };
    if (type === 'mic') return { type, key: a };
    if (type === 'vertex') return { type, owner: a as 'zone' | 'shape', id: b, index: +c };
    if (type === 'underlay') return { type };
    return { type: 'none' };
  };

  const onWheel = (e: React.WheelEvent) => {
    const p = toPt(e);
    const k = e.deltaY > 0 ? 1.15 : 1 / 1.15;
    const w = Math.min(Math.max(vb.w * k, 2), (floor.width + 2 * pad) * 3);
    const h = w * (vb.h / vb.w);
    setVb({ x: p.x - (p.x - vb.x) * (w / vb.w), y: p.y - (p.y - vb.y) * (h / vb.h), w, h });
  };

  const down = (e: React.PointerEvent) => {
    if (e.button === 1 || (e.button === 0 && e.shiftKey)) {
      pan.current = { x: e.clientX, y: e.clientY, vb };
      (e.target as Element).setPointerCapture?.(e.pointerId);
      return;
    }
    onPointer?.('down', toPt(e), hitOf(e), e);
  };
  const move = (e: React.PointerEvent) => {
    if (pan.current) {
      const r = svg.current!.getBoundingClientRect();
      const scale = Math.min(r.width / vb.w, r.height / vb.h);
      setVb({ ...pan.current.vb, x: pan.current.vb.x - (e.clientX - pan.current.x) / scale, y: pan.current.vb.y - (e.clientY - pan.current.y) / scale });
      return;
    }
    onPointer?.('move', toPt(e), hitOf(e), e);
  };
  const up = (e: React.PointerEvent) => {
    if (pan.current) { pan.current = null; return; }
    onPointer?.('up', toPt(e), hitOf(e), e);
  };

  const isSel = (h: Hit) => selected != null && JSON.stringify(selected) === JSON.stringify(h);
  const font = Math.max(0.4, Math.min(floor.width, floor.height) / 28);
  const grid = floor.width > 60 ? 5 : 1;

  return (
    <div className="blueprint-wrap">
      <svg ref={svg} className="blueprint" viewBox={`${vb.x} ${vb.y} ${vb.w} ${vb.h}`} preserveAspectRatio="xMidYMid meet"
        style={{ aspectRatio: `${floor.width + 2 * pad} / ${floor.height + 2 * pad}`, maxHeight: height }} onWheel={onWheel} onPointerDown={down} onPointerMove={move} onPointerUp={up}
        onDoubleClick={(e) => onPointer?.('dblclick', toPt(e), hitOf(e), e)}>
        <defs>
          <pattern id={`g-${floor.id}`} width={grid} height={grid} patternUnits="userSpaceOnUse">
            <path d={`M ${grid} 0 L 0 0 0 ${grid}`} fill="none" stroke="#1d4270" strokeWidth={0.03} />
          </pattern>
        </defs>
        <rect x={0} y={0} width={floor.width} height={floor.height} fill="#0d2a4d" />
        {floor.underlay && underlayUrl && (
          <image href={underlayUrl} x={floor.underlay.x} y={floor.underlay.y} width={floor.underlay.width} height={floor.underlay.height}
            opacity={floor.underlay.opacity} preserveAspectRatio="none" data-hit="underlay" style={{ pointerEvents: onPointer ? 'auto' : 'none' }} />
        )}
        <rect x={0} y={0} width={floor.width} height={floor.height} fill={`url(#g-${floor.id})`} pointerEvents="none" />
        <rect x={0} y={0} width={floor.width} height={floor.height} fill="none" stroke="#2f5d92" strokeWidth={0.05} pointerEvents="none" />

        {floor.shapes.map((s) => <ShapeEl key={s.id} s={s} font={font} selected={isSel({ type: 'shape', id: s.id })} />)}

        {floor.zones.map((z) => {
          const st = zoneStyle?.(z) ?? { fill: '#5fb4ff', opacity: 0.12, lines: [z.name] };
          const c = centroid(z.points);
          const sel = isSel({ type: 'zone', id: z.id });
          return (
            <g key={`z${z.id}-${z.points.length}`} data-hit={`zone|${z.id}`} style={{ cursor: onPointer ? 'pointer' : 'default' }}>
              <path d={pathOf(z.points, true)} fill={st.fill} fillOpacity={st.opacity} stroke={sel ? '#ffffff' : st.outline ?? st.fill}
                strokeWidth={sel ? 0.12 : 0.06} strokeDasharray={sel ? undefined : '0.25 0.12'} />
              {st.lines.map((line, i) => (
                <text key={i} x={c.x} y={c.y + (i - (st.lines.length - 1) / 2) * font * 1.25} fontSize={i === 1 ? font * 1.35 : font}
                  fontWeight={i === 1 ? 700 : 500} textAnchor="middle" dominantBaseline="middle" fill="#ffffff" pointerEvents="none"
                  style={{ paintOrder: 'stroke', stroke: 'rgba(5,15,30,.75)', strokeWidth: font * 0.25 }}>{line}</text>
              ))}
            </g>
          );
        })}

        {floor.mics.map((m) => {
          const tone = micTone?.(m) ?? 'ok';
          const color = tone === 'rover' ? '#f2b84b' : tone === 'off' ? '#6f87a3' : '#3ccf8e';
          const text = micText?.(m);
          const sel = isSel({ type: 'mic', key: m.micKey });
          return (
            <g key={m.micKey} data-hit={`mic|${m.micKey}`} style={{ cursor: onPointer ? 'pointer' : 'default' }}>
              <circle cx={m.x} cy={m.y} r={font * 0.9} fill="#0b1626" stroke={sel ? '#fff' : color} strokeWidth={font * 0.22} />
              <circle cx={m.x} cy={m.y} r={font * 0.35} fill={color} />
              {text && (
                <text x={m.x + font * 1.3} y={m.y} fontSize={font * 0.95} dominantBaseline="middle" fill={color}
                  style={{ paintOrder: 'stroke', stroke: 'rgba(5,15,30,.85)', strokeWidth: font * 0.25 }} pointerEvents="none">{text}</text>
              )}
            </g>
          );
        })}

        {showVertices && selected?.type === 'zone' && floor.zones.find((z) => z.id === selected.id)?.points.map((p, i) => (
          <circle key={i} cx={p.x} cy={p.y} r={font * 0.45} fill="#fff" stroke="#0b1626" strokeWidth={0.04} data-hit={`vertex|zone|${selected.id}|${i}`} style={{ cursor: 'move' }} />
        ))}
        {showVertices && selected?.type === 'shape' && floor.shapes.find((s) => s.id === selected.id)?.points.map((p, i) => (
          <circle key={i} cx={p.x} cy={p.y} r={font * 0.45} fill="#fff" stroke="#0b1626" strokeWidth={0.04} data-hit={`vertex|shape|${selected.id}|${i}`} style={{ cursor: 'move' }} />
        ))}

        {draft && draft.length > 0 && (
          <g pointerEvents="none">
            <path d={pathOf(draft, draftClosed && draft.length > 2)} fill={draftClosed ? 'rgba(95,180,255,.15)' : 'none'} stroke="#5fb4ff" strokeWidth={0.08} strokeDasharray="0.2 0.1" />
            {draft.map((p, i) => <circle key={i} cx={p.x} cy={p.y} r={font * 0.3} fill="#5fb4ff" />)}
          </g>
        )}
        {overlay}
      </svg>
      <div className="blueprint-tools">
        <button className="ghost" title="Zoom in" onClick={() => setVb((v) => ({ x: v.x + v.w * 0.1, y: v.y + v.h * 0.1, w: v.w * 0.8, h: v.h * 0.8 }))}>+</button>
        <button className="ghost" title="Zoom out" onClick={() => setVb((v) => ({ x: v.x - v.w * 0.125, y: v.y - v.h * 0.125, w: v.w * 1.25, h: v.h * 1.25 }))}>−</button>
        <button className="ghost" title="Fit floor" onClick={() => setVb(fit())}>Fit</button>
      </div>
    </div>
  );
}

function ShapeEl({ s, font, selected }: { s: PlanShape; font: number; selected: boolean }) {
  const st = SHAPE_STYLE[s.kind] ?? SHAPE_STYLE.other;
  if (s.kind === 'label') {
    const p = s.points[0] ?? { x: 0, y: 0 };
    return (
      <text data-hit={`shape|${s.id}`} x={p.x} y={p.y} fontSize={font * 1.1} fill={selected ? '#fff' : '#cfe3ff'} textAnchor="middle"
        dominantBaseline="middle" style={{ letterSpacing: '0.05em', cursor: 'default' }}>{s.label || 'Label'}</text>
    );
  }
  const closed = s.kind !== 'wall';
  const c = centroid(s.points);
  return (
    <g data-hit={`shape|${s.id}`}>
      <path d={pathOf(s.points, closed)} fill={closed ? st.fill : 'none'} stroke={selected ? '#ffffff' : st.stroke}
        strokeWidth={s.kind === 'wall' ? 0.18 : selected ? 0.1 : 0.06} strokeDasharray={st.dash} strokeLinejoin="round" />
      {/* wide invisible stroke so thin lines are easy to click */}
      <path d={pathOf(s.points, closed)} fill="none" stroke="transparent" strokeWidth={0.6} />
      {s.label && s.kind !== 'wall' && (
        <text x={c.x} y={c.y} fontSize={font * 0.85} fill="#9fc0ff" textAnchor="middle" dominantBaseline="middle" opacity={0.85} pointerEvents="none">{s.label}</text>
      )}
    </g>
  );
}

/** Colour for a level relative to warn/alarm: green → amber → red. */
export function levelColor(v: number | null | undefined, warn: number, alarm: number): string {
  if (v == null) return '#6f87a3';
  if (v >= alarm) return '#ff5d5d';
  if (v >= warn) return '#f2b84b';
  // fade from blue (quiet) to green (approaching warn)
  const t = Math.max(0, Math.min(1, (v - (warn - 20)) / 20));
  const a = [95, 180, 255], b = [60, 207, 142];
  return `rgb(${a.map((x, i) => Math.round(x + (b[i] - x) * t)).join(',')})`;
}
