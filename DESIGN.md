# RTA App — Design

Multi-mic SPL monitoring and zone prediction for a church worship space. Windows-only.
Status: design agreed 2026-11-17. Phase 1 (Node) built 2026-11-17 — see README.md.

## Goals

- Measure dB SPL (A- and C-weighted) from multiple RTA mics simultaneously, in real time.
- Mics may be on several computers; readings are aggregated on one Hub.
- Survey seating zones once (e.g. one Sunday), then predict zone levels on later Sundays from the permanent mics.
- Publish readings and audio over NDI and many control/AV integrations (ProPresenter first).
- Log every service; alert on thresholds; export CSV/PDF.

## Architecture

- Single **.NET 10** (LTS) application, installed as a **Windows service** with a tray icon for status. Mode chosen at install: **Node**, **Hub**, or **Node + Hub**.
- **Node**: captures audio, does all metering locally, publishes levels and NDI feeds.
  - Audio via **ASIO** (primary; Focusrite, Behringer) with **WASAPI** fallback.
  - 48 kHz default; **96 kHz** allowed with a CPU/network resource warning.
  - Buffers ~7 days of level data locally; backfills the Hub after an outage.
- **Hub**: exactly one per installation; aggregates nodes, runs prediction, alerts, logging, reports, integrations; serves the web dashboard.
- **Node ↔ Hub transport**: lightweight, auto-discovered (mDNS/DNS-SD + WebSocket). Not NDI.
- **Scale**: designed for 2–4 mics; more allowed with a network-congestion warning (especially with NDI audio enabled). Wired Ethernet recommended.

## Metering

- dB SPL re 20 µPa. Measurement-grade, **not** IEC 61672 Class 1 certified (dbx RTA-M / Behringer ECM8000 mics).
- Metrics per mic (and per predicted zone where meaningful):
  - LCeq / LAeq over 1 min, 15 min, service
  - LC / LA Slow (live), Fast available
  - LCpeak
  - C−A difference (bass-heaviness indicator)
  - 1/3-octave RTA
  - Budget-style "% of service target used"
- **C and A are the defaults.** Warn levels user-configurable.

## Calibration

- **Calibration profile** is a free-standing object: offset, optional frequency-correction curve, metadata (mic name/serial, channel, gain position, date, notes).
- Each live input is assigned exactly one profile — manually or auto-suggested by matching mic/channel. Binding to mic or channel is optional (maximum flexibility).
- App **warns, never blocks**, when profile metadata mismatches the input it is on, or when a level jump suggests a gain change.
- Methods (added 2026-11-17, all available):
  - acoustic calibrator (1 kHz or 250 Hz, any level, Class 1/2/unknown)
  - SPL-meter match (Class 1/2/consumer)
  - copy from another calibrated mic placed side by side, inheriting its accuracy
  - phone meter app (always flagged unreliable)
  - spec-sheet estimate (very rough)
  - manual offset
- Every profile stores a **quality rating** (Reference grade, Good, Unreliable, Very rough, Unknown) and a typical ±dB uncertainty. Unreliable and very rough profiles are badged on every reading and listed as input warnings.
- Profiles import/export as files.

## UI (web dashboard)

- **React + TypeScript**, served by the Hub; canvas charts (uPlot-style) for 10–20 Hz live updates; works on iPad/phone.
- **Access**: open view-only; config, calibration, and survey behind a password.
- **Live view**: simple **blueprint/schematic-style** room plan; zones shaded by predicted level; mic icons show live numbers.
- **Plan editor**: built-in shape editor (room outline, stage, aisles, seating blocks) with optional uploaded image/PDF underlay to trace. Zones are **polygons**. Multiple floor layers supported (optional).
- **Mic/zone detail view** (click a mic or zone):
  - Default: (a) level vs time (C/A Slow + warn line), (b) Leq tiles, (c) live 1/3-octave RTA with peak hold, (d) C−A trend
  - Optional tabs: (e) spectrogram, (f) level histogram
  - Time ranges: live / last 15 min / this service

## Survey & prediction

- **Roving survey**: a laptop running in Node mode. Workflow: open survey screen (password) → pick zone on plan → Start → capture ≥ 60 s synchronized with permanent mics, confidence bar fills → Stop/Save → next zone. Can be driven from a phone/tablet via the Hub UI.
- **Manual entry**: zone + dBA/dBC from a handheld meter + timestamp, matched to the permanent-mic log; single-number only, flagged lower confidence.
- Signal: typically a live service; pink noise also supported.
- **Model**: per-zone, per-1/3-octave-band offsets from each permanent mic; prediction is a weighted blend across permanent mics; A/C totals recomputed from bands.
- **Resurvey**: replace by default (old surveys kept in history); per-zone option to average.
- **Staleness**: if a permanent mic is moved/recalibrated/swapped, affected zones are flagged **"needs resurvey"** and keep predicting with lower confidence.

