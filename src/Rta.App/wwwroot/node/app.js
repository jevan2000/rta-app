'use strict';

// ---------- helpers ----------
const $ = (id) => document.getElementById(id);
const FLOOR = -150; // anything below is "no signal"
const fmt = (v, d = 1) => (v == null || v < FLOOR) ? '—' : v.toFixed(d);
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const css = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

async function api(path, opts = {}, retried = false) {
  const res = await fetch('/api' + path, {
    headers: opts.body && typeof opts.body === 'string' && !opts.raw ? { 'Content-Type': 'application/json' } : {},
    ...opts,
  });
  if (res.status === 401 && !retried && !path.startsWith('/auth')) {
    if (await signIn()) return api(path, opts, true);
  }
  if (!res.ok) {
    let msg = res.statusText;
    try { const j = await res.json(); msg = j.detail || j.title || msg; } catch { /* not json */ }
    throw new Error(msg);
  }
  return res.status === 204 ? null : res.json();
}

/** Shows the sign-in (or first-time password) dialog. Resolves true once signed in. */
async function signIn() {
  const st = await (await fetch('/api/auth/status')).json();
  const setup = !st.passwordSet;
  if (setup && !st.local) { alert('Set an admin password on the PC running this node first, then sign in here.'); return false; }
  $('loginTitle').textContent = setup ? 'Set an admin password' : 'Admin sign-in';
  $('loginHelp').textContent = setup
    ? 'Choose a password for changing settings, calibration and the Hub. Viewing stays open to everyone on the network.'
    : 'Changes need the admin password.';
  $('loginPw2Wrap').hidden = !setup;
  $('loginGo').textContent = setup ? 'Set password' : 'Sign in';
  $('loginPw').value = ''; $('loginPw2').value = ''; $('loginMsg').textContent = '';
  const dlg = $('login');
  dlg.showModal();
  return new Promise((resolve) => {
    let settled = false;
    const finish = (ok) => { if (settled) return; settled = true; $('loginGo').onclick = null; if (dlg.open) dlg.close(); resolve(ok); };
    dlg.onclose = () => finish(false);
    $('loginGo').onclick = async () => {
      if (setup && $('loginPw').value !== $('loginPw2').value) { $('loginMsg').innerHTML = '<span class="warn">Passwords don’t match.</span>'; return; }
      const r = await fetch(`/api/auth/${setup ? 'setup' : 'login'}`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password: $('loginPw').value }) });
      if (r.ok) { finish(true); return; }
      let msg = r.statusText; try { const j = await r.json(); msg = j.detail || j.title || msg; } catch { /* */ }
      $('loginMsg').innerHTML = `<span class="warn">${esc(msg)}</span>`;
    };
  });
}

function levelClass(v, warn, alarm) {
  if (v == null || v < FLOOR) return '';
  if (v >= alarm) return 'alarm-level';
  if (v >= warn) return 'warn-level';
  return '';
}

// ---------- state ----------
let status = null;
let channels = [];
const history = new Map();  // channel -> [{t, c, a, ca}] at 2 Hz, 15 min
const rtaPeak = new Map();  // channel -> peak-hold band levels
const rtaSmooth = new Map();
let tick = 0;
let detailChannel = null;
const HISTORY_SECONDS = 15 * 60;

// ---------- live socket ----------
function connect() {
  const ws = new WebSocket(`${location.protocol === 'https:' ? 'wss' : 'ws'}://${location.host}/ws/live`);
  ws.onmessage = (e) => {
    const msg = JSON.parse(e.data);
    status = msg.status;
    channels = msg.channels;
    if (msg.hubLink) renderHubLink(msg.hubLink);
    tick++;
    record();
    render();
  };
  ws.onclose = () => {
    $('statusDot').className = 'dot err';
    $('deviceLine').textContent = 'Disconnected from node — retrying…';
    setTimeout(connect, 1500);
  };
}

function record() {
  const now = Date.now() / 1000;
  for (const ch of channels) {
    const L = ch.levels;
    if (!L) continue;
    // RTA: fast-ish smoothing + decaying peak hold
    const prev = rtaSmooth.get(ch.channelIndex);
    const sm = L.bands.map((v, i) => prev ? Math.max(v, prev[i] - 2.5) * 0.35 + prev[i] * 0.65 : v);
    rtaSmooth.set(ch.channelIndex, sm);
    const pk = rtaPeak.get(ch.channelIndex);
    rtaPeak.set(ch.channelIndex, L.bands.map((v, i) => pk ? Math.max(v, pk[i] - 0.15) : v));
    if (tick % 5 === 0) {
      const h = history.get(ch.channelIndex) ?? [];
      h.push({ t: now, c: L.lcs, a: L.las, ca: L.coverage1mSeconds > 5 ? L.cMinusA : null });
      while (h.length && h[0].t < now - HISTORY_SECONDS) h.shift();
      history.set(ch.channelIndex, h);
    }
  }
}

