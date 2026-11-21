import { useEffect, useRef, useState } from 'react';
import { api } from '../api';
import { useApp } from '../App';
import { BackupPanel } from '../components/BackupPanel';
import { Empty } from '../components/common';
import { useFetch } from '../hooks';
import type { HubSettings } from '../types';

export function SettingsPage() {
  const { auth } = useApp();
  if (auth?.monitor) return <MonitorSettingsPage />;
  return <HubSettingsPage />;
}

interface ProbeResult { address: string; url: string; ok: boolean; hubName: string | null; hubId: string | null; appVersion: string | null; error: string | null }
interface MonitorStatus {
  hubConnection: 'Auto' | 'Manual' | 'Off'; hubAddress: string | null; hubUrl: string | null; hubName: string | null;
  connected: boolean; message: string; discovered: { hubId: string; name: string; url: string }[];
  discovery: { listening: boolean; listenError: string | null; announcementsLastMinute: number; lastAnnouncement: number | null; otherVersions: string[]; hint: string | null };
}

/** A monitor PC's own settings: which Hub to watch (found automatically, searched for, or at a fixed address). */
function MonitorSettingsPage() {
  const { data, reload } = useFetch<MonitorStatus>('/api/monitor', 4000);
  const [mode, setMode] = useState<'Auto' | 'Manual' | null>(null);
  const [address, setAddress] = useState<string | null>(null);
  const [test, setTest] = useState<ProbeResult | null>(null);
  const [found, setFound] = useState<ProbeResult[] | null>(null);
  const [busy, setBusy] = useState('');
  const [msg, setMsg] = useState('');
  const latest = useRef(0);
  if (!data) return <Empty>Loading…</Empty>;

  const shown = mode ?? (data.hubConnection === 'Manual' ? 'Manual' : 'Auto');
  const addr = address ?? data.hubAddress ?? '';
  const save = async (body: object, done: string) => {
    try { await api('/api/monitor', { method: 'PUT', json: body }); setMsg(done); setMode(null); reload(); }
    catch (e) { setMsg((e as Error).message); }
  };
  const connect = async (a: string) => {
    const run = ++latest.current; // only the latest test's answer counts
    setBusy('test'); setTest(null); setMsg('');
    try {
      const r = await api<ProbeResult>(`/api/monitor/probe?address=${encodeURIComponent(a)}`);
      if (run !== latest.current) return;
      setTest(r);
      if (r.ok) await save({ hubConnection: 'Manual', hubAddress: a }, `Connected to ${r.hubName}. The dashboard follows it from now on.`);
    } catch (e) { if (run === latest.current) setMsg((e as Error).message); }
    finally { if (run === latest.current) setBusy(''); }
  };
  const search = async () => {
    setBusy('search'); setFound(null); setMsg('');
    try { setFound(await api<ProbeResult[]>('/api/monitor/search', { method: 'POST' })); }
    catch (e) { setMsg((e as Error).message); }
    finally { setBusy(''); }
  };
  const d = data.discovery;

  return (
    <>
      <div className="page-head"><h2>Settings</h2></div>
      <section className="panel">
        <h3>Monitor PC</h3>
        <p>This PC shows a Hub’s dashboard and alerts, <b>view only</b>: reports and exports can be downloaded, but nothing can be changed from here, even with the admin password.</p>
        <p className={data.connected ? 'ok-text' : 'warn'}><b>{data.message}</b></p>

        <h4>Hub to watch</h4>
        <label className="check" style={{ display: 'block' }}>
          <input type="radio" checked={shown === 'Auto'} onChange={() => { setMode('Auto'); save({ hubConnection: 'Auto' }, 'Looking for Hubs on the network.'); }} /> Find it on the network automatically
        </label>
        <label className="check" style={{ display: 'block' }}>
          <input type="radio" checked={shown === 'Manual'} onChange={() => setMode('Manual')} /> At a fixed address
        </label>

        {shown === 'Manual' && (
          <div className="actions" style={{ justifyContent: 'flex-start', marginTop: 8 }}>
            <input value={addr} placeholder="Hub PC's IP, e.g. 192.168.1.20 (port 5180 unless changed)" style={{ minWidth: 320 }}
              onChange={(e) => { setAddress(e.target.value); setTest(null); }} onKeyDown={(e) => { if (e.key === 'Enter' && addr.trim() && busy === '') connect(addr.trim()); }} />
            <button className="primary" disabled={!addr.trim() || busy !== ''} onClick={() => connect(addr.trim())}>{busy === 'test' ? 'Testing…' : 'Test and connect'}</button>
          </div>
        )}
        {test && !test.ok && <p className="warn">{test.error}</p>}

        {shown === 'Auto' && data.discovered.length > 1 && (
          <>
            <p className="muted small">More than one Hub is on the network. Watch:</p>
            {data.discovered.map((h) => (
              <label key={h.hubId} className="check" style={{ display: 'block' }}>
                <input type="radio" checked={data.hubUrl === h.url} onChange={() => save({ hubConnection: 'Auto', preferredHubId: h.hubId }, `Watching ${h.name}.`)} /> {h.name} <span className="muted small">{h.url}</span>
              </label>
            ))}
          </>
        )}

        <h4>Can’t find the Hub?</h4>
        <p className="muted small">
          {d.listening
            ? `Hub announcements heard in the last minute: ${d.announcementsLastMinute}${d.lastAnnouncement ? ` (last at ${new Date(d.lastAnnouncement * 1000).toLocaleTimeString()})` : ''}.`
            : 'Not listening for Hub announcements.'}
          {d.otherVersions.length > 0 && <> Heard Hubs running another version: {d.otherVersions.join('; ')}.</>}
        </p>
        {d.hint && <p className="warn small">{d.hint}</p>}
        <div className="actions" style={{ justifyContent: 'flex-start' }}>
          <button disabled={busy !== ''} onClick={search}>{busy === 'search' ? 'Searching (up to ~20 s)…' : 'Search this network'}</button>
          <span className="muted small">Tries port 5180 on every address of this PC’s network. It works even when the announcements are blocked.</span>
        </div>
        {found && (found.length === 0
          ? <p className="warn small">No RTA Hub answered on port 5180 on this network. Check the Hub PC is on and its firewall allows RTA (re-run setup on it with “Allow RTA through Windows Firewall” ticked), or enter its address above if it uses another port or subnet.</p>
          : (
            <table className="grid">
              <tbody>
                {found.map((r) => (
                  <tr key={r.url}>
                    <td>{r.ok ? <b>{r.hubName}</b> : <span className="muted">RTA (not a Hub)</span>}</td>
                    <td className="muted small">{r.url}{r.appVersion ? ` · version ${r.appVersion}` : ''}</td>
                    <td style={{ textAlign: 'right' }}>{r.ok
                      ? <button className="primary" onClick={() => { setMode('Manual'); setAddress(r.address); connect(r.address); }}>Watch this Hub</button>
                      : <span className="muted small">{r.error}</span>}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          ))}
        <p className="muted small">
          Other things to check: this PC and the Hub are on the same network; Windows on this PC calls the network <i>Private</i> (Settings → Network &amp; internet → your connection → Network profile type); and the Hub PC allows RTA through its firewall.
        </p>
        {msg && <p className="muted small">{msg}</p>}
      </section>
      <ThisPcSection />
    </>
  );
}

function HubSettingsPage() {
  const { data, reload } = useFetch<HubSettings>('/api/hub/settings');
  const [form, setForm] = useState<HubSettings | null>(null);
  const [msg, setMsg] = useState('');
  useEffect(() => { if (data) setForm(data); }, [data]);
  if (!form) return <Empty>Loading…</Empty>;

  const save = async () => {
    try {
      await api('/api/hub/settings', { method: 'PUT', json: { name: form.name, secondsRetentionDays: form.secondsRetentionDays, autoAcceptNodes: form.autoAcceptNodes } });
      setMsg('Saved.');
      reload();
    } catch (e) { setMsg((e as Error).message); }
  };

  return (
    <>
      <div className="page-head"><h2>Settings</h2></div>
      <section className="panel">
        <h3>Hub</h3>
        <div className="form-grid">
          <label>Hub name <input value={form.name} onChange={(e) => setForm({ ...form, name: e.target.value })} /></label>
          <label>Keep per-second data for (days)
            <input type="number" min={7} max={3650} value={form.secondsRetentionDays} onChange={(e) => setForm({ ...form, secondsRetentionDays: +e.target.value })} />
          </label>
          <label className="check"><input type="checkbox" checked={form.autoAcceptNodes} onChange={(e) => setForm({ ...form, autoAcceptNodes: e.target.checked })} /> Accept new nodes automatically</label>
        </div>
        <p className="muted small">Per-minute summaries (including 1/3-octave bands) are kept forever; per-second data uses roughly 1 GB per mic per year. Auto-accept is convenient but lets any PC on the network add readings.</p>
        <div className="actions"><span className="muted">{msg}</span><button className="primary" onClick={save}>Save</button></div>
      </section>
      <ThisPcSection />
      <PasswordSection />
      <BackupPanel />
      <section className="panel">
        <h3>About</h3>
        <p className="muted small">Hub id {form.hubId}. Each node’s audio, inputs and calibration are set on that node’s own page (<a href="/node/" target="_blank" rel="noopener">this PC’s node page</a>).</p>
      </section>
    </>
  );
}

function ThisPcSection() {
  const { data, reload } = useFetch<{ runsNode: boolean; runsHub: boolean; monitor: boolean; openAtStartup: string; runAs: string }>('/api/app');
  const [msg, setMsg] = useState('');
  if (!data) return null;
  const set = async (openAtStartup: string) => {
    try { await api('/api/pc', { method: 'PUT', json: { openAtStartup } }); setMsg('Saved.'); reload(); }
    catch (e) { setMsg((e as Error).message); }
  };
  return (
    <section className="panel">
      <h3>This PC</h3>
      <div className="form-grid">
        <label>When someone signs in, open
          <select value={data.openAtStartup} onChange={(e) => set(e.target.value)}>
            <option value="Off">Nothing</option>
            {(data.runsHub || data.monitor) && <option value="Dashboard">The dashboard</option>}
            {data.runsNode && <option value="Node">This PC’s node page</option>}
          </select>
        </label>
      </div>
      <p className="muted small">The page opens in the default browser once RTA is running. It needs the RTA status icon to start at sign-in (it does in sign-in mode; for a service, tick “Show the RTA status icon” in setup).{msg && <> <b>{msg}</b></>}</p>
    </section>
  );
}

function PasswordSection() {
  const { auth, refreshAuth, signIn } = useApp();
  const [cur, setCur] = useState('');
  const [pw, setPw] = useState('');
  const [pw2, setPw2] = useState('');
  const [msg, setMsg] = useState('');
  if (!auth) return null;

  const change = async () => {
    if (pw !== pw2) { setMsg('New passwords don’t match.'); return; }
    try {
      await api('/api/auth/change', { method: 'POST', json: { current: cur || null, password: pw } });
      setMsg('Password changed.');
      setCur(''); setPw(''); setPw2('');
      refreshAuth();
    } catch (e) { setMsg((e as Error).message); }
  };

  return (
    <section className="panel">
      <h3>Admin password</h3>
      {!auth.passwordSet ? (
        <>
          <p className="warn">No admin password is set. Until one is, changes are only possible from this Hub PC, and other PCs can’t change anything.</p>
          {auth.local ? <button className="primary" onClick={() => signIn()}>Set a password</button> : <p className="muted">Open this page on the Hub PC to set one.</p>}
        </>
      ) : !auth.canEdit ? (
        <p className="muted">Sign in to change the password. <button onClick={() => signIn()}>Sign in</button></p>
      ) : (
        <>
          <p className="muted small">The same password protects every node page on this PC and the Hub. Viewing stays open to everyone on the network.</p>
          <div className="form-grid">
            <label>Current password <input type="password" value={cur} onChange={(e) => setCur(e.target.value)} autoComplete="current-password" /></label>
            <label>New password <input type="password" value={pw} onChange={(e) => setPw(e.target.value)} autoComplete="new-password" /></label>
            <label>Repeat new password <input type="password" value={pw2} onChange={(e) => setPw2(e.target.value)} autoComplete="new-password" /></label>
          </div>
          <div className="actions"><span className="muted">{msg}</span><button className="primary" onClick={change}>Change password</button></div>
        </>
      )}
    </section>
  );
}