## Alerts

- Rule = target (mic or zone) + metric (C/A, Slow or Leq 1/15/service) + **warn** and **alarm** levels, with hysteresis and minimum duration.
- Global defaults (warn 98 / alarm 103 dBC LCeq-1min, editable) with per-zone overrides.
- Delivered to: UI colors, NDI overlay, OSC/HTTP/WebSocket, MIDI, webhook, ProPresenter stage message.

## NDI outputs (each toggleable)

- Per mic: **`<Node> – Mic N Audio`** (raw audio), **`<Node> – Mic N Meter`** (rendered video meter + JSON level metadata).
- Hub: **`RTA Hub – Dashboard`** (full UI, rendered natively with SkiaSharp, not screen capture), optional **`RTA Hub – Zone <name>`** per zone.

## Integrations

- **OSC** (configurable address patterns)
- **WebSocket** JSON stream
- **HTTP REST** (current values + history)
- **Webhook** on alarm
- **MIDI out**: CC per mic/zone level (scaled over a configurable dB range), note-on on warn/alarm
- **Browser stage-view URL** (for web sources in ProPresenter/OBS/vMix)
- **Bitfocus Companion**: generic OSC/HTTP presets in v1; dedicated module later
- **ProPresenter** (official network API, 7.9+; target 21.x and 22.x):
  - Settings: host + port + Test button reporting detected version
  - Alarm → `PUT /v1/stage/message`, cleared when level recovers
  - Optional live readout via a Message with a token, rate-limited ~1/s
  - **Stage display by default; main/audience output possible but opt-in**
  - Also usable without the API: NDI meter feed as live input, or stage-view URL as web fill
  - TCP API variant (17+) available for streaming

## Data, logging, reports

- Hub storage: local **SQLite**.
- Retention (configurable): 1-s data for 1 year; 1-min aggregates forever; service reports permanent.
- Logging is continuous; **services** are labeled time ranges from a **weekly schedule** plus **manual start/stop** override.
- **PDF report auto-generated and saved to a folder** after each service: header (date/service/schedule), per mic and zone LAeq/LCeq service, max 1-min Leq, LCpeak, time above warn/alarm, level-vs-time chart, alert log, survey/model confidence notes. No email.
- **CSV export** of 1-s or 1-min data for any time range.

## Distribution

- Internal use for now; may be distributed later. Build generically (no hardcoded room, vendor, or church assumptions).
- Deferred until distribution: NDI SDK attribution/runtime redistribution terms, installer code signing, licensing.

## Build phases

1. Node: capture (ASIO/WASAPI), calibration, A/C metering, local UI
2. Hub: discovery, multi-mic dashboard, logging, alerts
3. Survey, zone prediction, blueprint editor
4. NDI outputs (audio, meter, dashboard, zones)
5. Integrations (OSC/HTTP/WS/MIDI/webhook/ProPresenter) + PDF/CSV reports
6. Installer + Windows service hardening

## Decisions made during Phase 1

- **.NET 10** instead of .NET 8: .NET 8 support ended 10 Nov 2026.
- Calibrated SPL = 10·log10(mean square re full scale) + profile offset. A full-scale sine reads -3.01 dBFS.
- Weighting filters use a bilinear transform of the IEC 61672 analog poles. Unit tests confirm they are within Class 1 tolerance at 48 and 96 kHz.
- 1/3-octave bands are 6th-order Butterworth with pre-warped edges, 31 bands from 20 Hz to 20 kHz at 48 kHz and above. The 20 kHz band is kept at 48 kHz, with weaker skirts, so band sets match across nodes.
- A mic response curve corrects the RTA bands only. Broadband A/C/Z use the flat offset. Revisit if correction curves matter for broadband readings.
- Calibration is applied when readings are computed, not when audio is captured. Assigning or changing a profile updates readings at once and keeps the Leq history.
- The Phase 1 node binds to localhost with no password. Password-protected network access arrives with the Hub (Phase 2).
- Default dBA warn/alarm levels are 92/97 (dBC stays 98/103). All four are editable.
- The node's local UI is plain HTML/JS. The React dashboard is a Phase 2 Hub deliverable.
- Deferred to the Phase 2 Hub: the "% of budget used" metric (it needs service definitions) and the gain-change heuristic beyond clip detection.

## Open items to verify during build

- ProPresenter API auth requirements (none found in the 7.9 spec) and exact token payload for message triggers — check the in-app "API Documentation" button.
- ProPresenter 22 release notes mention a fixed Windows bug where API messages didn't show on output — test message triggering on 21.x and 22.x.
- User reported version "21.0.3"; release notes list 21, 21.0.1, 21.1 — confirm actual installed version.
- ASIO capture is implemented but not yet tested against the real Focusrite/Behringer hardware (WASAPI and the test signal are).