// ---------- hub link ----------
let hubLink = null;
const HUB_STATE = {
  Connected: ['ok', 'Hub: connected'], Pending: ['wait', 'Hub: waiting for approval'], Searching: ['wait', 'Hub: searching…'],
  Connecting: ['wait', 'Hub: connecting…'], Rejected: ['err', 'Hub: rejected'], Error: ['err', 'Hub: error'], Off: ['', 'Hub: off'],
};
function renderHubLink(h) {
  hubLink = h;
  const [cls, text] = HUB_STATE[h.state] ?? ['', 'Hub: ' + h.state];
  const el = $('hubLink');
  el.className = 'hub-pill ' + cls;
  el.textContent = text + (h.unsentRecords > 30 ? ` · ${h.unsentRecords} unsent` : '');
  el.title = [h.hubName, h.message].filter(Boolean).join(' — ');
}

// ---------- rendering ----------
function render() {
  const s = status;
  $('nodeName').textContent = s.nodeName;
  $('statusDot').className = 'dot ' + (s.running ? 'on' : s.error ? 'err' : '');
  $('deviceLine').textContent = s.running
    ? `${s.deviceName} · ${(s.sampleRate / 1000).toFixed(1)} kHz · ${channels.length} input(s) metered`
    : (s.error ?? 'Audio stopped');
  $('btnStart').hidden = s.running;
  $('btnStop').hidden = !s.running;

  const warns = [...(s.error ? [{ t: s.error, err: true }] : []), ...s.warnings.map((t) => ({ t }))];
  $('warnings').innerHTML = warns.map((w) => `<div class="${w.err ? 'err' : ''}">${esc(w.t)}</div>`).join('');

  $('empty').hidden = channels.length > 0;
  const main = $('channels');
  const ids = channels.map((c) => c.channelIndex).join(',');
  if (main.dataset.ids !== ids) {
    main.dataset.ids = ids;
    main.innerHTML = channels.map((c) => cardHtml(c.channelIndex)).join('');
    for (const c of channels) $(`card${c.channelIndex}`).onclick = () => openDetail(c.channelIndex);
  }
  for (const c of channels) updateCard(c);
  if (detailChannel != null && $('detail').open) updateDetail();
}

function cardHtml(i) {
  return `<article class="card" id="card${i}">
    <div class="card-head"><b id="n${i}"></b><span id="b${i}" class="badge"></span></div>
    <div class="big">
      <div class="c"><span class="v" id="lcs${i}">—</span><span class="u">dBC Slow</span></div>
      <div class="a"><span class="v" id="las${i}">—</span><span class="u">dBA</span></div>
    </div>
    <table class="leq">
      <thead><tr><th></th><th>1 min</th><th>15 min</th><th>Session</th></tr></thead>
      <tbody>
        <tr><td>LCeq</td><td class="cc" id="c1${i}"></td><td class="cc" id="c15${i}"></td><td class="cc" id="cs${i}"></td></tr>
        <tr><td>LAeq</td><td class="aa" id="a1${i}"></td><td class="aa" id="a15${i}"></td><td class="aa" id="as${i}"></td></tr>
        <tr><td>LCpeak max 1m</td><td id="pk${i}"></td><td></td><td id="pks${i}"></td></tr>
        <tr><td>C − A (1 min)</td><td id="ca${i}"></td><td></td><td></td></tr>
      </tbody>
    </table>
    <canvas id="spark${i}" height="60"></canvas>
    <canvas id="rta${i}" height="90"></canvas>
    <div class="cw" id="w${i}"></div>
  </article>`;
}

function updateCard(ch) {
  const i = ch.channelIndex, L = ch.levels, th = status.thresholds;
  $(`n${i}`).textContent = `${ch.name}${ch.micLabel ? ' · ' + ch.micLabel : ''}`;
  const badge = $(`b${i}`);
  const rough = ['Low', 'VeryLow'].includes(ch.profileQuality);
  badge.textContent = !L?.calibrated ? 'UNCAL · dBFS'
    : rough ? `≈ ${QUALITY_LABEL[ch.profileQuality]}${ch.profileUncertaintyDb != null ? ' ±' + ch.profileUncertaintyDb.toFixed(0) + ' dB' : ''}`
    : (ch.profileName ?? 'Calibrated');
  badge.title = L?.calibrated ? `${ch.profileName ?? ''} · ${QUALITY_LABEL[ch.profileQuality ?? 'Unknown']}` : 'No calibration profile assigned';
  badge.className = 'badge ' + (!L?.calibrated ? 'uncal' : rough ? 'rough' : 'cal');
  $(`w${i}`).textContent = ch.warnings.filter((w) => !w.startsWith('Uncalibrated')).join(' ');
  if (!L) return;

  const set = (id, v, cls = '') => { const el = $(id); el.textContent = fmt(v); if (cls !== null) el.className = el.className.replace(/\b(warn|alarm)-level\b/g, '').trim() + (cls ? ' ' + cls : ''); };
  set(`lcs${i}`, L.lcs, levelClass(L.lcs, th.warnC, th.alarmC));
  set(`las${i}`, L.las, levelClass(L.las, th.warnA, th.alarmA));
  set(`c1${i}`, L.lCeq1m); set(`c15${i}`, L.lCeq15m); set(`cs${i}`, L.lCeqSession);
  set(`a1${i}`, L.lAeq1m); set(`a15${i}`, L.lAeq15m); set(`as${i}`, L.lAeqSession);
  set(`pk${i}`, L.lCpeakMax1m); set(`pks${i}`, L.lCpeakMaxSession);
  $(`ca${i}`).textContent = L.coverage1mSeconds > 5 ? L.cMinusA.toFixed(1) + ' dB' : '—';

  const card = $(`card${i}`);
  const state = (L.lCeq1m >= th.alarmC || L.lcs >= th.alarmC) ? 'alarm-state'
    : (L.lCeq1m >= th.warnC || L.lcs >= th.warnC) ? 'warn-state' : '';
  card.classList.remove('warn-state', 'alarm-state');
  if (state) card.classList.add(state);

  drawTime($(`spark${i}`), history.get(i) ?? [], 5 * 60, L.calibrated ? th.warnC : null, false);
  drawRta($(`rta${i}`), status.bandsHz, rtaSmooth.get(i), rtaPeak.get(i), false);
}

