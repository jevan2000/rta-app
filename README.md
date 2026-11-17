# RTA App

Multi-mic SPL monitoring (A/C-weighted) for a worship space. See [DESIGN.md](DESIGN.md) for the full design and build phases.

## Status: Phases 1–5 done (NDI added in Phase 4); Phase 6 (installer) to come

**Phase 1: Node**
- Audio capture:
  - **ASIO** (Focusrite, Behringer, …), with **WASAPI** as a fallback.
  - A **virtual 4-channel test-signal device** for trying the app without hardware.
  - Sample rates 44.1, 48, 88.2 and 96 kHz.
- Metering per input:
  - LA, LC and LZ, Fast and Slow.
  - LAeq and LCeq over 1 min, 15 min and the session.
  - LCpeak, C−A, and a 1/3-octave RTA.
  - Clip detection.
- A and C weighting meet the IEC 61672-1 Class 1 tolerances at 48 and 96 kHz (unit-tested).
- Calibration profiles, made by any of several methods (see below), each rated for accuracy.

**Phase 2: Hub**
- **Modes:**
  - **Both** (default): one PC does capture and the Hub.
  - **Node**: a capture-only PC.
  - **Hub**: dashboard and logging only.
- **Discovery:**
  - Nodes find the Hub automatically via a UDP broadcast on port 5181.
  - Alternatively, a fixed Hub address can be entered on the node.
- **Node approval:** new nodes show as *Pending* until an admin accepts them on the Hub. The node on the Hub's own PC is accepted automatically.
- **Store and forward:**
  - Each node logs per-second and per-minute records to a local buffer (7 days).
  - Anything the Hub hasn't acknowledged is resent after a reconnect, so a Hub restart loses no data.
- **Hub database** (SQLite):
  - Per-second LAeq, LCeq and LZeq, peak and Slow max, kept for 1 year (configurable).
  - Per-minute summaries with 1/3-octave bands, kept forever.
- **Services:**
  - A weekly schedule (Hub local time) plus manual start and stop.
  - Live service Leq per mic.
  - A per-service summary: Leq, maximum 1-min Leq, peak, time above the warn and alarm levels, and alerts.
- **Alerts:**
  - Rules per mic (or all mics) on any measure: Slow, 1/15-min Leq, service Leq, or peak.
  - Warn and alarm levels, a minimum hold time and hysteresis.
  - A live banner, plus an event log.
- **Dashboard** (React + TypeScript, at `/` on the Hub):
  - Live cards grouped by node.
  - Mic detail: level vs time, Leq tiles, RTA with peak hold, C−A trend, plus optional spectrogram and histogram.
  - History for several mics at once, by time range or by service.
  - Pages for services, alerts, nodes and settings.
- **Admin password:**
  - Viewing is open to anyone on the network. Every change needs the password (PBKDF2 hash with a sign-in cookie).
  - Until a password is set, changes are allowed only from the Hub PC itself.
- The node page (audio, inputs, calibration) has moved to `/node/`. It shows the Hub connection status and has Hub settings.

**Phase 3: survey and zone prediction**
- **Plan editor** (dashboard → **Plan**):
  - Draw the room in metres on a grid: outline, stage, seating blocks, aisles, walls and text.
  - Draw **zones** as rectangles or polygons.
  - Optionally trace over a PNG/JPEG/WebP photo or plan. Multiple floors are supported.
  - Place each mic as **Permanent** (used for predictions) or **Roving** (the survey mic).
- **Survey** (dashboard → **Survey**):
  1. Pick a zone and the roving mic, and press Start.
  2. Every permanent mic is recorded over exactly the same time, as gap-free 1/3-octave data.
  3. A confidence bar fills with time and steadiness (minimum 60 s by default).
  4. Save it, or discard it.
  - A **handheld meter reading** can be entered instead. It's single-number, so lower confidence.
- **Model:**
  - Each zone gets a per-band offset from each permanent mic.
  - Mics are weighted by how steadily they tracked the zone, and a broadband correction keeps the A/C totals exact.
  - Predictions follow changes in the mix (for example more bass) band by band, which a single dB offset can't.
  - Resurveys replace the old data by default, or average with it (set per zone).
- **Room view** (dashboard → **Room**):
  - The blueprint with each zone shaded by its predicted level: now (1 s), 1 min, 15 min or service, in dBC or dBA.
  - Live mic readings shown on the plan.
  - Click a zone for the same graphs as a mic.
- **Zones log like mics** (per second and per minute, under `zones:<id>`). They appear in History, service summaries and **alert rules** (per zone or "every zone").
- **Stale data:**
  - If a permanent mic is moved on the plan, removed or offline, the zone is flagged and keeps predicting at lower confidence.
  - Recalibrating a permanent mic after a survey is **compensated automatically**.

