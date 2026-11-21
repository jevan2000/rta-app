import { useState } from 'react';
import { api, fmt, fmtDuration, nowSec } from '../api';
import { useApp } from '../App';
import { Sparkline, RtaChart } from '../components/Charts';
import { Empty, QualityBadge, levelClass, micTitle, thresholdsFor } from '../components/common';
import { useRules } from '../hooks';
import type { MicView } from '../types';

export function LivePage() {
  const { live } = useApp();
  const { data: rules } = useRules();
  const [showHidden, setShowHidden] = useState(false);
  if (!live) return <Empty>Connecting to the Hub…</Empty>;

  const mics = live.mics.filter((m) => showHidden || !m.hidden);
  const byNode = new Map<string, MicView[]>();
  for (const m of mics) byNode.set(m.nodeId, [...(byNode.get(m.nodeId) ?? []), m]);
  const pending = live.nodes.filter((n) => n.acceptance === 'Pending' && n.connected);

  return (
    <>
      <ServiceBar />
      {pending.length > 0 && (
        <div className="notice">
          {pending.length} new node{pending.length > 1 ? 's' : ''} waiting for approval: {pending.map((n) => n.name).join(', ')}. <a href="#/nodes">Review on the Nodes page</a>.
        </div>
      )}
      {mics.length === 0 && <Empty>No mics yet. Start a node (this PC’s node page is at <a href="/node/" target="_blank" rel="noopener">/node/</a>) and accept it on the <a href="#/nodes">Nodes</a> page.</Empty>}
      {[...byNode.entries()].map(([nodeId, list]) => {
        const node = live.nodes.find((n) => n.id === nodeId);
        return (
          <section key={nodeId} className="node-group">
            <h3>
              {node?.name ?? 'Node'}
              <span className={`pill ${node?.connected ? (node.running ? 'ok' : 'wait') : 'err'}`}>
                {node?.connected ? (node.running ? 'live' : 'audio stopped') : 'offline'}
              </span>
              {node?.error && <span className="warn small"> {node.error}</span>}
            </h3>
            <div className="cards">
              {list.map((m) => <MicCard key={m.key} m={m} rules={rules} />)}
            </div>
          </section>
        );
      })}
      {live.mics.some((m) => m.hidden) && (
        <label className="check muted small"><input type="checkbox" checked={showHidden} onChange={(e) => setShowHidden(e.target.checked)} /> Show hidden mics</label>
      )}
    </>
  );
}

function MicCard({ m, rules }: { m: MicView; rules: ReturnType<typeof useRules>['data'] }) {
  const { history, live } = useApp();
  const L = m.live;
  // Slow readings use an LCS/LAS rule if there is one, otherwise the 1-minute rule.
  const tC1 = thresholdsFor(rules, m.key, 'LCeq1m');
  const tA1 = thresholdsFor(rules, m.key, 'LAeq1m');
  const tC = thresholdsFor(rules, m.key, 'LCS') ?? tC1;
  const tA = thresholdsFor(rules, m.key, 'LAS') ?? tA1;
  const alert = live?.alerts.filter((a) => a.micKey === m.key) ?? [];
  const state = alert.some((a) => a.level === 'alarm') ? 'alarm-state' : alert.length ? 'warn-state' : '';
  const pts = (history.get(m.key) ?? []).slice(-600);
  const cal = m.calibrated;

  return (
    <a className={`card ${state} ${m.online ? '' : 'offline'}`} href={`#/mic/${encodeURIComponent(m.key)}`}>
      <div className="card-head">
        <b>{micTitle(m)}</b>
        <QualityBadge quality={m.quality} uncertainty={m.uncertaintyDb} calibrated={cal} />
      </div>
      {!m.online || !L ? <div className="muted offline-note">Offline</div> : (
        <>
          <div className="big">
            <div className="c"><span className={`v ${cal ? levelClass(L.lcs, tC) : ''}`}>{fmt(L.lcs)}</span><span className="u">dBC Slow</span></div>
            <div className="a"><span className={`v ${cal ? levelClass(L.las, tA) : ''}`}>{fmt(L.las)}</span><span className="u">dBA</span></div>
          </div>
          <table className="leq">
            <thead><tr><th /><th>1 min</th><th>15 min</th><th>Service</th></tr></thead>
            <tbody>
              <tr><td>LCeq</td><td className={`cc ${cal ? levelClass(L.lCeq1m, tC1) : ''}`}>{fmt(L.lCeq1m)}</td><td className="cc">{fmt(L.lCeq15m)}</td><td className="cc">{fmt(m.lCeqService)}</td></tr>
              <tr><td>LAeq</td><td className="aa">{fmt(L.lAeq1m)}</td><td className="aa">{fmt(L.lAeq15m)}</td><td className="aa">{fmt(m.lAeqService)}</td></tr>
              <tr><td>C − A</td><td>{L.coverage1m > 5 ? `${L.cMinusA.toFixed(1)} dB` : '—'}</td><td /><td className="muted small">{m.serviceSeconds > 0 ? fmtDuration(m.serviceSeconds) : ''}</td></tr>
            </tbody>
          </table>
          <Sparkline values={pts.map((p) => p.lcs)} values2={pts.map((p) => p.las)} warn={cal ? tC?.warn : null} />
          <RtaChart bandsHz={live?.bandsHz ?? []} levels={L.bands} height={64} labels={false} />
          {(L.clipped || L.warnings.length > 0) && (
            <div className="cw">{L.warnings.filter((w) => !w.startsWith('Uncalibrated')).join(' ')}</div>
          )}
        </>
      )}
    </a>
  );
}

function ServiceBar() {
  const { live, auth } = useApp();
  const viewOnly = !!auth?.monitor;
  const [busy, setBusy] = useState(false);
  const s = live?.service;
  const act = async (fn: () => Promise<unknown>) => {
    setBusy(true);
    try { await fn(); } catch (e) { alert((e as Error).message); } finally { setBusy(false); }
  };
  return (
    <div className="service-bar">
      {s ? (
        <>
          <span className="pill ok">● Service</span>
          <b>{s.name}</b>
          <span className="muted">{fmtDuration(nowSec() - s.startTs)} · {s.source === 'schedule' ? 'scheduled' : 'started manually'}</span>
          <a href={`#/services/${s.id}`}>Details</a>
          {!viewOnly && <button disabled={busy} onClick={() => act(() => api('/api/hub/services/stop', { method: 'POST' }))}>Stop service</button>}
        </>
      ) : (
        <>
          <span className="muted">No service running.{viewOnly ? '' : <> Services start from the <a href="#/services">schedule</a>, or:</>}</span>
          {!viewOnly && <button disabled={busy} onClick={() => act(async () => {
            const name = prompt('Service name:', `Service ${new Date().toLocaleString(undefined, { weekday: 'short', hour: '2-digit', minute: '2-digit' })}`);
            if (name != null) await api('/api/hub/services/start', { method: 'POST', json: { name } });
          })}>Start service now</button>}
        </>
      )}
      {auth && !auth.canEdit && !viewOnly && <span className="muted small">(sign in to start or stop)</span>}
    </div>
  );
}