// ---------- charts ----------
function fitCanvas(cv) {
  const dpr = window.devicePixelRatio || 1;
  // Fixed CSS height from the markup's height attribute; width follows the layout.
  if (!cv.dataset.h) { cv.dataset.h = cv.getAttribute('height'); cv.style.height = cv.dataset.h + 'px'; }
  const w = cv.clientWidth, h = +cv.dataset.h;
  if (cv.width !== Math.round(w * dpr) || cv.height !== Math.round(h * dpr)) { cv.width = Math.round(w * dpr); cv.height = Math.round(h * dpr); }
  const g = cv.getContext('2d');
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, w, h);
  return { g, w, h };
}

function range(values, pad = 5, minSpan = 30) {
  const v = values.filter((x) => x != null && x > FLOOR);
  if (!v.length) return [0, minSpan];
  let lo = Math.min(...v) - pad, hi = Math.max(...v) + pad;
  if (hi - lo < minSpan) { const m = (hi + lo) / 2; lo = m - minSpan / 2; hi = m + minSpan / 2; }
  return [Math.floor(lo / 5) * 5, Math.ceil(hi / 5) * 5];
}

function gridLines(g, w, h, lo, hi, step, labels, left) {
  g.strokeStyle = css('--grid'); g.fillStyle = css('--muted'); g.font = '10px Segoe UI'; g.lineWidth = 1;
  for (let v = Math.ceil(lo / step) * step; v <= hi; v += step) {
    const y = h - ((v - lo) / (hi - lo)) * h;
    g.beginPath(); g.moveTo(left, y); g.lineTo(w, y); g.stroke();
    if (labels) g.fillText(v.toFixed(0), 2, y + 3);
  }
}

function drawTime(cv, h, seconds, warn, big, key = null) {
  const { g, w, h: H } = fitCanvas(cv);
  const now = Date.now() / 1000;
  const pts = h.filter((p) => p.t >= now - seconds);
  const left = big ? 30 : 0;
  const series = key ? [[key, '--accent']] : [['c', '--c'], ['a', '--a']];
  const [lo, hi] = key ? range(pts.map((p) => p[key]), 2, 10) : range(pts.flatMap((p) => [p.c, p.a]).concat(big && warn != null ? [warn] : []));
  gridLines(g, w, H, lo, hi, key ? 2 : 10, big, left);
  const x = (t) => left + ((t - (now - seconds)) / seconds) * (w - left);
  const y = (v) => H - ((v - lo) / (hi - lo)) * H;
  if (!key && warn != null && warn >= lo && warn <= hi) {
    g.strokeStyle = css('--warn'); g.setLineDash([4, 4]);
    g.beginPath(); g.moveTo(left, y(warn)); g.lineTo(w, y(warn)); g.stroke(); g.setLineDash([]);
  }
  for (const [k, color] of series) {
    g.strokeStyle = css(color); g.lineWidth = big ? 2 : 1.5; g.beginPath();
    let started = false;
    for (const p of pts) {
      const v = p[k];
      if (v == null || v < FLOOR) { started = false; continue; }
      if (!started) { g.moveTo(x(p.t), y(v)); started = true; } else g.lineTo(x(p.t), y(v));
    }
    g.stroke();
  }
}

function drawRta(cv, bandsHz, levels, peaks, big) {
  const { g, w, h } = fitCanvas(cv);
  if (!levels) return;
  const left = big ? 30 : 0, bottom = big ? 14 : 0;
  const [lo, hi] = range(peaks ?? levels, 3, 40);
  gridLines(g, w, h - bottom, lo, hi, 10, big, left);
  const n = levels.length, bw = (w - left) / n;
  const y = (v) => (h - bottom) - ((Math.max(v, lo) - lo) / (hi - lo)) * (h - bottom);
  for (let i = 0; i < n; i++) {
    g.fillStyle = css('--c');
    g.globalAlpha = 0.85;
    g.fillRect(left + i * bw + 1, y(levels[i]), bw - 2, (h - bottom) - y(levels[i]));
    g.globalAlpha = 1;
    if (peaks) { g.fillStyle = css('--text'); g.fillRect(left + i * bw + 1, y(peaks[i]) - 1, bw - 2, 2); }
  }
  if (big) {
    g.fillStyle = css('--muted'); g.font = '10px Segoe UI'; g.textAlign = 'center';
    bandsHz.forEach((f, i) => {
      if ([31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000].includes(f))
        g.fillText(f >= 1000 ? (f / 1000) + 'k' : f, left + (i + 0.5) * bw, h - 2);
    });
    g.textAlign = 'left';
  }
}

