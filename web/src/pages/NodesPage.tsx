import { useState } from 'react';
import { api, fmtTime } from '../api';
import { useApp } from '../App';
import { Empty, QualityBadge } from '../components/common';
import { useFetch } from '../hooks';
import type { MicRow, NodeView } from '../types';

export function NodesPage() {
  const { live } = useApp();
  const { data: micRows, reload: reloadMics } = useFetch<MicRow[]>('/api/hub/mics');
  const [busy, setBusy] = useState(false);
  if (!live) return <Empty>Connecting…</Empty>;

  const act = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    try { await fn(); reloadMics(); } catch (e) { alert((e as Error).message); } finally { setBusy(false); }
  };
  const order = { Pending: 0, Accepted: 1, Rejected: 2 } as const;
  const nodes = [...live.nodes].sort((a, b) => order[a.acceptance] - order[b.acceptance] || a.name.localeCompare(b.name));

  return (
    <>
      <div className="page-head"><h2>Nodes</h2></div>
      <p className="muted">A node is a PC with an audio interface and RTA mics. Nodes on the same network find this Hub automatically. New nodes appear here as <b>Pending</b> and send nothing until you accept them. A node that was offline sends what it missed (up to 7 days) when it reconnects.</p>
      {nodes.length === 0 && <Empty>No nodes have connected yet.</Empty>}
      <div className="node-list">
        {nodes.map((n) => <NodeCard key={n.id} n={n} busy={busy} act={act} micRows={micRows ?? []} />)}
      </div>
      <section className="panel">
        <h3>Troubleshooting</h3>
        <ul className="muted small">
          <li>Nodes find the Hub by a UDP broadcast on port 5181, and connect to it on TCP port 5180. Allow both through Windows Firewall on the Hub PC (Windows usually asks the first time).</li>
          <li>If the node is on a different subnet or VLAN, broadcasts won’t reach it: on the node’s page, open Settings → Hub and enter this Hub’s address.</li>
        </ul>
      </section>
    </>
  );
}

function NodeCard({ n, busy, act, micRows }: { n: NodeView; busy: boolean; act: (fn: () => Promise<unknown>) => void; micRows: MicRow[] }) {
  const { live } = useApp();
  const mics = live?.mics.filter((m) => m.nodeId === n.id) ?? [];
  const rows = micRows.filter((r) => r.nodeId === n.id);
  return (
    <section className={`panel node ${n.acceptance.toLowerCase()}`}>
      <div className="node-head">
        <h3>{n.name}</h3>
        <span className={`pill ${n.connected ? 'ok' : 'err'}`}>{n.connected ? 'online' : 'offline'}</span>
        <span className={`pill ${n.acceptance === 'Accepted' ? 'ok' : n.acceptance === 'Pending' ? 'wait' : 'err'}`}>{n.acceptance}</span>
        <span className="spacer" />
        {n.acceptance !== 'Accepted' && <button className="primary" disabled={busy} onClick={() => act(() => api(`/api/hub/nodes/${n.id}/accept`, { method: 'POST' }))}>Accept</button>}
        {n.acceptance !== 'Rejected' && <button disabled={busy} onClick={() => confirm(`Reject ${n.name}? Its readings will be ignored.`) && act(() => api(`/api/hub/nodes/${n.id}/reject`, { method: 'POST' }))}>Reject</button>}
        <button className="ghost" disabled={busy} onClick={() => { const name = prompt('Node name:', n.name); if (name) act(() => api(`/api/hub/nodes/${n.id}`, { method: 'PUT', json: { name } })); }}>Rename</button>
        {!n.connected && <button className="ghost" disabled={busy} onClick={() => confirm(`Remove ${n.name} from the list? Its logged history is kept.`) && act(() => api(`/api/hub/nodes/${n.id}`, { method: 'DELETE' }))}>Remove</button>}
      </div>
      <p className="muted small">
        {n.address ?? 'unknown address'} · app {n.version ?? '?'} · {n.sampleRate ? `${n.sampleRate / 1000} kHz · ` : ''}{n.micCount} input(s) · last seen {fmtTime(n.lastSeen, true)}
        {n.connected && !n.running && <span className="warn"> · audio stopped</span>}
      </p>
      {n.error && <p className="warn">{n.error}</p>}
      {n.warnings.map((w) => <p key={w} className="warn small">{w}</p>)}
      {n.acceptance === 'Accepted' && rows.length > 0 && (
        <table className="grid">
          <thead><tr><th>Input</th><th>Name on node</th><th>Display name (Hub)</th><th>Calibration</th><th>Hidden</th></tr></thead>
          <tbody>
            {rows.map((r) => {
              const m = mics.find((x) => x.key === r.key);
              return (
                <tr key={r.key}>
                  <td>{r.channelIndex < 100 ? r.channelIndex + 1 : `${Math.floor(r.channelIndex / 100) + 1}·${(r.channelIndex % 100) + 1}`}</td>
                  <td>{r.name}{r.micLabel ? <span className="muted"> · {r.micLabel}</span> : null}</td>
                  <td>
                    <input defaultValue={r.displayName ?? ''} placeholder={r.name}
                      onBlur={(e) => { if ((e.target.value || null) !== r.displayName) act(() => api(`/api/hub/mics/${encodeURIComponent(r.key)}`, { method: 'PUT', json: { displayName: e.target.value || null, hidden: r.hidden } })); }} />
                  </td>
                  <td>{m ? <QualityBadge quality={m.quality} uncertainty={m.uncertaintyDb} calibrated={m.calibrated} /> : '—'}</td>
                  <td><input type="checkbox" checked={r.hidden} onChange={(e) => act(() => api(`/api/hub/mics/${encodeURIComponent(r.key)}`, { method: 'PUT', json: { displayName: r.displayName, hidden: e.target.checked } }))} /></td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
      {n.acceptance === 'Pending' && <p className="muted small">Accept this node to start logging its mics. Everything it buffered while waiting will be sent.</p>}
    </section>
  );
}
