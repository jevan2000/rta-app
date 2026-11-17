# RTA App — Design

Multi-mic SPL monitoring and zone prediction for a church worship space. Windows-only.
Status: design agreed 2026-11-17. Phase 1 (Node) built 2026-11-17. Phase 2 (Hub) merged 2026-11-17. Phases 1–3 and 5 are merged to `master`. Phase 4 (NDI) built 2026-11-17 on branch `phase-4`. See README.md.

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
- **Node ↔ Hub transport**: one WebSocket per node (`/ws/node`, JSON envelopes). Not NDI. Discovery is a UDP broadcast beacon on port 5181, with a fixed Hub address as fallback. See the Phase 2 decisions below for why not mDNS.
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

## Decisions made during Phase 2

- **Discovery is a UDP broadcast, not mDNS.** Windows' built-in mDNS responder holds port 5353, which makes app-level mDNS unreliable. The Hub broadcasts a small JSON beacon every 2 s on each IPv4 network (port 5181). Nodes on another subnet use a fixed Hub address.
- **Node approval**: new nodes are *Pending* and their records are ignored until an admin accepts them. Auto-accept is an option, off by default. The node on the Hub's own PC (loopback + same node id) is accepted automatically.
- **Store and forward**: nodes calibrate and build per-second and per-minute records locally, aligned to wall-clock seconds and minutes. Each record gets a sequence number in a local SQLite buffer (7-day retention). The Hub acknowledges the highest stored sequence, and unacknowledged records are resent after a reconnect. Stores are idempotent: `(mic, ts)` is the primary key. A new buffer id resets the Hub's acknowledgement.
- **Hub storage**: SQLite with levels as integer centi-dB.
  - Per-second rows hold LA, LC and LZ eq, LCpeak, LAS and LCS max, coverage and flags: about 1 GB per mic per year, kept 365 days by default.
  - Per-minute rows add the 1/3-octave bands (int16 centi-dB) and are kept forever.
- **Mic identity** is `nodeId:channel`. Display names and hiding are Hub-side.
- **Records are calibrated at emission** with the profile current at that moment. Recalibrating does not rewrite history.
- **Services**: a weekly schedule in Hub local time, plus manual start and stop. A manually stopped scheduled occurrence is not restarted. Manual services auto-end after 6 h. "Service Leq" is tracked live in memory from incoming per-second records and rebuilt from the database on restart.
- **Alerts**:
  - Rules are evaluated on the Hub every 500 ms on live values.
  - Levels go normal → warn → alarm. Rising requires a hold time; falling requires dropping below the threshold minus the hysteresis.
  - Events are stored. Phase 2 delivers alerts to the dashboard only; the outbound integrations are Phase 5.
- **Auth**: one admin password per machine (PBKDF2-SHA256, 210k iterations) with a 12 h sliding cookie.
  - Viewing (GET and WebSockets) is open. Every other `/api` call needs admin.
  - With no password set, only loopback may change things, and the first password can only be set from the PC itself.
  - Five wrong passwords lock sign-in for 1 minute.
- **Pages**: the dashboard is React + TypeScript (Vite 6, uPlot) at `/`. The node page moved to `/node/`. The dashboard build output is not committed; the csproj builds it when it's missing.
- **Default listen address**: Hub and Both use `0.0.0.0:5180` (LAN); node-only uses `localhost:5180`.

## Decisions made during Phase 3

- **Gap-free band data:** each live message (5×/s) carries `Interval` levels: calibrated LA, LC, LZ and band Leq over exactly the audio since the previous message. The Hub keeps 15 minutes of these per mic, timestamped on arrival at the Hub. That gives time alignment across nodes to about 0.2 s, which is plenty over a 60 s+ survey.
- **Survey:**
  - Accumulates the rover and every permanent (plan) mic over the same window, plus 5-s chunks of the rover−ref dBC offset.
  - Confidence = duration factor × steadiness (standard error of the chunk offsets) × band coverage (share of the 50 Hz–10 kHz bands with signal).
  - Manual meter entries use the refs' stored per-second Leq over the stated window and have fixed low confidence (0.2–0.3).
- **Model per zone:**
  - Band offsets O[r,b] = zone − ref_r per 1/3-octave band. Bands more than 50 dB below the loudest band during the survey fall back to the broadband offset.
  - Ref weight = 1/(stderr² + 0.25). The 0.25 floor stands for about 0.5 dB of model error no survey removes.
  - Prediction: weighted mean across refs per band, then the A/C band sum plus K (rover broadband − band sum, measured in the survey).
  - Manual-only zones use broadband offsets.
  - Replace mode uses the newest included survey; Average weights included surveys by confidence.
- **Calibration compensation:** each survey stores every ref's calibration offset and profile id. Live predictions subtract (current offset − offset at survey), so recalibrating a permanent mic doesn't skew zones. A profile change is reported as information, not as stale.
- **Stale** = a ref moved more than 0.5 m on the plan, or removed from the plan. Displayed confidence = model confidence × 0.5 if stale × (refs online / refs in model).
- **Zones are virtual mics** `zones:<zoneId>`, logged per second (1-s predicted Leq) and per minute (with bands) by the Hub. History, services and alerts work unchanged. Alert targets: `*` (all mics), `zones:*` (all zones), or a single key.
- **Plan** is stored as `plan.json` in metres. Zone ids are Hub-wide integers and never reused. Underlay images (PNG/JPEG/WebP, ≤ 15 MB, checked by magic bytes; SVG is refused) live in `plan-images/`.
- **Caching:** HTML is served `no-cache`; hashed `/assets` are cached for a year, so dashboard updates load straight away.