// ---------- detail view ----------
function openDetail(i) {
  detailChannel = i;
  $('detail').showModal();
  updateDetail();
}

function updateDetail() {
  const ch = channels.find((c) => c.channelIndex === detailChannel);
  if (!ch || !ch.levels) return;
  const L = ch.levels, th = status.thresholds;
  $('detailTitle').textContent = `${ch.name}${ch.micLabel ? ' · ' + ch.micLabel : ''}${!L.calibrated ? ' (uncalibrated, dBFS)' : ['Low', 'VeryLow'].includes(ch.profileQuality) ? ` (≈ ${QUALITY_LABEL[ch.profileQuality].toLowerCase()} calibration)` : ''}`;
  const tile = (label, v, cls = '') => `<div class="tile"><div class="l">${label}</div><div class="n ${cls}">${fmt(v)}</div></div>`;
  $('dTiles').innerHTML =
    tile('LCeq 1 min', L.lCeq1m, levelClass(L.lCeq1m, th.warnC, th.alarmC)) + tile('LCeq 15 min', L.lCeq15m) + tile('LCeq session', L.lCeqSession) +
    tile('LAeq 1 min', L.lAeq1m, levelClass(L.lAeq1m, th.warnA, th.alarmA)) + tile('LAeq 15 min', L.lAeq15m) + tile('LAeq session', L.lAeqSession) +
    tile('LC Fast', L.lcf) + tile('LCpeak max 1m', L.lCpeakMax1m) + tile('C − A 1 min', L.coverage1mSeconds > 5 ? L.cMinusA : null);
  const h = history.get(detailChannel) ?? [];
  drawTime($('dTime'), h, HISTORY_SECONDS, L.calibrated ? th.warnC : null, true);
  drawRta($('dRta'), status.bandsHz, rtaSmooth.get(detailChannel), rtaPeak.get(detailChannel), true);
  drawTime($('dCa'), h, HISTORY_SECONDS, 0, true, 'ca');
}

// ---------- settings ----------
let devices = [];
let profiles = [];
let draftConfig = null;

async function openSettings() {
  $('settings').showModal();
  $('sMsg').textContent = 'Loading devices…';
  [draftConfig, profiles] = await Promise.all([api('/config'), api('/calibration/profiles')]);
  devices = await api('/devices');
  $('sMsg').textContent = '';
  $('sName').value = draftConfig.nodeName;
  $('sAuto').checked = draftConfig.autoStart;
  $('sHubMode').value = draftConfig.hubConnection ?? 'Auto';
  $('sNdiFps').value = draftConfig.ndiMeterFps ?? 10;
  loadNdiStatus();
  $('sHubAddr').value = draftConfig.hubAddress ?? '';
  onHubModeChange();
  for (const k of ['warnC', 'alarmC', 'warnA', 'alarmA']) $('t' + k[0].toUpperCase() + k.slice(1)).value = draftConfig.thresholds[k];
  $('sDevice').innerHTML = devices.map((d, i) =>
    `<option value="${i}">${esc(d.backend === 'TestSignal' ? d.name : `${d.backend} · ${d.name}`)}${d.inputChannels ? ` (${d.inputChannels} in)` : ''}</option>`).join('');
  const cur = devices.findIndex((d) => d.backend === draftConfig.backend && d.id === draftConfig.deviceId);
  $('sDevice').value = String(Math.max(cur, 0));
  onDeviceChange(cur < 0);
}

function onDeviceChange(changed = true) {
  const d = devices[+$('sDevice').value];
  if (!d) return;
  const rates = d.sampleRates.length ? d.sampleRates : [48000];
  $('sRate').innerHTML = rates.map((r) => `<option value="${r}">${(r / 1000).toFixed(1)} kHz</option>`).join('');
  $('sRate').value = String(rates.includes(draftConfig.sampleRate) ? draftConfig.sampleRate : rates.includes(48000) ? 48000 : rates[0]);
  $('sDeviceNote').textContent = d.note ?? '';
  onRateChange();
  const sameDevice = d.backend === draftConfig.backend && d.id === draftConfig.deviceId;
  const inputs = sameDevice && draftConfig.inputs.length ? draftConfig.inputs
    : Array.from({ length: d.inputChannels }, (_, i) => ({ channelIndex: i, enabled: i < 8, name: `Mic ${i + 1}`, micLabel: '', profileId: null }));
  renderInputs(inputs, d);
  if (changed && !sameDevice) $('sMsg').textContent = 'New device: inputs reset.';
}

