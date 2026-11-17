import { useEffect, useState } from 'react';
import { api } from '../api';
import { useFetch } from '../hooks';

interface HubNdi { dashboard: boolean; dashboardWidth: number; dashboardHeight: number; dashboardFps: number; dashboardZones: boolean; zoneFeeds: boolean; zoneFps: number }
interface NdiStatus { runtimeAvailable: boolean; runtimeMessage: string; senders: { name: string; kind: string; connections: number; estimatedMbps: number }[]; estimatedMbps: number; warnings: string[] }
interface Probe { source: string; connected: boolean; width: number | null; height: number | null; fourCC: string | null; audioChannels: number | null; audioRate: number | null; metadata: string | null; message: string }

const SIZES: [number, number, string][] = [[1920, 1080, '1920 × 1080 (1080p)'], [1280, 720, '1280 × 720 (720p)'], [3840, 2160, '3840 × 2160 (4K)']];

/** NDI outputs from the Hub (dashboard and zone feeds), status and a source finder. */
export function NdiSection() {
  const { data, reload } = useFetch<HubNdi>('/api/hub/ndi');
  const { data: status } = useFetch<NdiStatus>('/api/ndi/status', 3000);
  const [f, setF] = useState<HubNdi | null>(null);
  const [dirty, setDirty] = useState(false);
  const [msg, setMsg] = useState('');
  const [sources, setSources] = useState<{ name: string; url: string | null }[] | null>(null);
  const [finding, setFinding] = useState(false);
  const [probes, setProbes] = useState<Record<string, Probe | 'busy'>>({});
  useEffect(() => { if (data && !dirty) setF(data); }, [data, dirty]);
  if (!f) return null;
  const set = (v: Partial<HubNdi>) => { setF({ ...f, ...v }); setDirty(true); };
  const save = async () => {
    try { await api('/api/hub/ndi', { method: 'PUT', json: f }); setDirty(false); setMsg('Saved.'); reload(); }
    catch (e) { setMsg((e as Error).message); }
  };
  const find = async () => {
    setFinding(true);
    try { setSources(await api('/api/ndi/sources')); } catch (e) { setMsg((e as Error).message); } finally { setFinding(false); }
  };
  const probe = async (name: string) => {
    setProbes((p) => ({ ...p, [name]: 'busy' }));
    try { const r = await api<Probe>(`/api/ndi/probe?source=${encodeURIComponent(name)}`); setProbes((p) => ({ ...p, [name]: r })); }
    catch (e) { setProbes((p) => ({ ...p, [name]: { source: name, connected: false, message: (e as Error).message } as Probe })); }
  };
  const size = `${f.dashboardWidth}x${f.dashboardHeight}`;

  return (
    <section className="panel">
      <div className="node-head">
        <h3>NDI video</h3>
        {status && <span className={`pill ${status.runtimeAvailable ? 'ok' : 'err'}`}>{status.runtimeAvailable ? 'runtime found' : 'no runtime'}</span>}
        <span className="spacer" />
        {dirty && <span className="warn small">Unsaved</span>}
        <span className="muted small">{msg}</span>
        <button className="primary" disabled={!dirty} onClick={save}>Save NDI</button>
      </div>
      {status && !status.runtimeAvailable && <p className="warn small">{status.runtimeMessage}</p>}
      <p className="muted small">Feeds for vMix, OBS, ProPresenter (as a live video input), or a confidence monitor. Raw audio and per-mic meters are switched on per input on each node’s page (Settings → Inputs). NDI® is a registered trademark of Vizrt NDI AB.</p>
      <div className="form-grid">
        <label className="check"><input type="checkbox" checked={f.dashboard} onChange={(e) => set({ dashboard: e.target.checked })} /> “RTA Dashboard”: every mic on one frame</label>
        <label>Size
          <select value={size} onChange={(e) => { const [w, h] = e.target.value.split('x').map(Number); set({ dashboardWidth: w, dashboardHeight: h }); }}>
            {SIZES.map(([w, h, l]) => <option key={l} value={`${w}x${h}`}>{l}</option>)}
          </select>
        </label>
        <label>Frames per second <input type="number" min={1} max={30} value={f.dashboardFps} onChange={(e) => set({ dashboardFps: +e.target.value })} /></label>
        <label className="check"><input type="checkbox" checked={f.dashboardZones} onChange={(e) => set({ dashboardZones: e.target.checked })} /> Include predicted zones</label>
        <label className="check"><input type="checkbox" checked={f.zoneFeeds} onChange={(e) => set({ zoneFeeds: e.target.checked })} /> A separate “RTA Zone &lt;name&gt;” feed per zone</label>
        <label>Zone feed frames per second <input type="number" min={1} max={30} value={f.zoneFps} onChange={(e) => set({ zoneFps: +e.target.value })} /></label>
      </div>
      <p className="muted small">Levels change slowly, so 5–10 fps is plenty and keeps network use low.</p>

      {status && (status.senders.length > 0 || status.warnings.length > 0) && (
        <>
          <h4>Publishing from this PC (about {status.estimatedMbps} Mb/s at most)</h4>
          <ul className="plain small">
            {status.senders.map((s) => <li key={s.name}>{s.name} <span className="muted">· {s.kind} · {s.connections} receiver{s.connections === 1 ? '' : 's'} · ≤{s.estimatedMbps.toFixed(1)} Mb/s</span></li>)}
          </ul>
          {status.warnings.map((w) => <p key={w} className="warn small">{w}</p>)}
        </>
      )}

      <h4>NDI sources on the network</h4>
      <button disabled={finding || !status?.runtimeAvailable} onClick={find}>{finding ? 'Looking…' : 'Find sources'}</button>
      {sources && (sources.length === 0 ? <p className="muted small">None found. Check the PCs are on the same network and NDI isn't blocked by a firewall.</p> : (
        <table className="grid">
          <thead><tr><th>Source</th><th>Address</th><th /></tr></thead>
          <tbody>
            {sources.map((s) => {
              const p = probes[s.name];
              return (
                <tr key={s.name}>
                  <td>{s.name}{p && p !== 'busy' && <div className={`small ${p.connected ? 'muted' : 'warn'}`}>
                    {p.message}{p.width ? ` Video ${p.width}×${p.height} ${p.fourCC ?? ''}.` : ''}{p.audioChannels ? ` Audio ${p.audioChannels} ch @ ${p.audioRate} Hz.` : ''}{p.metadata ? ` Data: ${p.metadata}` : ''}
                  </div>}</td>
                  <td className="muted small">{s.url}</td>
                  <td><button className="ghost" disabled={p === 'busy'} onClick={() => probe(s.name)}>{p === 'busy' ? 'Checking…' : 'Check'}</button></td>
                </tr>
              );
            })}
          </tbody>
        </table>
      ))}
    </section>
  );
}
