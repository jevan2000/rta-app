import { useEffect, useState } from 'react';
import { api, fmtTime } from '../api';
import { useApp } from '../App';
import { Empty } from '../components/common';
import { useFetch } from '../hooks';
import { NdiSection } from '../components/NdiSection';

interface NetTarget { host: string; port: number }
interface MidiMap { source: string; metric: string; cc: number; minDb: number; maxDb: number }
interface Integrations {
  osc: { enabled: boolean; targets: NetTarget[]; prefix: string; rateHz: number };
  midi: { enabled: boolean; device: string | null; channel: number; mappings: MidiMap[]; warnNote: number; alarmNote: number };
  webhooks: { enabled: boolean; urls: string[]; onAlertClear: boolean; onService: boolean };
  proPresenter: {
    enabled: boolean; host: string; port: number; stageAlarms: boolean; stageWarnings: boolean; stageReadout: boolean;
    readoutSources: string[]; readoutMetric: string; messageReadout: boolean; messageId: string | null; tokenName: string;
  };
  reports: { autoGenerate: boolean; folder: string | null };
}
type Status = Record<string, { ok: boolean; message: string; at: string }>;

const METRICS: [string, string][] = [
  ['LCeq1m', 'dBC 1 min'], ['LCS', 'dBC Slow'], ['LCeq15m', 'dBC 15 min'], ['LCeqService', 'dBC service'],
  ['LAeq1m', 'dBA 1 min'], ['LAS', 'dBA Slow'], ['LAeq15m', 'dBA 15 min'], ['LAeqService', 'dBA service'],
];

export function IntegrationsPage() {
  const { live } = useApp();
  const { data, reload } = useFetch<Integrations>('/api/hub/integrations');
  const { data: status } = useFetch<Status>('/api/hub/integrations/status', 2000);
  const [f, setF] = useState<Integrations | null>(null);
  const [dirty, setDirty] = useState(false);
  const [msg, setMsg] = useState('');
  useEffect(() => { if (data && !dirty) setF(data); }, [data, dirty]);
  if (!f || !live) return <Empty>Loading…</Empty>;

  const set = <K extends keyof Integrations>(k: K, v: Partial<Integrations[K]>) => { setF({ ...f, [k]: { ...f[k], ...v } }); setDirty(true); };
  const sources = [
    ...live.mics.filter((m) => !m.hidden).map((m) => ({ key: m.key, label: `${m.label} (${m.nodeName})` })),
    ...live.zones.filter((z) => z.surveyed).map((z) => ({ key: z.key, label: `Zone: ${z.name}` })),
  ];

  const save = async () => {
    try { await api('/api/hub/integrations', { method: 'PUT', json: f }); setDirty(false); setMsg('Saved.'); reload(); }
    catch (e) { setMsg((e as Error).message); }
  };

  return (
    <>
      <div className="page-head">
        <h2>Integrations</h2>
        <span className="spacer" />
        {dirty && <span className="warn small">Unsaved changes</span>}
        <span className="muted small">{msg}</span>
        <button className="primary" disabled={!dirty} onClick={save}>Save</button>
      </div>
      <p className="muted">Send levels and alerts to other systems. Save your changes before pressing Test.</p>

      <ProPresenterSection f={f} set={set} status={status?.propresenter} sources={sources} dirty={dirty} />

      <section className="panel">
        <SectionHead title="OSC" on={f.osc.enabled} onToggle={(v) => set('osc', { enabled: v })} status={status?.osc} test="osc" dirty={dirty} />
        <p className="muted small">For Bitfocus Companion, QLab, consoles and show-control software. Sends <code>{f.osc.prefix}/mic/&lt;name&gt;/lceq1m</code> (and lcs, laeq1m, lceq15m, lceqservice, cminusa…) as floats, <code>{f.osc.prefix}/zone/&lt;name&gt;/…</code> for zones, <code>…/alert</code> as 0/1/2, and <code>{f.osc.prefix}/alert</code> (source, level, value, rule) the moment an alert changes.</p>
        <div className="form-grid">
          <label>Address prefix <input value={f.osc.prefix} onChange={(e) => set('osc', { prefix: e.target.value })} /></label>
          <label>Level updates per second <input type="number" min={0.2} max={20} step={0.5} value={f.osc.rateHz} onChange={(e) => set('osc', { rateHz: +e.target.value })} /></label>
        </div>
        <h4>Targets</h4>
        {f.osc.targets.map((t, i) => (
          <div key={i} className="inline-row">
            <input placeholder="IP or host" value={t.host} onChange={(e) => set('osc', { targets: f.osc.targets.map((x, k) => (k === i ? { ...x, host: e.target.value } : x)) })} />
            <input type="number" className="num" value={t.port} onChange={(e) => set('osc', { targets: f.osc.targets.map((x, k) => (k === i ? { ...x, port: +e.target.value } : x)) })} />
            <button className="ghost" onClick={() => set('osc', { targets: f.osc.targets.filter((_, k) => k !== i) })}>Remove</button>
          </div>
        ))}
        <button className="ghost" onClick={() => set('osc', { targets: [...f.osc.targets, { host: '127.0.0.1', port: 9000 }] })}>+ Target</button>
        <SourceNames live={live} />
      </section>

      <section className="panel">
        <SectionHead title="HTTP & WebSocket (read-only, always on)" />
        <p className="muted small">No sign-in needed, so other apps can read levels:</p>
        <ul className="small plain">
          <li><code>GET /api/v1/levels</code>: every mic and zone, with alerts and service (JSON)</li>
          <li><code>GET /api/v1/levels/&lt;name&gt;</code>: one source (JSON)</li>
          <li><code>GET /api/v1/levels/&lt;name&gt;/lceq1m</code>: just the number as text, e.g. <code>84.2</code>. Handy for a vMix/OBS text source or a Companion variable.</li>
          <li><code>ws://&lt;hub&gt;:5180/ws/v1/levels?hz=2</code>: the same JSON, streamed</li>
        </ul>
      </section>

      <MidiSection f={f} set={set} status={status?.midi} sources={sources} dirty={dirty} />

      <section className="panel">
        <SectionHead title="Webhooks" on={f.webhooks.enabled} onToggle={(v) => set('webhooks', { enabled: v })} status={status?.webhooks} test="webhook" dirty={dirty} />
        <p className="muted small">POSTs JSON (<code>event</code>: alert.start / alert.escalate / alert.clear / service.start / service.end) to each URL, for Teams/Slack workflows, Home Assistant, Zapier and similar. Retries twice.</p>
        {f.webhooks.urls.map((u, i) => (
          <div key={i} className="inline-row">
            <input className="grow" value={u} placeholder="https://…" onChange={(e) => set('webhooks', { urls: f.webhooks.urls.map((x, k) => (k === i ? e.target.value : x)) })} />
            <button className="ghost" onClick={() => set('webhooks', { urls: f.webhooks.urls.filter((_, k) => k !== i) })}>Remove</button>
          </div>
        ))}
        <button className="ghost" onClick={() => set('webhooks', { urls: [...f.webhooks.urls, ''] })}>+ URL</button>
        <div className="form-grid">
          <label className="check"><input type="checkbox" checked={f.webhooks.onAlertClear} onChange={(e) => set('webhooks', { onAlertClear: e.target.checked })} /> Also when an alert clears</label>
          <label className="check"><input type="checkbox" checked={f.webhooks.onService} onChange={(e) => set('webhooks', { onService: e.target.checked })} /> Service start and end</label>
        </div>
      </section>

      <NdiSection />

      <ReportsSection f={f} set={set} />
    </>
  );
}