async function loadNdiStatus() {
  try {
    const st = await (await fetch('/api/ndi/status')).json();
    const lines = [];
    if (!st.runtimeAvailable) lines.push(`<span class="warn">${esc(st.runtimeMessage)}</span>`);
    else lines.push(`NDI runtime: ${esc(st.runtimeMessage)}. ${st.senders.length ? `${st.senders.length} source(s), about ${st.estimatedMbps} Mb/s: ` + st.senders.map((x) => `${esc(x.name)} (${x.connections} watching)`).join(', ') : 'No sources published.'}`);
    for (const w of st.warnings) lines.push(`<span class="warn">${esc(w)}</span>`);
    $('sNdiStatus').innerHTML = lines.join('<br>');
  } catch { $('sNdiStatus').textContent = ''; }
}

function onHubModeChange() {
  const m = $('sHubMode').value;
  $('sHubAddrWrap').hidden = m !== 'Manual';
  const found = hubLink?.discovered ?? [];
  $('sHubInfo').textContent = m === 'Off' ? 'Readings stay on this PC only.'
    : m === 'Auto' ? (found.length ? `Found on the network: ${found.map((h) => h.name).join(', ')}.` : 'No Hub found on the network yet. If the Hub is on another subnet, use a fixed address.')
    : 'Use the Hub PC’s IP address and port (default 5180).';
}

function onRateChange() { $('sRateWarn').hidden = +$('sRate').value <= 48000; }

function renderInputs(inputs, device) {
  $('sInputs').innerHTML = inputs.map((ic) => {
    const opts = ['<option value="">— none (uncalibrated) —</option>'].concat(profiles.map((p) => {
      const star = (p.micName && ic.micLabel && p.micName.trim().toLowerCase() === ic.micLabel.trim().toLowerCase()) || p.channelIndex === ic.channelIndex ? '★ ' : '';
      return `<option value="${p.id}" ${p.id === ic.profileId ? 'selected' : ''}>${star}${esc(p.name)} (${p.offsetDb.toFixed(1)} dB)</option>`;
    })).join('');
    return `<tr data-ch="${ic.channelIndex}">
      <td><input type="checkbox" class="iOn" ${ic.enabled ? 'checked' : ''}></td>
      <td>${ic.channelIndex + 1}<div class="muted" style="font-size:11px">${esc(device?.channelNames?.[ic.channelIndex] ?? '')}</div></td>
      <td><input class="iName" value="${esc(ic.name)}"></td>
      <td><input class="iMic" value="${esc(ic.micLabel ?? '')}" placeholder="e.g. dbx RTA-M #1"></td>
      <td><select class="iProf">${opts}</select></td>
      <td><input type="checkbox" class="iNdiA" ${ic.ndiAudio ? 'checked' : ''}></td>
      <td><input type="checkbox" class="iNdiM" ${ic.ndiMeter ? 'checked' : ''}></td></tr>`;
  }).join('');
}

function readInputs() {
  return [...$('sInputs').querySelectorAll('tr')].map((tr) => ({
    channelIndex: +tr.dataset.ch,
    enabled: tr.querySelector('.iOn').checked,
    name: tr.querySelector('.iName').value.trim() || `Mic ${+tr.dataset.ch + 1}`,
    micLabel: tr.querySelector('.iMic').value.trim() || null,
    profileId: tr.querySelector('.iProf').value || null,
    ndiAudio: tr.querySelector('.iNdiA').checked,
    ndiMeter: tr.querySelector('.iNdiM').checked,
  }));
}

async function saveSettings() {
  const d = devices[+$('sDevice').value];
  const cfg = {
    ...draftConfig,
    nodeName: $('sName').value.trim() || draftConfig.nodeName,
    backend: d.backend, deviceId: d.id, sampleRate: +$('sRate').value,
    autoStart: $('sAuto').checked,
    hubConnection: $('sHubMode').value,
    ndiMeterFps: Math.min(30, Math.max(1, +$('sNdiFps').value || 10)),
    hubAddress: $('sHubAddr').value.trim() || null,
    inputs: readInputs(),
    thresholds: { warnC: +$('tWarnC').value, alarmC: +$('tAlarmC').value, warnA: +$('tWarnA').value, alarmA: +$('tAlarmA').value },
  };
  $('sMsg').textContent = 'Saving…';
  try {
    draftConfig = await api('/config', { method: 'PUT', body: JSON.stringify(cfg) });
    $('sMsg').innerHTML = '<span class="ok-text">Saved.</span>';
  } catch (e) { $('sMsg').innerHTML = `<span class="warn">${esc(e.message)}</span>`; }
}

// ---------- calibration ----------
let lastResult = null;
let curvePoints = null;

const METHOD_HELP = {
  Calibrator: 'Fit the calibrator snugly on the mic capsule, switch it on, wait a moment for it to settle, then press Measure. Keep the room quiet. Most accurate option.',
  MeterMatch: 'Put the meter right next to the mic, capsules level and pointing the same way. Play steady pink noise through the PA (~80–90 dB). Set the meter to the same weighting and to Slow or Leq, then type its reading and press Measure.',
  Transfer: 'Put this mic right next to an input that is already calibrated (capsules within a few cm, pointing the same way). Play steady pink noise (~75–90 dB) and press Measure. This mic will read the same as the reference, so it is only as accurate as that mic.',
  PhoneApp: 'UNRELIABLE — use only until you have a meter or calibrator. Hold the phone’s bottom mic right next to the RTA mic, play steady pink noise (~80–90 dB), use the app’s Leq with the chosen weighting, type its reading and press Measure. Profiles made this way are flagged on every screen.',
  SpecEstimate: 'VERY ROUGH (±6 dB or worse) — no measurement. Enter the mic’s sensitivity from its datasheet, the interface’s maximum input level at minimum gain, and how many dB of gain you have added (shown in the interface’s software on digitally controlled preamps). Analog gain knobs make this much less accurate.',
};