## Decisions made during Phase 5

- **Order:** Phase 5 was built before Phase 4 (NDI). Reports and the ProPresenter alarm are more useful week to week, and Phase 4 needs the NDI SDK, which the user installs under NDI's license.
- **One integration loop** (`IntegrationService`) runs every 200 ms on the Hub snapshot:
  - It detects alert start, escalate, de-escalate and clear, and service start and end, by comparing ticks.
  - Each output keeps its own rate and a status for the UI.
  - Alert clears report the last known alert details.
- **Stable names for other systems:** mic and zone labels are turned into slugs (lowercase, accents stripped, `_`). Duplicate names get the node name added. Measures: lcs, las, lceq1m, laeq1m, lceq15m, laeq15m, lceqservice, laeqservice, lcpeak1m, cminusa.
- **ProPresenter:**
  - The stage message (`PUT/DELETE /v1/stage/message`) carries alarms, which override the optional readout line.
  - The Message readout (`POST /v1/message/{id}/trigger` with `[{name, text:{text}}]`) is opt-in because it can reach the audience output.
  - Updates go out at most once a second and only when the text changes.
  - All request bodies are sent with a Content-Length: some embedded HTTP servers can't read chunked uploads, which .NET's `*AsJsonAsync` uses.
- **MIDI:** NAudio.Midi 2.2.1 (it matches the other NAudio 2.2.1 packages; 3.x would force an NAudio.Core upgrade). CCs are sent on change and refreshed every 5 s; warn and alarm notes are held while active.
- **Webhooks** are queued (bounded, oldest dropped) and retried twice with backoff.
- **Integration tests** need admin, since they send real messages. The `/api/v1` levels API is read-only and open, like the dashboard.
- **Reports:**
  - QuestPDF (Community license), Letter size.
  - The chart is an SVG built from 10-s energy averages; uncalibrated mics are left out of it.
  - Missing-glyph errors are off, so unusual characters in names can't break a report.
  - Reports open inline in the browser (`?download=1` to save).
  - The automatic pass runs every 30 s and writes `<date> <time> <name> (<id>).pdf` once per ended service.
- **CSV** is long format (one row per source per interval), uses the per-minute table for ranges over 3 h unless asked otherwise, and prefixes `'` to fields starting with `= + - @`.

## Decisions made during Phase 4

- **No SDK dependency.** A small P/Invoke layer (`Ndi/NdiInterop.cs`) calls `Processing.NDI.Lib.x64.dll`, found through `NDI_RUNTIME_DIR_V6`/`V5` (set by NDI Tools or the NDI Runtime) or the default install folders. Without it, NDI features show "runtime not found" and everything else keeps working.
- **API used:** `send_create`, `send_video_v2` (BGRA, progressive, synthesized timecode), `send_audio_v2` (planar float), `send_metadata`, `send_get_no_connections`, plus `find_*` and `recv_*_v2` for the in-app check. A unit test round-trips a source through the real runtime and skips itself if NDI isn't installed.
- **Feeds:**
  - Node: per input, `RTA <input> audio` (raw, mono) and `RTA <input> meter` (640×360 video plus `<rta_levels/>` XML metadata), switched per input in the node config.
  - Hub: `RTA Dashboard` (configurable 720p/1080p/4K at 1–30 fps) and optional `RTA Zone <name>` per surveyed zone.
- **Rendering** is native SkiaSharp 3.119 into a reused BGRA bitmap, not a captured browser, in the dashboard's colours. Tiles scale from a 640×360 design.
- **Clocking:** senders are created with clock_video/clock_audio off. Our loops pace video, and audio follows the capture callback, so NDI never blocks the metering thread.
- **Bandwidth estimate:** video ≈ 125 Mb/s × (pixels / 1080p) × (fps / 60), an upper bound since graphics compress well; audio = channels × rate × 32 bit. Warnings appear above 40 and 100 Mb/s, or more than 8 sources from one PC.
- **Attribution:** "NDI® is a registered trademark of Vizrt NDI AB" is shown on the NDI settings and in the code. The runtime is not redistributed.

## Open items to verify during build

- ProPresenter API auth requirements (none found in the 7.9 spec) and exact token payload for message triggers — check the in-app "API Documentation" button.
- ProPresenter 22 release notes mention a fixed Windows bug where API messages didn't show on output — test message triggering on 21.x and 22.x.
- User reported version "21.0.3"; release notes list 21, 21.0.1, 21.1 — confirm actual installed version.
- ASIO capture is implemented but not yet tested against the real Focusrite/Behringer hardware (WASAPI and the test signal are).
- View the NDI feeds in a real receiver (NDI Studio Monitor, vMix, OBS, ProPresenter) and from a second PC.
- ProPresenter integration has been tested only against a stand-in HTTP server. Test it on the real ProPresenter 21/22 machine: the stage message, the Message token format, and whether the API needs a password.
- MIDI output is untested on a real device.
- Zone prediction has been tested with test signals only. Validate it in the room: survey a zone, then compare the prediction against the roving mic left in place during a later service.
- Multi-PC discovery has only been tested with two processes on one PC (the beacon went out over the LAN interface). Test it with a second real PC, and check that Windows Firewall prompts for TCP 5180 and UDP 5181.