**Phase 5: integrations and reports** (dashboard → **Integrations**; built before Phase 4)
- **ProPresenter** (7.9+, including 21 and 22, via its network API):
  - Alarms (and optionally warnings) appear as a **stage-display message**, for example "LEVEL ALARM: Balcony 104 dBC (limit 103)", and clear when the level drops.
  - An optional live level line on the stage display.
  - An opt-in **Message** readout with a text token, for the audience output. It's off by default.
  - A **Test** button checks the connection and shows a test stage message for 4 s.
- **OSC** to any number of targets:
  - Levels at `/rta/mic/<name>/<measure>` and `/rta/zone/<name>/<measure>`.
  - Alert state at `…/alert`, as 0, 1 or 2.
  - `/rta/alert` the moment an alert changes, and `/rta/service/active`.
- **MIDI out:**
  - A CC per chosen level, scaled over a dB range to 0–127.
  - A note held while any warning or alarm is active.
- **Webhooks:** JSON POSTs on alert start, escalate and clear and on service start and end (Teams/Slack workflows, Home Assistant, Zapier…), with retries.
- **Read-only HTTP and WebSocket API** (no sign-in):
  - `/api/v1/levels` (JSON).
  - `/api/v1/levels/<name>/<measure>`: the plain number as text, for vMix/OBS text sources or Companion.
  - `/ws/v1/levels?hz=2`: the same JSON, streamed.
- **PDF service reports:**
  - Written automatically about 90 s after each service ends, to a folder you choose (a synced or shared folder works well).
  - Also open from any service's page.
  - Contents: Leq per mic and zone, maximum 1-min LCeq, peak, time above the warn and alarm levels, a level chart, alerts, and calibration notes.
- **CSV export:** per second (or per minute for long ranges) for any mics and zones from History, or everything for a service. Spreadsheet formulas in names are neutralised.

**Phase 4: NDI outputs** (needs NDI Tools or the NDI Runtime from ndi.video on each PC that publishes; the app finds it automatically)
- **Per input, on each node** (node page → Settings → Inputs):
  - **NDI audio**: the raw mic audio, unweighted, 1 channel at the node's sample rate.
  - **NDI meter**: a 640×360 meter video (dBC/dBA, Leq 1 min/15 min/session, bar with warn/alarm marks, mini RTA). The levels are also sent as NDI metadata `<rta_levels … lcs="84.5" lceq1m="84.1" …/>`.
- **From the Hub** (dashboard → Integrations → NDI video):
  - **RTA Dashboard**: every mic (and optionally every zone) on one 720p/1080p/4K frame, with alert banner, service and clock.
  - Optional **RTA Zone &lt;name&gt;** feed per zone.
- **Names:** sources appear as `<PC name> (RTA Mic 1 meter)` and so on. Use them as inputs in vMix, OBS, ProPresenter (live video input) or a confidence monitor.
- **Bandwidth:** each source shows an estimate, and the app warns above about 40 Mb/s and 100 Mb/s, or more than 8 sources from one PC.
- **Checking feeds:** **Find sources / Check** on the Integrations page lists NDI sources on the network and confirms frames arrive (size, audio format, metadata).

**Not yet:**
- Windows service installer (Phase 6).

## Run

Requires the .NET 10 SDK, and Node.js 20+ to build the dashboard.

```bash
cd web && npm ci && npm run build && cd ..
```

```bash
dotnet run --project src/Rta.App --no-launch-profile
```

Then open http://localhost:5180 for the dashboard, or http://localhost:5180/node/ for this PC's node. The node starts on the test signal; pick your interface in the node's **Settings**. The first time you change something, the app asks you to set an admin password.

Options (command line or `appsettings.json`):

- `--Mode Both|Node|Hub` (default `Both`).
- `--Urls`: the listen address. The default is `http://0.0.0.0:5180` for a Hub (reachable on the LAN) and `http://localhost:5180` for a node-only PC.
- `--DataDir <folder>`: where config, calibration, buffers and the Hub database live (default `C:\ProgramData\RtaApp`).

### Adding another PC as a node

1. On the second PC, run with `--Mode Node`.
2. Open http://localhost:5180/node/ there, pick the interface, and calibrate.
3. It finds the Hub automatically. On the Hub, open **Nodes** and press **Accept**. Everything it has logged in the meantime is sent.
4. If it can't find the Hub (different subnet or VLAN), set **Settings → Hub → At a fixed address** on the node.
5. On the Hub PC, Windows Firewall must allow TCP 5180 and UDP 5181. Windows usually asks the first time the app runs.