const QUALITY_LABEL = { High: 'Reference grade', Medium: 'Good', Low: 'Unreliable', VeryLow: 'Very rough', Unknown: 'Unknown' };
const METHOD_LABEL = { Calibrator: 'Calibrator', MeterMatch: 'SPL meter', Transfer: 'Copied', PhoneApp: 'Phone app', SpecEstimate: 'Spec estimate', Manual: 'Manual' };
const qualityBadge = (q, u) => `<span class="q q-${q ?? 'Unknown'}">${QUALITY_LABEL[q ?? 'Unknown']}${u != null ? ` ±${(+u).toFixed(1)} dB` : ''}</span>`;

async function openCalibration() {
  $('calibration').showModal();
  $('cResult').hidden = true;
  $('cMsg').textContent = '';
  const opt = (c) => `<option value="${c.channelIndex}">${c.channelIndex + 1} · ${esc(c.name)}${c.micLabel ? ' · ' + esc(c.micLabel) : ''}</option>`;
  $('cInput').innerHTML = channels.map(opt).join('');
  if (detailChannel != null) $('cInput').value = String(detailChannel);
  refreshReferenceInputs();
  onMethodChange();
  await loadProfiles();
}

function refreshReferenceInputs() {
  const target = +$('cInput').value;
  const refs = channels.filter((c) => c.profileId && c.channelIndex !== target);
  $('cRefInput').innerHTML = refs.length
    ? refs.map((c) => `<option value="${c.channelIndex}">${c.channelIndex + 1} · ${esc(c.name)} — ${esc(c.profileName ?? '')} (${QUALITY_LABEL[c.profileQuality ?? 'Unknown']})</option>`).join('')
    : '<option value="">No other calibrated inputs yet</option>';
}

function onMethodChange() {
  const m = $('cMethod').value;
  document.querySelectorAll('#calibration [data-for]').forEach((el) => { el.hidden = !el.dataset.for.split(' ').includes(m); });
  $('cHelp').textContent = METHOD_HELP[m];
  $('cHelp').className = ['PhoneApp', 'SpecEstimate'].includes(m) ? 'warn' : 'muted';
  $('cMeasure').textContent = m === 'SpecEstimate' ? 'Estimate' : 'Measure';
  $('cMsg').textContent = m === 'Transfer' && !$('cRefInput').value ? 'Calibrate one mic first, then copy it to the others.' : '';
}

function describeReference(req) {
  switch (req.method) {
    case 'Calibrator': return `Calibrator ${req.referenceDb} dB @ ${req.toneHz >= 1000 ? req.toneHz / 1000 + ' kHz' : req.toneHz + ' Hz'} (${req.calibratorClass === 'Unknown' ? 'class unknown' : req.calibratorClass.replace('Class', 'Class ')})`;
    case 'MeterMatch': return `${{ Class1: 'Class 1', Class2: 'Class 2', Consumer: 'Consumer/unrated' }[req.meterClass]} meter${$('cMeterModel').value.trim() ? ' · ' + $('cMeterModel').value.trim() : ''}, ${req.referenceDb} dB${req.weighting}`;
    case 'PhoneApp': return `${req.phone === 'IPhone' ? 'iPhone' : 'Android'}${$('cPhoneApp').value.trim() ? ' · ' + $('cPhoneApp').value.trim() : ''}, ${req.referenceDb} dB${req.weighting}`;
    case 'Transfer': return `Side by side with input ${req.referenceChannelIndex + 1}, ${req.weighting}-weighted`;
    case 'SpecEstimate': return `Specs: ${req.micSensitivityMvPerPa} mV/Pa, ${req.maxInputDbuAtMinGain} dBu max in, +${req.gainAboveMinDb} dB gain`;
    default: return null;
  }
}