function SectionHead({ title, on, onToggle, status, test, dirty }: {
  title: string; on?: boolean; onToggle?: (v: boolean) => void; status?: { ok: boolean; message: string; at: string }; test?: string; dirty?: boolean;
}) {
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState('');
  return (
    <>
      <div className="node-head">
        <h3>{title}</h3>
        {onToggle && <label className="check small"><input type="checkbox" checked={!!on} onChange={(e) => onToggle(e.target.checked)} /> Enabled</label>}
        {on && status && <span className={`pill ${status.ok ? 'ok' : 'err'}`} title={new Date(status.at).toLocaleTimeString()}>{status.ok ? 'working' : 'problem'}</span>}
        <span className="spacer" />
        {test && <button disabled={busy || dirty} title={dirty ? 'Save first' : ''} onClick={async () => {
          setBusy(true); setResult('');
          try { setResult((await api<{ message: string }>(`/api/hub/integrations/test/${test}`, { method: 'POST' })).message); }
          catch (e) { setResult((e as Error).message); } finally { setBusy(false); }
        }}>Test</button>}
      </div>
      {on && status && <p className={`small ${status.ok ? 'muted' : 'warn'}`}>{status.message}</p>}
      {result && <p className="small">{result}</p>}
    </>
  );
}

function ProPresenterSection({ f, set, status, sources, dirty }: {
  f: Integrations; set: <K extends keyof Integrations>(k: K, v: Partial<Integrations[K]>) => void;
  status?: { ok: boolean; message: string; at: string }; sources: { key: string; label: string }[]; dirty: boolean;
}) {
  const p = f.proPresenter;
  const [messages, setMessages] = useState<{ id: string; name: string; tokens: string[] }[] | null>(null);
  const [mErr, setMErr] = useState('');
  return (
    <section className="panel">
      <SectionHead title="ProPresenter" on={p.enabled} onToggle={(v) => set('proPresenter', { enabled: v })} status={status} test="propresenter" dirty={dirty} />
      <p className="muted small">Works with ProPresenter 7.9 and later (including 21 and 22). In ProPresenter: Settings → Network → Enable Network, and note the IP and port. Alarms show on the <b>stage display</b> only.</p>
      <div className="form-grid">
        <label>ProPresenter IP / host <input value={p.host} onChange={(e) => set('proPresenter', { host: e.target.value })} /></label>
        <label>Port <input type="number" value={p.port} onChange={(e) => set('proPresenter', { port: +e.target.value })} /></label>
      </div>
      <div className="form-grid">
        <label className="check"><input type="checkbox" checked={p.stageAlarms} onChange={(e) => set('proPresenter', { stageAlarms: e.target.checked })} /> Stage message on alarms</label>
        <label className="check"><input type="checkbox" checked={p.stageWarnings} onChange={(e) => set('proPresenter', { stageWarnings: e.target.checked })} /> …and on warnings</label>
        <label className="check"><input type="checkbox" checked={p.stageReadout} onChange={(e) => set('proPresenter', { stageReadout: e.target.checked })} /> Live level line on the stage display</label>
      </div>
      <h4>Levels to show</h4>
      <div className="form-grid">
        <label>Measure
          <select value={p.readoutMetric} onChange={(e) => set('proPresenter', { readoutMetric: e.target.value })}>
            {METRICS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}
          </select>
        </label>
      </div>
      <div className="mic-picker">
        {sources.map((s) => (
          <label key={s.key} className="check small">
            <input type="checkbox" checked={p.readoutSources.includes(s.key)}
              onChange={(e) => set('proPresenter', { readoutSources: e.target.checked ? [...p.readoutSources, s.key] : p.readoutSources.filter((k) => k !== s.key) })} />
            {s.label}
          </label>
        ))}
      </div>
      <details>
        <summary className="small">Show levels on a ProPresenter Message (can appear on the audience screen)</summary>
        <p className="warn small">Off by default. A Message appears wherever it’s set to show in ProPresenter, often the main/audience output. Make a Message with a text token (e.g. <code>{'{level}'}</code>) and choose it here. ProPresenter’s token format can differ between versions, so check it with Test after setting it up.</p>
        <label className="check small"><input type="checkbox" checked={p.messageReadout} onChange={(e) => set('proPresenter', { messageReadout: e.target.checked })} /> Update this Message with the levels</label>
        <div className="form-grid">
          <label>Message
            <span className="inline-row">
              <select value={p.messageId ?? ''} onChange={(e) => set('proPresenter', { messageId: e.target.value || null })}>
                <option value="">Choose…</option>
                {p.messageId && !messages?.some((m) => m.id === p.messageId) && <option value={p.messageId}>{p.messageId}</option>}
                {messages?.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
              </select>
              <button className="ghost" onClick={async () => {
                setMErr('');
                try { setMessages(await api('/api/hub/propresenter/messages')); } catch (e) { setMErr((e as Error).message); }
              }}>Load</button>
            </span>
          </label>
          <label>Token name <input value={p.tokenName} onChange={(e) => set('proPresenter', { tokenName: e.target.value })} /></label>
        </div>
        {mErr && <p className="warn small">{mErr}</p>}
      </details>
    </section>
  );
}

function MidiSection({ f, set, status, sources, dirty }: {
  f: Integrations; set: <K extends keyof Integrations>(k: K, v: Partial<Integrations[K]>) => void;
  status?: { ok: boolean; message: string; at: string }; sources: { key: string; label: string }[]; dirty: boolean;
}) {
  const m = f.midi;
  const { data: devices } = useFetch<string[]>('/api/hub/midi/devices');
  const upd = (i: number, v: Partial<MidiMap>) => set('midi', { mappings: m.mappings.map((x, k) => (k === i ? { ...x, ...v } : x)) });
  return (
    <section className="panel">
      <SectionHead title="MIDI" on={m.enabled} onToggle={(v) => set('midi', { enabled: v })} status={status} test="midi" dirty={dirty} />
      <p className="muted small">Sends a control change per level (scaled over a dB range to 0–127), and holds a note on while any warning or alarm is active. Use a hardware port or a virtual one (e.g. loopMIDI) to reach other software.</p>
      <div className="form-grid">
        <label>Output
          <select value={m.device ?? ''} onChange={(e) => set('midi', { device: e.target.value || null })}>
            <option value="">Choose…</option>
            {(devices ?? []).map((d) => <option key={d} value={d}>{d}</option>)}
          </select>
        </label>
        <label>Channel <input type="number" min={1} max={16} value={m.channel} onChange={(e) => set('midi', { channel: +e.target.value })} /></label>
        <label>Warning note (-1 = none) <input type="number" min={-1} max={127} value={m.warnNote} onChange={(e) => set('midi', { warnNote: +e.target.value })} /></label>
        <label>Alarm note (-1 = none) <input type="number" min={-1} max={127} value={m.alarmNote} onChange={(e) => set('midi', { alarmNote: +e.target.value })} /></label>
      </div>
      {devices && devices.some((d) => /GS Wavetable/i.test(d)) && <p className="muted small">“Microsoft GS Wavetable Synth” is a built-in software synth: it will play notes through the PC’s speakers.</p>}
      <h4>Level → CC</h4>
      <div className="table-scroll">
        <table className="grid">
          <thead><tr><th>Source</th><th>Measure</th><th>CC</th><th>dB at 0</th><th>dB at 127</th><th /></tr></thead>
          <tbody>
            {m.mappings.map((x, i) => (
              <tr key={i}>
                <td><select value={x.source} onChange={(e) => upd(i, { source: e.target.value })}>{sources.map((s) => <option key={s.key} value={s.key}>{s.label}</option>)}</select></td>
                <td><select value={x.metric} onChange={(e) => upd(i, { metric: e.target.value })}>{METRICS.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select></td>
                <td><input type="number" className="num" min={0} max={127} value={x.cc} onChange={(e) => upd(i, { cc: +e.target.value })} /></td>
                <td><input type="number" className="num" value={x.minDb} onChange={(e) => upd(i, { minDb: +e.target.value })} /></td>
                <td><input type="number" className="num" value={x.maxDb} onChange={(e) => upd(i, { maxDb: +e.target.value })} /></td>
                <td><button className="ghost" onClick={() => set('midi', { mappings: m.mappings.filter((_, k) => k !== i) })}>Remove</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <button className="ghost" disabled={!sources.length} onClick={() => set('midi', { mappings: [...m.mappings, { source: sources[0].key, metric: 'LCeq1m', cc: 20 + m.mappings.length, minDb: 60, maxDb: 110 }] })}>+ Mapping</button>
    </section>
  );
}

function ReportsSection({ f, set }: { f: Integrations; set: <K extends keyof Integrations>(k: K, v: Partial<Integrations[K]>) => void }) {
  const { data } = useFetch<{ folder: string; files: { name: string; bytes: number; modified: string }[] }>('/api/hub/reports', 30000);
  return (
    <section className="panel">
      <h3>Service reports</h3>
      <div className="form-grid">
        <label className="check"><input type="checkbox" checked={f.reports.autoGenerate} onChange={(e) => set('reports', { autoGenerate: e.target.checked })} /> Save a PDF automatically when each service ends</label>
        <label>Folder (blank = app data folder)
          <input value={f.reports.folder ?? ''} placeholder={data?.folder} onChange={(e) => set('reports', { folder: e.target.value || null })} />
        </label>
      </div>
      <p className="muted small">The folder is on the Hub PC: a shared or synced folder (OneDrive, a network share) makes reports easy to pass on.</p>
      {data && data.files.length > 0 && (
        <table className="grid">
          <thead><tr><th>Report</th><th>Saved</th><th /></tr></thead>
          <tbody>
            {data.files.slice(0, 20).map((r) => (
              <tr key={r.name}>
                <td><a href={`/api/hub/reports/${encodeURIComponent(r.name)}`} target="_blank" rel="noreferrer">{r.name}</a></td>
                <td className="muted">{fmtTime(Date.parse(r.modified) / 1000, true)}</td>
                <td><a href={`/api/hub/reports/${encodeURIComponent(r.name)}?download=1`}>Download</a></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
  );
}

function SourceNames({ live }: { live: NonNullable<ReturnType<typeof useApp>['live']> }) {
  const { data } = useFetch<{ mics: { slug: string; label: string }[]; zones: { slug: string; label: string }[] }>('/api/v1/levels', 10000);
  if (!data) return null;
  return (
    <details>
      <summary className="small">Names used in addresses ({data.mics.length + data.zones.length})</summary>
      <ul className="small plain">
        {data.mics.map((m) => <li key={'m' + m.slug}><code>mic/{m.slug}</code> <span className="muted">{m.label}</span></li>)}
        {data.zones.map((z) => <li key={'z' + z.slug}><code>zone/{z.slug}</code> <span className="muted">{z.label}</span></li>)}
      </ul>
      <p className="muted small">Names come from the mic and zone names{live.mics.length ? '; rename them on the Nodes and Plan pages' : ''}.</p>
    </details>
  );
}