### Dashboard development

`npm run dev` in `web/` serves the dashboard at http://localhost:5173 with hot reload, proxying the API to a running app on port 5180.

## Calibrating a mic

Set the preamp gain where it will stay, enable the input in **Settings**, and type which mic is plugged in. Then open **Calibration**, pick the input and a method:

| Method | Accuracy (typical) | How |
|---|---|---|
| **Acoustic calibrator** | Reference grade, ±0.4–0.8 dB | Fit the 94/104/114 dB calibrator (1 kHz, or 250 Hz pistonphone), press Measure. |
| **Match an SPL meter** | Good, ±1–1.5 dB (Class 1/2). Unreliable, ±3 dB (consumer/unrated) | Meter next to the mic, steady pink noise, same weighting, type the meter's reading. |
| **Copy from another calibrated mic** | Source ± 0.5 dB | Put this mic next to a calibrated one, play pink noise, press Measure. It inherits the source's rating, so a copy of an unreliable profile is unreliable too. |
| **Phone meter app** | **Unreliable**: about ±3 dB on iPhone (e.g. NIOSH SLM), ±6 dB or worse on Android | Same as meter match, with the phone as the reference. |
| **Estimate from specs** | **Very rough**: ±6 dB or worse | Mic sensitivity (mV/Pa), interface max input at minimum gain (dBu), and gain added (dB). No measurement. |
| **Manual** | Unknown | Type an offset. |

Profiles rated Unreliable or Very rough show a dashed amber **≈** badge on the node page and the dashboard, with a warning line. Editing a profile's offset by hand resets its rating to Unknown.

### Setting up zone prediction

1. **Plan:**
   - Draw the room and the listening zones (for example "Front center", "Under balcony", "Balcony left").
   - Place the permanent mics where they really are.
   - Place the roving mic anywhere, marked *Roving*.
   - Save.
2. **Calibrate** the roving mic. Copying from a permanent mic side by side is fine. Uncalibrated permanent mics still work, but a calibrated rover is what makes zone levels real dB SPL.
3. **Survey:**
   1. Take the roving laptop to a typical seat in each zone.
   2. Choose the zone and press Start.
   3. Let it run for at least a minute of service sound or pink noise, then Stop & save.
4. **Later:**
   - The **Room** page shows the predicted levels.
   - If you move a permanent mic, the affected zones are flagged; resurvey them.

Suggested workflow without a calibrator:
1. Calibrate one mic with the best reference you have (a meter, otherwise a phone).
2. **Copy** that calibration to every other mic. The mics then agree with each other, which is what the zone predictions depend on.
3. When the calibrator arrives, recalibrate one mic and copy it again. New profiles take effect immediately.

Changing preamp gain after calibrating makes the reading wrong; recalibrate.

## Test

```bash
dotnet test
```

```bash
cd web && npm run typecheck
```

## Notes and known limits

- Calibrated SPL = 10·log10(mean square re digital full scale) + profile offset.
- Records are calibrated on the node with the profile current at that moment. Changing a profile does not rewrite history that is already logged.
- A mic response file corrects the RTA and minute bands only; broadband A, C and Z use the flat offset.
- At 48 kHz the 20 kHz band is squeezed near Nyquist (about 12 dB adjacent-band rejection instead of 18 dB). The band is kept so 48 and 96 kHz nodes report the same band set.
- WASAPI runs at the rate set in Windows Sound settings, and Windows "audio enhancements" must be off for the input.
- The node page's own graphs still keep history only in the browser. Stored history is on the Hub dashboard.
- The ProPresenter Message readout sends the token in the v1 API's documented trigger format. Versions differ, so confirm it with your ProPresenter (the stage message path is the standard one).
- MIDI has been built against the Windows MIDI API but not yet tried with a real device or loopMIDI.
- NDI: the app doesn't ship the NDI runtime; each PC needs NDI Tools or the NDI Runtime installed. Before distributing the app, follow NDI's SDK license terms (attribution, runtime redistribution). NDI® is a registered trademark of Vizrt NDI AB.
- NDI feeds have been verified on this PC through the NDI 6 runtime (found and received). They have not yet been viewed in vMix, OBS, ProPresenter or NDI Studio Monitor, or sent across to another PC.
- PDF reports use QuestPDF under its Community license (free for organisations under US$1M annual revenue). Check its terms before distributing the app.
- Zone predictions are made live on the Hub. Readings a node backfills after an outage are stored for the mics, but zone history for that gap is not reconstructed.
- Mic identity on the Hub is node + input number. Moving a mic to another input or PC creates a new entry; the old history stays under the old one.
