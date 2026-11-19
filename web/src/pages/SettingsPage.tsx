import { useEffect, useState } from 'react';
import { api } from '../api';
import { useApp } from '../App';
import { BackupPanel } from '../components/BackupPanel';
import { Empty } from '../components/common';
import { useFetch } from '../hooks';
import type { HubSettings } from '../types';

export function SettingsPage() {
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
  const { data, reload } = useFetch<{ runsNode: boolean; runsHub: boolean; openAtStartup: string; runAs: string }>('/api/app');
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
            {data.runsHub && <option value="Dashboard">The dashboard</option>}
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
