import { useState } from 'react';
import { api } from '../api';
import { useApp } from '../App';
import { useFetch } from '../hooks';

interface BackupFile { name: string; size: number; created: number }
interface BackupStatus {
  backups: BackupFile[];
  folder: string;
  lastRestore: { ok: boolean; at: string; message: string; safetyBackup: string | null } | null;
  restorePending: boolean;
  notes: string[];
  autoRestart: boolean;
}
interface Preview {
  manifest: { appVersion: string; created: string; machine: string; reason: string; nodeName: string | null; hubName: string | null; includesHistory: boolean };
  file: string | null;
  sections: { id: string; label: string; applies: boolean; note: string | null }[];
  samePc: boolean;
  suggestTakeOver: boolean;
}

const KIND: [string, string][] = [['daily-', 'Daily'], ['before-update-', 'Before update'], ['before-restore-', 'Before restore']];
const kindOf = (name: string) => KIND.find(([p]) => name.startsWith(p))?.[1] ?? 'Backup';
const mb = (b: number) => (b < 1024 * 1024 ? `${Math.max(1, Math.round(b / 1024))} KB` : `${(b / 1024 / 1024).toFixed(1)} MB`);

/** Backup & restore: download settings (optionally with history), restore from a file or an automatic backup. */
export function BackupPanel() {
  const { auth, signIn } = useApp();
  const { data, reload } = useFetch<BackupStatus>('/api/backup');
  const [history, setHistory] = useState(false);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [chosen, setChosen] = useState<string[]>([]);
  const [takeOver, setTakeOver] = useState(false);
  const [msg, setMsg] = useState('');
  const [busy, setBusy] = useState(false);
  const [showAll, setShowAll] = useState(false);

  const admin = async () => auth?.canEdit || (await signIn());
  const download = async (url: string) => { if (await admin()) window.location.href = url; };

  const open = (p: Preview) => {
    setPreview(p);
    setChosen(p.sections.filter((s) => s.applies && s.id !== 'history').map((s) => s.id));
    setTakeOver(p.suggestTakeOver && !p.samePc);
    setMsg('');
  };

  const upload = async (file: File | undefined) => {
    if (!file || !(await admin())) return;
    setBusy(true);
    try { open(await api<Preview>('/api/backup/upload', { method: 'POST', body: file, headers: { 'Content-Type': 'application/octet-stream' } })); }
    catch (e) { setMsg((e as Error).message); }
    finally { setBusy(false); }
  };

  const restore = async () => {
    if (!preview) return;
    if (!confirm('Restore the selected parts? A backup of the current settings is saved first, and RTA restarts.')) return;
    setBusy(true);
    try {
      const r = await api<{ restarting: boolean; message: string }>('/api/backup/restore', { method: 'POST', json: { sections: chosen, takeOverIdentity: takeOver, file: preview.file } });
      setMsg(r.message);
      setPreview(null);
      if (r.restarting) await waitForRestart(setMsg);
      reload();
    } catch (e) { setMsg((e as Error).message); }
    finally { setBusy(false); }
  };

  if (!data) return null;
  const list = showAll ? data.backups : data.backups.slice(0, 6);
  return (
    <section className="panel">
      <h3>Backup &amp; restore</h3>
      {data.notes.map((n) => <p key={n} className="warn small">{n}</p>)}
      {data.lastRestore && <p className={data.lastRestore.ok ? "muted small" : "warn small"}>Last restore ({new Date(data.lastRestore.at).toLocaleString()}): {data.lastRestore.message}</p>}
      {data.restorePending && <p className="warn small">A restore is waiting for RTA to restart.</p>}

      <p className="muted small">
        Settings survive updates on their own; these backups are for moving to a new PC, rebuilding one, or undoing a change.
        A backup holds this PC’s inputs and calibration, the Hub’s settings, alert rules, integrations, floor plan, zones and surveys, mic names, schedule and the admin password.
      </p>
      <div className="actions" style={{ justifyContent: 'flex-start' }}>
        <button className="primary" onClick={() => download(`/api/backup/export?history=${history}`)}>Download backup</button>
        <label className="check"><input type="checkbox" checked={history} onChange={(e) => setHistory(e.target.checked)} /> Include logged history (larger file)</label>
        <label className="filebtn">{busy ? 'Working…' : 'Restore from file…'}
          <input type="file" accept=".rtabackup" hidden disabled={busy} onChange={(e) => { upload(e.target.files?.[0]); e.target.value = ''; }} />
        </label>
      </div>

      {preview && (
        <div className="panel" style={{ background: "var(--panel-2)" }}>
          <h4>Restore from {preview.manifest.machine}, {new Date(preview.manifest.created).toLocaleString()}</h4>
          <p className="muted small">{preview.manifest.reason} · version {preview.manifest.appVersion}{preview.manifest.hubName ? ` · Hub “${preview.manifest.hubName}”` : ''}{preview.manifest.nodeName ? ` · node “${preview.manifest.nodeName}”` : ''}</p>
          {preview.sections.map((s) => (
            <label key={s.id} className="check" style={{ display: 'block' }}>
              <input type="checkbox" disabled={!s.applies} checked={chosen.includes(s.id)}
                onChange={(e) => setChosen(e.target.checked ? [...chosen, s.id] : chosen.filter((c) => c !== s.id))} /> {s.label}
              {s.note && <span className="muted small"> — {s.note}</span>}
            </label>
          ))}
          {!preview.samePc && (
            <label className="check" style={{ display: 'block', marginTop: 8 }}>
              <input type="checkbox" checked={takeOver} onChange={(e) => setTakeOver(e.target.checked)} /> This PC replaces {preview.manifest.machine}
              <div className="muted small">Takes over its identity so its mics, zones, surveys and history line up. Leave unticked to copy settings onto an extra PC.</div>
            </label>
          )}
          <div className="actions">
            <button onClick={() => { setPreview(null); api('/api/backup/cancel', { method: 'POST' }).catch(() => {}); }}>Cancel</button>
            <button className="primary" disabled={busy || chosen.length === 0} onClick={restore}>Restore and restart</button>
          </div>
        </div>
      )}
      {msg && <p className="muted">{msg}</p>}

      <h4>Automatic backups</h4>
      <p className="muted small">Saved daily (kept 2 weeks), before each update runs for the first time, and before every restore. Folder: {data.folder}</p>
      {data.backups.length === 0 ? <p className="muted small">None yet.</p> : (
        <table className="grid">
          <thead><tr><th>When</th><th>Kind</th><th>Size</th><th /></tr></thead>
          <tbody>
            {list.map((b) => (
              <tr key={b.name}>
                <td>{new Date(b.created * 1000).toLocaleString()}</td>
                <td>{kindOf(b.name)}</td>
                <td>{mb(b.size)}</td>
                <td style={{ textAlign: 'right' }}>
                  <button onClick={() => download(`/api/backup/files/${encodeURIComponent(b.name)}`)}>Download</button>{' '}
                  <button onClick={async () => { if (await admin()) { try { open(await api<Preview>(`/api/backup/files/${encodeURIComponent(b.name)}/preview`)); } catch (e) { setMsg((e as Error).message); } } }}>Restore…</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {data.backups.length > 6 && <button className="ghost small" onClick={() => setShowAll(!showAll)}>{showAll ? 'Show fewer' : `Show all ${data.backups.length}`}</button>}
    </section>
  );
}

/** Waits for the app to come back after a restore restart, then reloads the page. */
async function waitForRestart(setMsg: (m: string) => void) {
  await new Promise((r) => setTimeout(r, 3000));
  for (let i = 0; i < 30; i++) {
    try {
      const r = await fetch('/api/backup', { cache: 'no-store' });
      if (r.ok) { location.reload(); return; }
    } catch { /* still restarting */ }
    await new Promise((r) => setTimeout(r, 1500));
  }
  setMsg('RTA hasn’t come back yet. Restart it from the tray icon (or restart the PC) to finish restoring.');
}
