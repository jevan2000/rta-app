# RTA App

Multi-mic SPL monitoring (A/C-weighted) for a worship space. See [DESIGN.md](DESIGN.md) for the full design and build phases.

## Status: Phase 2 (Hub)

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

**Not yet (later phases):**
- Survey and zone prediction with the blueprint editor (Phase 3).
- NDI (Phase 4).
- OSC, HTTP, MIDI, ProPresenter and webhooks, plus PDF and CSV reports (Phase 5).
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
- Mic identity on the Hub is node + input number. Moving a mic to another input or PC creates a new entry; the old history stays under the old one.
