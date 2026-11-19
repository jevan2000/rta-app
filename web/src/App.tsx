import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react';
import { api, getAuth, onSignInRequired, useHubLive, useLiveHistory, fmtTime } from './api';
import type { AuthStatus, HubLive, LivePointMap } from './appTypes';
import { LivePage } from './pages/LivePage';
import { MicPage } from './pages/MicPage';
import { HistoryPage } from './pages/HistoryPage';
import { ServicesPage } from './pages/ServicesPage';
import { AlertsPage } from './pages/AlertsPage';
import { NodesPage } from './pages/NodesPage';
import { SettingsPage } from './pages/SettingsPage';
import { RoomPage } from './pages/RoomPage';
import { PlanPage } from './pages/PlanPage';
import { SurveyPage } from './pages/SurveyPage';
import { IntegrationsPage } from './pages/IntegrationsPage';

interface Ctx {
  live: HubLive | null;
  history: LivePointMap;
  auth: AuthStatus | null;
  refreshAuth: () => Promise<void>;
  /** Opens the sign-in dialog; resolves true when signed in. */
  signIn: () => Promise<boolean>;
}
const AppCtx = createContext<Ctx>(null!);
export const useApp = () => useContext(AppCtx);

const ROUTES: [string, string][] = [
  ['live', 'Live'], ['room', 'Room'], ['history', 'History'], ['services', 'Services'], ['alerts', 'Alerts'], ['nodes', 'Nodes'], ['plan', 'Plan'], ['survey', 'Survey'], ['integrations', 'Integrations'], ['settings', 'Settings'],
];

