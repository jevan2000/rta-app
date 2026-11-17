import { useEffect, useRef, useState } from 'react';
import { api } from '../api';
import { useApp } from '../App';
import { Blueprint, SHAPE_STYLE, type Hit } from '../components/Blueprint';
import { Empty } from '../components/common';
import { useFetch } from '../hooks';
import type { Floor, Plan, PlanShape, Pt } from '../types';

type Tool = 'select' | 'zone' | 'zone-rect' | 'rect' | 'poly' | 'wall' | 'label' | 'mic';
const TOOLS: [Tool, string, string][] = [
  ['select', 'Select / move', 'Click to select; drag to move; drag white dots to reshape. Delete removes.'],
  ['zone-rect', 'Zone ▭', 'Drag a rectangle to create a listening zone.'],
  ['zone', 'Zone ⬠', 'Click corners; click the first corner (or double-click) to finish.'],
  ['rect', 'Shape ▭', 'Drag a rectangle (room, stage, seating block…).'],
  ['poly', 'Shape ⬠', 'Click corners; click the first corner (or double-click) to finish.'],
  ['wall', 'Wall', 'Click points along the wall; double-click to finish.'],
  ['label', 'Text', 'Click where the text should go.'],
  ['mic', 'Place mic', 'Choose a mic below, then click where it is in the room.'],
];

const uid = () => Math.random().toString(36).slice(2, 10);
const rectPts = (a: Pt, b: Pt): Pt[] => [{ x: a.x, y: a.y }, { x: b.x, y: a.y }, { x: b.x, y: b.y }, { x: a.x, y: b.y }];

