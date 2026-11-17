# RTA App

Multi-mic SPL monitoring (A/C-weighted) for a worship space. See [DESIGN.md](DESIGN.md) for the full design and build phases.

## Status: Phase 1 (Node)

Working:

- Audio capture: **ASIO** (Focusrite, Behringer, …), **WASAPI** fallback, and a **virtual test-signal device** (4 channels) for trying it without hardware. 44.1 / 48 / 88.2 / 96 kHz.
- Metering per input: LA/LC/LZ Fast & Slow, LAeq/LCeq over 1 min, 15 min and session, LCpeak (max 1 min / session), C−A, 1/3-octave RTA (20 Hz–20 kHz), clip detection.
- A and C weighting meet IEC 61672-1 Class 1 tolerances at 48 and 96 kHz (unit-tested).
- Calibration profiles, made by any of several methods (below), each rated for accuracy. Optional mic response file (REW/.frd/miniDSP text), import/export between PCs, editing, and free assignment to any input with warnings (never blocking) when the mic or channel doesn't match.
- Local web UI at http://localhost:5180: live cards, warn/alarm colours, detail view (level vs time, Leq tiles, RTA with peak hold, C−A trend), settings and calibration.

Not yet (later phases): Hub and multi-node discovery, logging/history, password-protected network access, survey/prediction, NDI, integrations, reports, Windows service installer.

## Run

Requires the .NET 10 SDK.

```bash
dotnet run --project src/Rta.App --no-launch-profile -- --Urls http://localhost:5180
```

Then open http://localhost:5180. It starts on the test signal; pick your interface in **Settings**.

Options:

- `--DataDir <folder>`: where config and calibration profiles live (default `C:\ProgramData\RtaApp`).
- `--Urls`: listen address. Phase 1 is meant to stay on `localhost`; there is no password yet.

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

Profiles rated Unreliable or Very rough show a dashed amber **≈** badge on the input's card and a warning line. Editing a profile's offset by hand resets its rating to Unknown.

Suggested workflow without a calibrator: calibrate one mic with the best reference you have (meter, then phone), then **copy** it to every other mic. All the mics then agree with each other, which is what the zone predictions depend on. When the calibrator arrives, recalibrate one mic and copy it again. New profiles take effect immediately.

Changing preamp gain after calibrating makes the reading wrong; recalibrate.

## Test

```bash
dotnet test
```

## Notes and known limits

- Calibrated SPL = 10·log10(mean square re digital full scale) + profile offset.
- A mic response file corrects the RTA bands only; broadband A/C/Z use the flat offset.
- At 48 kHz the 20 kHz band is squeezed near Nyquist (about 12 dB adjacent-band rejection instead of 18 dB). The band is kept so 48 and 96 kHz nodes report the same band set.
- WASAPI runs at the rate set in Windows Sound settings, and Windows "audio enhancements" must be off for the input.
- Graphs in the local UI hold history in the browser only (lost on reload). Stored history arrives with the Hub in Phase 2.