async function measure() {
  const m = $('cMethod').value;
  const channelIndex = +$('cInput').value;
  let req, path;
  if (m === 'SpecEstimate') {
    req = { method: m, channelIndex, micSensitivityMvPerPa: +$('cSens').value, maxInputDbuAtMinGain: +$('cMaxIn').value, gainAboveMinDb: +$('cGain').value };
    if (!(req.micSensitivityMvPerPa > 0) || $('cMaxIn').value === '' || $('cGain').value === '') {
      $('cMsg').innerHTML = '<span class="warn">Fill in all three spec values.</span>';
      return;
    }
    path = '/calibration/estimate';
  } else {
    req = {
      channelIndex, method: m, seconds: +$('cSecs').value,
      referenceDb: m === 'Calibrator' ? +$('cCalLevel').value : +$('cRef').value,
      weighting: m === 'Calibrator' ? 'Z' : $('cWeight').value,
      toneHz: +$('cTone').value, calibratorClass: $('cCalClass').value,
      meterClass: $('cMeterClass').value, phone: $('cPhone').value,
      referenceChannelIndex: m === 'Transfer' ? ($('cRefInput').value === '' ? null : +$('cRefInput').value) : null,
    };
    if (m === 'Transfer' && req.referenceChannelIndex == null) {
      $('cMsg').innerHTML = '<span class="warn">No calibrated reference input to copy from.</span>';
      return;
    }
    path = '/calibration/measure';
  }
  $('cMeasure').disabled = true;
  $('cMsg').textContent = m === 'SpecEstimate' ? 'Calculating…' : `Measuring for ${req.seconds} s…`;
  try {
    lastResult = { ...(await api(path, { method: 'POST', body: JSON.stringify(req) })), request: req };
    $('cMsg').textContent = '';
    showResult();
  } catch (e) { $('cMsg').innerHTML = `<span class="warn">${esc(e.message)}</span>`; }
  finally { $('cMeasure').disabled = false; }
}

function showResult() {
  const r = lastResult, req = r.request;
  $('cResult').hidden = false;
  $('rMeasured').textContent = r.measuredDb == null ? '—' : `${r.measuredDb.toFixed(2)} dBFS`;
  $('rOffset').textContent = `${r.offsetDb.toFixed(2)} dB`;
  $('rQuality').innerHTML = qualityBadge(r.quality, r.uncertaintyDb);
  $('rStabLabel').textContent = req.method === 'Transfer' ? 'Mic-to-mic spread' : 'Stability';
  $('rStab').textContent = req.method === 'SpecEstimate' ? '—' : `±${r.stabilityDb.toFixed(2)} dB`;
  $('rBand').textContent = r.dominantBandHz ? `${r.dominantBandHz} Hz` : '—';
  $('rWarn').innerHTML = r.warnings.length ? r.warnings.map((w) => `<li>${esc(w)}</li>`).join('') : '<li class="ok-text">Capture looks good.</li>';
  $('rNotes').innerHTML = (r.notes ?? []).map((n) => `<li>${esc(n)}</li>`).join('');
  $('rNotes').className = 'note-list' + (['Low', 'VeryLow'].includes(r.quality) ? ' rough' : '');
  const ch = channels.find((c) => c.channelIndex === req.channelIndex);
  $('pMic').value = ch?.micLabel ?? '';
  $('pName').value = `${ch?.micLabel || ch?.name || 'Input ' + (req.channelIndex + 1)} · ${METHOD_LABEL[req.method]} · ${new Date().toLocaleDateString()}`;
  curvePoints = null;
  $('pCurve').value = '';
  $('pMsg').textContent = '';
}

async function saveProfile() {
  const r = lastResult, req = r.request;
  const ch = channels.find((c) => c.channelIndex === req.channelIndex);
  const source = req.method === 'Transfer' ? channels.find((c) => c.channelIndex === req.referenceChannelIndex) : null;
  const profile = {
    name: $('pName').value.trim() || 'Calibration',
    offsetDb: r.offsetDb,
    method: req.method,
    referenceDb: req.referenceDb ?? null,
    referenceWeighting: req.weighting ?? null,
    quality: r.quality,
    uncertaintyDb: r.uncertaintyDb,
    referenceDescription: describeReference(req),
    sourceProfileId: source?.profileId ?? null,
    sourceProfileName: source?.profileName ?? null,
    micName: $('pMic').value.trim() || null,
    micSerial: $('pSerial').value.trim() || null,
    deviceName: status.deviceName || null,
    channelIndex: $('pBindCh').checked ? req.channelIndex : null,
    gainNote: $('pGain').value.trim() || null,
    sampleRate: status.sampleRate,
    notes: $('pNotes').value.trim() || null,
    correction: curvePoints,
  };
  try {
    const saved = await api('/calibration/profiles', { method: 'POST', body: JSON.stringify(profile) });
    if ($('pAssign').checked) await assignProfile(req.channelIndex, saved.id, profile.micName ?? ch?.micLabel);
    $('pMsg').innerHTML = `<span class="ok-text">Saved${$('pAssign').checked ? ' and assigned' : ''}.</span>`;
    await loadProfiles();
  } catch (e) { $('pMsg').innerHTML = `<span class="warn">${esc(e.message)}</span>`; }
}

async function assignProfile(channelIndex, profileId, micLabel) {
  const cfg = await api('/config');
  cfg.inputs = cfg.inputs.map((ic) => ic.channelIndex === channelIndex ? { ...ic, profileId, micLabel: ic.micLabel || micLabel || null } : ic);
  await api('/config', { method: 'PUT', body: JSON.stringify(cfg) });
}