export function PlanPage() {
  const { live } = useApp();
  const { data, reload } = useFetch<Plan>('/api/hub/plan');
  const [plan, setPlan] = useState<Plan | null>(null);
  const [floorIdx, setFloorIdx] = useState(0);
  const [tool, setTool] = useState<Tool>('select');
  const [kind, setKind] = useState('room');
  const [sel, setSel] = useState<Hit | null>(null);
  const [draft, setDraft] = useState<Pt[] | null>(null);
  const [cursor, setCursor] = useState<Pt | null>(null);
  const [snap, setSnap] = useState(true);
  const [micToPlace, setMicToPlace] = useState('');
  const [dirty, setDirty] = useState(false);
  const [msg, setMsg] = useState('');
  const drag = useRef<{ start: Pt; hit: Hit; orig: Floor } | null>(null);

  useEffect(() => { if (data && !dirty) setPlan(data); }, [data, dirty]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.target as HTMLElement).closest('input,select,textarea')) return;
      if (e.key === 'Escape') { setDraft(null); setSel(null); }
      if ((e.key === 'Delete' || e.key === 'Backspace') && sel) { e.preventDefault(); removeSelected(); }
      if (e.key === 'Enter' && draft) finishDraft();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });
  useEffect(() => {
    const warn = (e: BeforeUnloadEvent) => { if (dirty) e.preventDefault(); };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [dirty]);

  if (!plan || !live) return <Empty>Loading…</Empty>;
  const floor = plan.floors[Math.min(floorIdx, plan.floors.length - 1)];
  const sn = (p: Pt): Pt => (snap ? { x: Math.round(p.x * 4) / 4, y: Math.round(p.y * 4) / 4 } : { x: +p.x.toFixed(2), y: +p.y.toFixed(2) });

  const setFloor = (f: Floor) => {
    setPlan({ ...plan, floors: plan.floors.map((x, i) => (i === floorIdx ? f : x)) });
    setDirty(true);
  };

  const addZone = (pts: Pt[]) => {
    const n = plan.floors.reduce((s, f) => s + f.zones.length, 0) + 1;
    const name = prompt('Zone name:', `Zone ${n}`);
    if (name == null) return;
    // Temporary negative ids; the Hub assigns real ones on save.
    const tmpId = -(1 + Math.floor(Math.random() * 1e9));
    setFloor({ ...floor, zones: [...floor.zones, { id: tmpId, name: name || `Zone ${n}`, points: pts, mode: 'Replace' }] });
    setSel({ type: 'zone', id: tmpId });
  };
  const addShape = (pts: Pt[], k = kind) => {
    const s: PlanShape = { id: uid(), kind: k, points: pts, label: null };
    setFloor({ ...floor, shapes: [...floor.shapes, s] });
    setSel({ type: 'shape', id: s.id });
  };

  function finishDraft() {
    if (!draft) return;
    const pts = draft;
    setDraft(null);
    if (tool === 'zone' && pts.length >= 3) addZone(pts);
    else if (tool === 'poly' && pts.length >= 3) addShape(pts);
    else if (tool === 'wall' && pts.length >= 2) addShape(pts, 'wall');
  }

  function removeSelected() {
    if (!sel) return;
    if (sel.type === 'zone') {
      const z = floor.zones.find((x) => x.id === sel.id);
      if (z && z.id > 0 && !confirm(`Delete zone "${z.name}"? Its surveys stay in the database but won't be used.`)) return;
      setFloor({ ...floor, zones: floor.zones.filter((x) => x.id !== sel.id) });
    } else if (sel.type === 'shape') setFloor({ ...floor, shapes: floor.shapes.filter((x) => x.id !== sel.id) });
    else if (sel.type === 'mic') setFloor({ ...floor, mics: floor.mics.filter((x) => x.micKey !== sel.key) });
    setSel(null);
  }

  const translate = (pts: Pt[], dx: number, dy: number) => pts.map((p) => ({ x: +(p.x + dx).toFixed(2), y: +(p.y + dy).toFixed(2) }));

  const onPointer = (kindEv: 'down' | 'move' | 'up' | 'dblclick', raw: Pt, hit: Hit) => {
    const p = sn(raw);
    if (kindEv === 'move') setCursor(p);

    if (tool === 'select') {
      if (kindEv === 'down') {
        if (hit.type === 'vertex') { drag.current = { start: p, hit, orig: floor }; return; }
        if (hit.type === 'zone' || hit.type === 'shape' || hit.type === 'mic') {
          setSel(hit);
          drag.current = { start: p, hit, orig: floor };
        } else setSel(null);
      } else if (kindEv === 'move' && drag.current) {
        const { start, hit: h, orig } = drag.current;
        const dx = p.x - start.x, dy = p.y - start.y;
        if (dx === 0 && dy === 0) return;
        if (h.type === 'vertex') {
          const setPt = (pts: Pt[]) => pts.map((q, i) => (i === h.index ? p : q));
          setFloor(h.owner === 'zone'
            ? { ...orig, zones: orig.zones.map((z) => (String(z.id) === h.id ? { ...z, points: setPt(z.points) } : z)) }
            : { ...orig, shapes: orig.shapes.map((s) => (s.id === h.id ? { ...s, points: setPt(s.points) } : s)) });
        } else if (h.type === 'zone') setFloor({ ...orig, zones: orig.zones.map((z) => (z.id === h.id ? { ...z, points: translate(z.points, dx, dy) } : z)) });
        else if (h.type === 'shape') setFloor({ ...orig, shapes: orig.shapes.map((s) => (s.id === h.id ? { ...s, points: translate(s.points, dx, dy) } : s)) });
        else if (h.type === 'mic') setFloor({ ...orig, mics: orig.mics.map((m) => (m.micKey === h.key ? { ...m, x: +(m.x + dx).toFixed(2), y: +(m.y + dy).toFixed(2) } : m)) });
      } else if (kindEv === 'up') drag.current = null;
      return;
    }

    if (tool === 'rect' || tool === 'zone-rect') {
      if (kindEv === 'down') { drag.current = { start: p, hit, orig: floor }; setDraft(rectPts(p, p)); }
      else if (kindEv === 'move' && drag.current) setDraft(rectPts(drag.current.start, p));
      else if (kindEv === 'up' && drag.current) {
        const a = drag.current.start;
        drag.current = null;
        setDraft(null);
        if (Math.abs(p.x - a.x) < 0.3 || Math.abs(p.y - a.y) < 0.3) return;
        if (tool === 'zone-rect') addZone(rectPts(a, p)); else addShape(rectPts(a, p));
      }
      return;
    }

    if (tool === 'zone' || tool === 'poly' || tool === 'wall') {
      if (kindEv === 'down') {
        const pts = draft ?? [];
        if (tool !== 'wall' && pts.length >= 3 && Math.hypot(pts[0].x - p.x, pts[0].y - p.y) < 0.5) { finishDraft(); return; }
        setDraft([...pts, p]);
      } else if (kindEv === 'dblclick') finishDraft();
      return;
    }

    if (tool === 'label' && kindEv === 'down') {
      const text = prompt('Text:');
      if (text) setFloor({ ...floor, shapes: [...floor.shapes, { id: uid(), kind: 'label', points: [p], label: text }] });
      return;
    }

    if (tool === 'mic' && kindEv === 'down') {
      if (!micToPlace) { setMsg('Choose a mic to place first.'); return; }
      const role = micToPlace.startsWith('rover:') ? 'Roving' : 'Permanent';
      const key = micToPlace.replace(/^rover:/, '');
      // A mic can only be in one place: remove it from every floor first.
      const floors = plan.floors.map((f, i) => {
        const rest = f.mics.filter((m) => m.micKey !== key);
        return i === floorIdx ? { ...f, mics: [...rest, { micKey: key, x: p.x, y: p.y, role }] } : { ...f, mics: rest };
      });
      setPlan({ ...plan, floors: floors as Floor[] });
      setDirty(true);
      setSel({ type: 'mic', key });
      setMicToPlace('');
      setTool('select');
    }
  };

  const save = async () => {
    try {
      const saved = await api<Plan>('/api/hub/plan', { method: 'PUT', json: plan });
      setPlan(saved);
      setDirty(false);
      setMsg('Saved.');
      reload();
    } catch (e) { setMsg((e as Error).message); }
  };

  const uploadUnderlay = async (file: File) => {
    try {
      const { id } = await api<{ id: string }>('/api/hub/plan/images', { method: 'POST', body: file, headers: { 'Content-Type': 'application/octet-stream' } });
      const img = new Image();
      img.src = URL.createObjectURL(file);
      await img.decode();
      const aspect = img.naturalHeight / img.naturalWidth;
      const w = floor.width, h = +(w * aspect).toFixed(2);
      setFloor({ ...floor, underlay: { imageId: id, x: 0, y: 0, width: w, height: h, opacity: 0.45 }, height: Math.max(floor.height, Math.ceil(h)) });
      setMsg('Image added. Use its width and position to line it up with the grid, then trace over it.');
    } catch (e) { setMsg((e as Error).message); }
  };

  const selZone = sel?.type === 'zone' ? floor.zones.find((z) => z.id === sel.id) : undefined;
  const selShape = sel?.type === 'shape' ? floor.shapes.find((s) => s.id === sel.id) : undefined;
  const selMic = sel?.type === 'mic' ? floor.mics.find((m) => m.micKey === sel.key) : undefined;
  const placed = new Set(plan.floors.flatMap((f) => f.mics.map((m) => m.micKey)));
  const micLabel = (key: string) => {
    const m = live.mics.find((x) => x.key === key);
    return m ? `${m.label}${m.micLabel ? ` · ${m.micLabel}` : ''} (${m.nodeName})` : key;
  };
  const draftShown = draft && cursor && (tool === 'zone' || tool === 'poly' || tool === 'wall') ? [...draft, cursor] : draft;

  return (
    <>
      <div className="page-head">
        <h2>Plan editor</h2>
        {plan.floors.map((f, i) => (
          <button key={f.id} className={i === floorIdx ? 'seg active' : 'seg'} onClick={() => { setFloorIdx(i); setSel(null); setDraft(null); }}>{f.name}</button>
        ))}
        <button className="ghost" onClick={() => {
          const name = prompt('New floor name:', 'Balcony');
          if (!name) return;
          setPlan({ ...plan, floors: [...plan.floors, { id: uid(), name, width: floor.width, height: floor.height, underlay: null, shapes: [], zones: [], mics: [] }] });
          setFloorIdx(plan.floors.length);
          setDirty(true);
        }}>+ Floor</button>
        <span className="spacer" />
        {dirty && <span className="warn small">Unsaved changes</span>}
        <span className="muted small">{msg}</span>
        <button className="primary" disabled={!dirty} onClick={save}>Save plan</button>
      </div>

      <div className="toolbar wrap">
        {TOOLS.map(([t, label]) => (
          <button key={t} className={tool === t ? 'seg active' : 'seg'} onClick={() => { setTool(t); setDraft(null); }}>{label}</button>
        ))}
        {(tool === 'rect' || tool === 'poly') && (
          <select value={kind} onChange={(e) => setKind(e.target.value)}>
            {['room', 'stage', 'seating', 'aisle', 'other'].map((k) => <option key={k} value={k}>{SHAPE_STYLE[k].label}</option>)}
          </select>
        )}
        {tool === 'mic' && (
          <select value={micToPlace} onChange={(e) => setMicToPlace(e.target.value)}>
            <option value="">Choose a mic…</option>
            <optgroup label="Permanent (used to predict zones)">
              {live.mics.map((m) => <option key={m.key} value={m.key}>{placed.has(m.key) ? '✓ ' : ''}{micLabel(m.key)}</option>)}
            </optgroup>
            <optgroup label="Roving (survey mic)">
              {live.mics.map((m) => <option key={'r' + m.key} value={`rover:${m.key}`}>{micLabel(m.key)}</option>)}
            </optgroup>
          </select>
        )}
        <label className="check small"><input type="checkbox" checked={snap} onChange={(e) => setSnap(e.target.checked)} /> Snap to 25 cm</label>
        {draft && (tool === 'zone' || tool === 'poly' || tool === 'wall') && <button onClick={finishDraft}>Finish shape</button>}
        {draft && <button className="ghost" onClick={() => setDraft(null)}>Cancel</button>}
      </div>
      <p className="muted small">{TOOLS.find((t) => t[0] === tool)?.[2]} {cursor && <span>· {cursor.x.toFixed(2)} m, {cursor.y.toFixed(2)} m</span>}</p>

      <div className="room-layout">
        <section className="panel room-plan">
          <Blueprint floor={floor} height={600} selected={sel} draft={draftShown} draftClosed={tool !== 'wall'} showVertices={tool === 'select'}
            underlayUrl={floor.underlay ? `/api/hub/plan/images/${floor.underlay.imageId}` : undefined}
            micText={(m) => micLabel(m.micKey).split(' (')[0] + (m.role === 'Roving' ? ' (roving)' : '')}
            micTone={(m) => (m.role === 'Roving' ? 'rover' : 'ok')}
            onPointer={onPointer} />
        </section>

        <section className="panel room-list">
          {selZone && (
            <>
              <h3>Zone</h3>
              <div className="form-grid one">
                <label>Name <input value={selZone.name} onChange={(e) => setFloor({ ...floor, zones: floor.zones.map((z) => (z.id === selZone.id ? { ...z, name: e.target.value } : z)) })} /></label>
                <label>When resurveyed
                  <select value={selZone.mode} onChange={(e) => setFloor({ ...floor, zones: floor.zones.map((z) => (z.id === selZone.id ? { ...z, mode: e.target.value as 'Replace' | 'Average' } : z)) })}>
                    <option value="Replace">Use the newest survey</option>
                    <option value="Average">Average all surveys</option>
                  </select>
                </label>
              </div>
              <div className="actions"><button className="ghost" onClick={removeSelected}>Delete zone</button></div>
            </>
          )}
          {selShape && (
            <>
              <h3>{SHAPE_STYLE[selShape.kind]?.label ?? 'Shape'}</h3>
              <div className="form-grid one">
                {selShape.kind !== 'wall' && selShape.kind !== 'label' && (
                  <label>Type
                    <select value={selShape.kind} onChange={(e) => setFloor({ ...floor, shapes: floor.shapes.map((s) => (s.id === selShape.id ? { ...s, kind: e.target.value } : s)) })}>
                      {['room', 'stage', 'seating', 'aisle', 'other'].map((k) => <option key={k} value={k}>{SHAPE_STYLE[k].label}</option>)}
                    </select>
                  </label>
                )}
                <label>{selShape.kind === 'label' ? 'Text' : 'Label (optional)'}
                  <input value={selShape.label ?? ''} onChange={(e) => setFloor({ ...floor, shapes: floor.shapes.map((s) => (s.id === selShape.id ? { ...s, label: e.target.value || null } : s)) })} />
                </label>
              </div>
              <div className="actions"><button className="ghost" onClick={removeSelected}>Delete</button></div>
            </>
          )}
          {selMic && (
            <>
              <h3>Mic</h3>
              <p>{micLabel(selMic.micKey)}</p>
              <div className="form-grid one">
                <label>Role
                  <select value={selMic.role} onChange={(e) => setFloor({ ...floor, mics: floor.mics.map((m) => (m.micKey === selMic.micKey ? { ...m, role: e.target.value as 'Permanent' | 'Roving' } : m)) })}>
                    <option value="Permanent">Permanent: predicts zones</option>
                    <option value="Roving">Roving: survey mic</option>
                  </select>
                </label>
              </div>
              <p className="muted small">Moving a permanent mic after surveying flags its zones for a resurvey.</p>
              <div className="actions"><button className="ghost" onClick={removeSelected}>Remove from plan</button></div>
            </>
          )}

          <h3>Floor</h3>
          <div className="form-grid one">
            <label>Name <input value={floor.name} onChange={(e) => setFloor({ ...floor, name: e.target.value })} /></label>
            <div className="two-col">
              <label>Width (m) <input type="number" min={1} max={1000} value={floor.width} onChange={(e) => setFloor({ ...floor, width: Math.max(1, +e.target.value) })} /></label>
              <label>Depth (m) <input type="number" min={1} max={1000} value={floor.height} onChange={(e) => setFloor({ ...floor, height: Math.max(1, +e.target.value) })} /></label>
            </div>
          </div>
          {plan.floors.length > 1 && (
            <div className="actions"><button className="ghost" onClick={() => {
              if (!confirm(`Delete floor "${floor.name}" and everything on it?`)) return;
              setPlan({ ...plan, floors: plan.floors.filter((_, i) => i !== floorIdx) });
              setFloorIdx(0);
              setDirty(true);
            }}>Delete floor</button></div>
          )}

          <h3>Background image</h3>
          {!floor.underlay ? (
            <>
              <p className="muted small">Optional: add a photo or exported floor plan (PNG/JPEG/WebP) to trace over.</p>
              <label className="filebtn">Add image… <input type="file" accept="image/png,image/jpeg,image/webp" onChange={(e) => e.target.files?.[0] && uploadUnderlay(e.target.files[0])} /></label>
            </>
          ) : (
            <div className="form-grid one">
              <label>Opacity <input type="range" min={0.05} max={1} step={0.05} value={floor.underlay.opacity} onChange={(e) => setFloor({ ...floor, underlay: { ...floor.underlay!, opacity: +e.target.value } })} /></label>
              <div className="two-col">
                <label>Width (m) <input type="number" step={0.1} value={floor.underlay.width} onChange={(e) => {
                  const w = +e.target.value, u = floor.underlay!;
                  setFloor({ ...floor, underlay: { ...u, width: w, height: +(u.height * (w / u.width)).toFixed(2) } });
                }} /></label>
                <label>Left / top (m)
                  <span className="two-col">
                    <input type="number" step={0.1} value={floor.underlay.x} onChange={(e) => setFloor({ ...floor, underlay: { ...floor.underlay!, x: +e.target.value } })} />
                    <input type="number" step={0.1} value={floor.underlay.y} onChange={(e) => setFloor({ ...floor, underlay: { ...floor.underlay!, y: +e.target.value } })} />
                  </span>
                </label>
              </div>
              <div className="actions"><button className="ghost" onClick={() => setFloor({ ...floor, underlay: null })}>Remove image</button></div>
            </div>
          )}

          <h3>Mics on this plan</h3>
          {plan.floors.flatMap((f) => f.mics).length === 0
            ? <p className="muted small">None yet. Use “Place mic” to put each permanent mic where it is in the room, and the roving survey mic anywhere.</p>
            : (
              <ul className="plain small">
                {plan.floors.flatMap((f) => f.mics.map((m) => (
                  <li key={m.micKey}>{m.role === 'Roving' ? '◎' : '●'} {micLabel(m.micKey)} <span className="muted">· {f.name}{m.role === 'Roving' ? ' · roving' : ''}</span></li>
                )))}
              </ul>
            )}
        </section>
      </div>
    </>
  );
}
