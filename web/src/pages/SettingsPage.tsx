import { useEffect, useState } from 'react';
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

interface MonitorStatus {
  hubConnection: 'Auto' | 'Manual' | 'Off'; hubAddress: string | null; hubUrl: string | null; hubName: string | null;
  connected: boolean; message: string; discovered: { hubId: string; name: string; url: string }[];
}

/** A monitor PC's own settings: which Hub to watch, and what opens at sign-in. */
function MonitorSettingsPage() {
  const { data, reload } = useFetch<MonitorStatus>('/api/monitor', 5000);
  const [address, setAddress] = useState<string | null>(null);
  const [msg, setMsg] = useState('');
  if (!data) return <Empty>Loading…</Empty>;
  const put = async (body: object) => {
    try { await api('/api/monitor', { method: 'PUT', json: body }); setMsg('Saved. The dashboard reconnects in a few seconds.'); reload(); }
    catch (e) { setMsg((e as Error).message); }
  };
  return (
    <>
      <div className="page-head"><h2>Settings</h2></div>
      <section className="panel">
        <h3>Monitor PC</h3>
        <p>This PC shows a Hub’s dashboard and alerts, <b>view only</b>: nothing can be changed from here, even with the admin password. Make changes on the Hub PC.</p>
        <p className={data.connected ? 'ok-text' : 'warn'}>{data.message}</p>
        <div className="form-grid">
          <label>Hub to watch
            <select value={data.hubConnection} onChange={(e) => put({ hubConnection: e.target.value, hubAddress: data.hubAddress })}>
              <option value="Auto">Find it on the network</option>
              <option value="Manual">At a fixed address</option>
            </select>
          </label>
          {data.hubConnection === 'Manual' && (
            <label>Hub address
              <input value={address ?? data.hubAddress ?? ''} placeholder="e.g. 192.168.1.20:5180" onChange={(e) => setAddress(e.target.value)}
                onBlur={() => address != null && put({ hubConnection: 'Manual', hubAddress: address })} />
            </label>
          )}
        </div>
        {data.hubConnection === 'Auto' && data.discovered.length > 1 && (
          <>
            <h4>Hubs on the network</h4>
            {data.discovered.map((h) => (
              <label key={h.hubId} className="check" style={{ display: 'block' }}>
                <input type="radio" checked={data.hubUrl === h.url} onChange={() => put({ hubConnection: 'Auto', preferredHubId: h.hubId })} /> {h.name} <span className="muted small">{h.url}</span>
              </label>
            ))}
          </>
        )}
        {data.hubConnection === 'Auto' && data.discovered.length === 0 && (
          <p className="muted small">No Hub found yet. Hubs announce themselves on UDP port 5181; if the Hub is on another subnet or VLAN, use a fixed address.</p>
        )}
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