async function loadProfiles() {
  profiles = await api('/calibration/profiles');
  $('cProfiles').innerHTML = profiles.length ? profiles.map((p) => `<tr>
      <td>${esc(p.name)}${p.sourceProfileName ? `<div class="muted" style="font-size:11px">from ${esc(p.sourceProfileName)}</div>` : ''}</td>
      <td>${p.offsetDb.toFixed(2)} dB</td>
      <td>${qualityBadge(p.quality, p.uncertaintyDb)}</td>
      <td>${esc(METHOD_LABEL[p.method] ?? p.method)}${p.referenceDescription ? `<div class="muted" style="font-size:11px">${esc(p.referenceDescription)}</div>` : ''}</td>
      <td>${esc(p.micName ?? '')}${p.micSerial ? ` <span class="muted">#${esc(p.micSerial)}</span>` : ''}</td>
      <td>${p.channelIndex != null ? p.channelIndex + 1 : '<span class="muted">any</span>'}</td>
      <td>${p.correction?.length ? `${p.correction.length} pts` : '—'}</td>
      <td>${new Date(p.createdUtc).toLocaleDateString()}</td>
      <td style="white-space:nowrap"><button type="button" data-edit="${p.id}">Edit</button>
          <a href="/api/calibration/profiles/${p.id}/export"><button type="button">Export</button></a>
          <button type="button" data-del="${p.id}">Delete</button></td></tr>`).join('')
    : '<tr><td colspan="9" class="muted">No profiles yet.</td></tr>';
  $('cProfiles').querySelectorAll('[data-del]').forEach((b) => b.onclick = async () => {
    const p = profiles.find((x) => x.id === b.dataset.del);
    if (!confirm(`Delete profile "${p.name}"? Inputs using it become uncalibrated.`)) return;
    await api(`/calibration/profiles/${p.id}`, { method: 'DELETE' });
    await loadProfiles();
  });
  $('cProfiles').querySelectorAll('[data-edit]').forEach((b) => b.onclick = () => editProfile(profiles.find((x) => x.id === b.dataset.edit)));
}

async function editProfile(p) {
  const name = prompt('Profile name:', p.name);
  if (name == null) return;
  const offStr = prompt('Offset in dB (SPL = dBFS + offset). Changing it marks the accuracy as unknown.', p.offsetDb.toFixed(2));
  if (offStr == null) return;
  const off = parseFloat(offStr);
  if (!Number.isFinite(off)) { alert('Offset must be a number.'); return; }
  const changed = Math.abs(off - p.offsetDb) > 0.005;
  const updated = {
    ...p, name: name.trim() || p.name, offsetDb: off,
    ...(changed ? {
      quality: 'Unknown', uncertaintyDb: null,
      notes: [p.notes, `Offset edited by hand from ${p.offsetDb.toFixed(2)} dB on ${new Date().toLocaleDateString()}.`].filter(Boolean).join(' '),
    } : {}),
  };
  await api('/calibration/profiles', { method: 'POST', body: JSON.stringify(updated) });
  await loadProfiles();
}

async function importProfile(file) {
  try {
    await api('/calibration/profiles/import', { method: 'POST', body: await file.text(), raw: true });
    await loadProfiles();
  } catch (e) { alert('Import failed: ' + e.message); }
  $('cImport').value = '';
}

async function loadCurve(file) {
  try {
    curvePoints = await api('/calibration/correction/parse', { method: 'POST', body: await file.text(), raw: true });
    $('pMsg').textContent = `Mic response file: ${curvePoints.length} points loaded.`;
  } catch (e) { curvePoints = null; $('pMsg').innerHTML = `<span class="warn">${esc(e.message)}</span>`; }
}

async function manualProfile() {
  const name = prompt('Profile name:');
  if (!name) return;
  const off = parseFloat(prompt('Offset in dB (SPL = dBFS + offset):', '120'));
  if (!Number.isFinite(off)) return;
  await api('/calibration/profiles', { method: 'POST', body: JSON.stringify({ name, offsetDb: off, method: 'Manual', quality: 'Unknown' }) });
  await loadProfiles();
}

// ---------- wiring ----------
$('btnStart').onclick = async () => { try { await api('/audio/start', { method: 'POST' }); } catch (e) { alert(e.message); } };
$('btnStop').onclick = () => api('/audio/stop', { method: 'POST' });
$('btnReset').onclick = async () => {
  if (!confirm('Reset the session Leq and peak for all inputs?')) return;
  await api('/session/reset', { method: 'POST' });
};
$('btnSettings').onclick = openSettings;
$('sHubMode').onchange = onHubModeChange;
fetch('/api/app').then((r) => r.json()).then((a) => { $('lnkDash').hidden = !a.runsHub; }).catch(() => {});
$('btnCal').onclick = openCalibration;
$('sDevice').onchange = () => onDeviceChange(true);
$('sRate').onchange = onRateChange;
$('sSave').onclick = saveSettings;
$('cMethod').onchange = onMethodChange;
$('cInput').onchange = () => { refreshReferenceInputs(); onMethodChange(); };
$('cMeasure').onclick = measure;
$('pSave').onclick = saveProfile;
$('cImport').onchange = (e) => e.target.files[0] && importProfile(e.target.files[0]);
$('pCurve').onchange = (e) => e.target.files[0] && loadCurve(e.target.files[0]);
$('cManual').onclick = manualProfile;
$('detail').addEventListener('close', () => { detailChannel = null; });

connect();