function useHashRoute() {
  const read = () => location.hash.replace(/^#\/?/, '') || 'live';
  const [route, setRoute] = useState(read);
  useEffect(() => {
    const on = () => setRoute(read());
    window.addEventListener('hashchange', on);
    return () => window.removeEventListener('hashchange', on);
  }, []);
  return route;
}

export function App() {
  const { live, connected } = useHubLive();
  const history = useLiveHistory(live);
  const route = useHashRoute();
  const [auth, setAuth] = useState<AuthStatus | null>(null);
  const [login, setLogin] = useState<{ setup: boolean } | null>(null);
  const resolver = useRef<((ok: boolean) => void) | null>(null);

  const refreshAuth = useCallback(async () => setAuth(await getAuth()), []);
  useEffect(() => { refreshAuth(); }, [refreshAuth]);

  const signIn = useCallback(async () => {
    const st = await getAuth();
    setAuth(st);
    if (!st.passwordSet && !st.local) {
      alert('No admin password is set yet. Set one on the Hub PC itself (open this page there), then sign in here.');
      return false;
    }
    setLogin({ setup: !st.passwordSet });
    return new Promise<boolean>((resolve) => { resolver.current = resolve; });
  }, []);
  useEffect(() => onSignInRequired(signIn), [signIn]);

  const closeLogin = async (ok: boolean) => {
    setLogin(null);
    resolver.current?.(ok);
    resolver.current = null;
    if (ok) await refreshAuth();
  };

  const [page, arg] = [route.split('/')[0], decodeURIComponent(route.split('/').slice(1).join('/'))];
  const pending = live?.nodes.filter((n) => n.acceptance === 'Pending' && n.connected).length ?? 0;

  return (
    <AppCtx.Provider value={{ live, history, auth, refreshAuth, signIn }}>
      <header className="top">
        <div className="brand">
          <span className={`dot ${connected ? 'on' : 'err'}`} />
          <div>
            <h1>{live?.hubName ?? 'RTA Hub'}</h1>
            <div className="muted small">
              {!connected ? 'Disconnected — retrying…'
                : live?.service ? <>Service running: <b>{live.service.name}</b> since {fmtTime(live.service.startTs)}</>
                : 'No service running'}
            </div>
          </div>
        </div>
        <nav>
          {ROUTES.map(([r, label]) => (
            <a key={r} href={`#/${r}`} className={page === r || (page === 'mic' && r === 'live') || (page === 'zone' && r === 'room') ? 'active' : ''}>
              {label}{r === 'nodes' && pending > 0 ? <span className="count">{pending}</span> : null}
              {r === 'survey' && live?.survey ? <span className="count">●</span> : null}
              {r === 'alerts' && (live?.alerts.length ?? 0) > 0 ? <span className="count alarm">{live!.alerts.length}</span> : null}
            </a>
          ))}
          <a href="/node/" target="_blank" rel="noopener" className="ext" title="This PC's own node page (audio, inputs, calibration)">Node ↗</a>
          <AuthButton />
        </nav>
      </header>

      <AlertBanner />

      <main className="page">
        {page === 'live' && <LivePage />}
        {page === 'mic' && <MicPage micKey={arg} />}
        {page === 'zone' && <MicPage micKey={`zones:${arg}`} />}
        {page === 'room' && <RoomPage />}
        {page === 'plan' && <PlanPage />}
        {page === 'survey' && <SurveyPage />}
        {page === 'integrations' && <IntegrationsPage />}
        {page === 'history' && <HistoryPage />}
        {page === 'services' && <ServicesPage id={arg || undefined} />}
        {page === 'alerts' && <AlertsPage />}
        {page === 'nodes' && <NodesPage />}
        {page === 'settings' && <SettingsPage />}
      </main>

      {login && <LoginDialog setup={login.setup} onDone={closeLogin} />}
    </AppCtx.Provider>
  );
}

function AuthButton() {
  const { auth, signIn, refreshAuth } = useApp();
  if (!auth) return null;
  if (auth.signedIn)
    return <button className="ghost" onClick={async () => { await api('/api/auth/logout', { method: 'POST' }); await refreshAuth(); }}>Sign out</button>;
  if (auth.canEdit) return <span className="muted small" title="No password set; changes allowed from this PC only">Local admin</span>;
  return <button className="ghost" onClick={() => signIn()}>Sign in</button>;
}

function AlertBanner() {
  const { live } = useApp();
  if (!live || !live.alerts.length) return null;
  const label = (key: string) => live.mics.find((m) => m.key === key)?.label ?? live.zones.find((z) => z.key === key)?.name ?? key;
  const worst = live.alerts.some((a) => a.level === 'alarm') ? 'alarm' : 'warn';
  return (
    <div className={`banner ${worst}`}>
      {live.alerts.map((a) => (
        <span key={`${a.ruleId}-${a.micKey}`}>
          <b>{a.level === 'alarm' ? 'ALARM' : 'Warning'}</b> {label(a.micKey)}: {a.ruleName} {a.value.toFixed(1)} dB (limit {a.threshold})
        </span>
      ))}
    </div>
  );
}

function LoginDialog({ setup, onDone }: { setup: boolean; onDone: (ok: boolean) => void }) {
  const [pw, setPw] = useState('');
  const [pw2, setPw2] = useState('');
  const [msg, setMsg] = useState('');
  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (setup && pw !== pw2) { setMsg('Passwords don’t match.'); return; }
    try {
      await api(`/api/auth/${setup ? 'setup' : 'login'}`, { method: 'POST', json: { password: pw } });
      onDone(true);
    } catch (err) { setMsg((err as Error).message); }
  };
  return (
    <div className="modal-back" onClick={() => onDone(false)}>
      <form className="modal" onClick={(e) => e.stopPropagation()} onSubmit={submit}>
        <h2>{setup ? 'Set an admin password' : 'Admin sign-in'}</h2>
        <p className="muted">{setup
          ? 'This password protects settings, calibration, services, alerts and nodes. Anyone on the network can still view levels.'
          : 'Changes need the admin password.'}</p>
        <label>Password <input type="password" autoFocus value={pw} onChange={(e) => setPw(e.target.value)} autoComplete={setup ? 'new-password' : 'current-password'} /></label>
        {setup && <label>Repeat password <input type="password" value={pw2} onChange={(e) => setPw2(e.target.value)} autoComplete="new-password" /></label>}
        {msg && <p className="warn">{msg}</p>}
        <div className="actions">
          <button type="button" className="ghost" onClick={() => onDone(false)}>Cancel</button>
          <button className="primary">{setup ? 'Set password' : 'Sign in'}</button>
        </div>
      </form>
    </div>
  );
}
