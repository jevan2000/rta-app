using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Rta.Audio;

namespace Rta.App.Hub.Integrations;

public sealed record IntegrationStatus(bool Ok, string Message, DateTimeOffset At);

public sealed record AlertEvent(string Event, ActiveAlertView Alert, string SourceLabel, string SourceKind);

/// <summary>
/// Pushes levels, alerts and service changes to OSC, MIDI, webhooks and ProPresenter. Runs every
/// 200 ms on the Hub's live snapshot; each output has its own rate and keeps a status for the UI.
/// </summary>
public sealed class IntegrationService(HubState state, HubSettingsStore settings, ProPresenterClient pro, IHttpClientFactory http, ILogger<IntegrationService> log)
    : BackgroundService
{
    private readonly ConcurrentDictionary<string, IntegrationStatus> _status = new();
    private readonly Channel<object> _webhooks = Channel.CreateBounded<object>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly OscSender _osc = new();
    private Dictionary<string, ActiveAlertView> _prevAlerts = [];
    private long? _prevService;
    private bool _first = true;
    private DateTimeOffset _lastOsc = DateTimeOffset.MinValue;

    private MidiOutput? _midi;
    private string? _midiDevice;
    private readonly Dictionary<int, int> _lastCc = [];
    private DateTimeOffset _lastCcRefresh = DateTimeOffset.MinValue;
    private readonly HashSet<int> _notesOn = [];

    private string? _lastStage;
    private string? _lastMessage;
    private DateTimeOffset _lastPro = DateTimeOffset.MinValue;
    private bool _proBusy;

    public IReadOnlyDictionary<string, IntegrationStatus> Status => _status;

    private void Ok(string k, string msg) => _status[k] = new IntegrationStatus(true, msg, DateTimeOffset.UtcNow);
    private void Fail(string k, string msg)
    {
        var prev = _status.GetValueOrDefault(k);
        if (prev is null || prev.Ok || prev.Message != msg) log.LogWarning("{Integration}: {Message}", k, msg);
        _status[k] = new IntegrationStatus(false, msg, DateTimeOffset.UtcNow);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _ = Task.Run(() => WebhookWorker(ct), ct);
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Integration tick failed"); }
            try { await Task.Delay(200, ct); } catch (OperationCanceledException) { break; }
        }
        CloseMidi();
        _osc.Dispose();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var cfg = settings.Current.Integrations;
        var live = state.Snapshot();
        var snap = LevelsView.Build(live);

        // ----- alert & service transitions -----
        var alerts = live.Alerts.ToDictionary(a => $"{a.RuleId}|{a.MicKey}", a => a);
        var events = new List<AlertEvent>();
        if (!_first)
        {
            foreach (var (k, a) in alerts)
            {
                if (!_prevAlerts.TryGetValue(k, out var prev)) events.Add(Ev("alert.start", a, snap));
                else if (prev.Level != a.Level) events.Add(Ev(a.Level == "alarm" ? "alert.escalate" : "alert.deescalate", a, snap));
            }
            // Cleared: report the last known details (the rule may even have been deleted).
            foreach (var (k, prev) in _prevAlerts.Where(x => !alerts.ContainsKey(x.Key)))
                events.Add(Ev("alert.clear", prev with { Level = "normal" }, snap));
        }
        _prevAlerts = alerts;
        var serviceId = live.Service?.Id;
        var serviceEvent = !_first && serviceId != _prevService ? (serviceId is null ? "service.end" : "service.start") : null;
        var endedServiceId = _prevService;
        _prevService = serviceId;
        _first = false;

        // ----- webhooks -----
        if (cfg.Webhooks.Enabled && cfg.Webhooks.Urls.Count > 0)
        {
            foreach (var e in events.Where(e => e.Event != "alert.clear" || cfg.Webhooks.OnAlertClear))
                _webhooks.Writer.TryWrite(new
                {
                    @event = e.Event, hub = live.HubName, time = DateTimeOffset.UtcNow,
                    alert = new { rule = e.Alert.RuleName, source = e.SourceLabel, sourceKey = e.Alert.MicKey, kind = e.SourceKind, level = e.Alert.Level, metric = e.Alert.Metric.ToString(), value = e.Alert.Value, threshold = e.Alert.Threshold },
                });
            if (serviceEvent is not null && cfg.Webhooks.OnService)
                _webhooks.Writer.TryWrite(new
                {
                    @event = serviceEvent, hub = live.HubName, time = DateTimeOffset.UtcNow,
                    service = live.Service is { } s ? new { id = s.Id, name = s.Name, start = DateTimeOffset.FromUnixTimeSeconds(s.StartTs) } : (object)new { id = endedServiceId },
                });
        }

        // ----- OSC -----
        if (cfg.Osc.Enabled && cfg.Osc.Targets.Count > 0)
        {
            var msgs = new List<byte[]>();
            foreach (var e in events)
                msgs.Add(Osc.Message($"{cfg.Osc.Prefix}/alert", e.SourceLabel, e.Alert.Level, (float)e.Alert.Value, e.Alert.RuleName));
            if (DateTimeOffset.UtcNow - _lastOsc >= TimeSpan.FromSeconds(1 / cfg.Osc.RateHz))
            {
                msgs.AddRange(OscSender.LevelMessages(cfg.Osc.Prefix, snap));
                _lastOsc = DateTimeOffset.UtcNow;
            }
            if (msgs.Count > 0)
            {
                try { await _osc.SendAsync(cfg.Osc.Targets, msgs, ct); Ok("osc", $"Sending to {string.Join(", ", cfg.Osc.Targets.Select(t => $"{t.Host}:{t.Port}"))}"); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Fail("osc", ex.Message); }
            }
        }
        else _status.TryRemove("osc", out _);

        // ----- MIDI -----
        if (cfg.Midi.Enabled && !string.IsNullOrWhiteSpace(cfg.Midi.Device)) Midi(cfg.Midi, snap, live);
        else { CloseMidi(); _status.TryRemove("midi", out _); }

        // ----- ProPresenter (at most once a second, never overlapping) -----
        if (cfg.ProPresenter.Enabled)
        {
            if (!_proBusy && (DateTimeOffset.UtcNow - _lastPro >= TimeSpan.FromSeconds(1) || events.Count > 0))
            {
                _proBusy = true;
                _lastPro = DateTimeOffset.UtcNow;
                _ = Task.Run(async () =>
                {
                    try { await ProPresenterAsync(cfg.ProPresenter, snap, live, ct); }
                    finally { _proBusy = false; }
                }, ct);
            }
        }
        else if (_lastStage is not null || _lastMessage is not null)
        {
            _lastStage = _lastMessage = null;
            _status.TryRemove("propresenter", out _);
        }
    }

    private static AlertEvent Ev(string name, ActiveAlertView a, LevelsSnapshot snap)
    {
        var src = LevelsView.Find(snap, a.MicKey);
        return new AlertEvent(name, a, src?.Label ?? a.MicKey, src?.Kind ?? (a.MicKey.StartsWith(HubStore.ZoneNodeId + ":") ? "zone" : "mic"));
    }

    // ---------- MIDI ----------

    private void Midi(MidiSettings m, LevelsSnapshot snap, HubLive live)
    {
        try
        {
            if (_midi is null || _midiDevice != m.Device)
            {
                CloseMidi();
                _midi = MidiOutput.Open(m.Device!);
                _midiDevice = m.Device;
                _lastCc.Clear();
            }
            var refresh = DateTimeOffset.UtcNow - _lastCcRefresh > TimeSpan.FromSeconds(5);
            if (refresh) _lastCcRefresh = DateTimeOffset.UtcNow;
            foreach (var map in m.Mappings)
            {
                var v = LevelsView.Find(snap, map.Source)?.Values.GetValueOrDefault(LevelsView.MetricName(map.Metric));
                if (v is not double d) continue;
                var val = (int)Math.Round(Math.Clamp((d - map.MinDb) / (map.MaxDb - map.MinDb), 0, 1) * 127);
                if (refresh || _lastCc.GetValueOrDefault(map.Cc, -1) != val)
                {
                    _midi.ControlChange(m.Channel, map.Cc, val);
                    _lastCc[map.Cc] = val;
                }
            }
            var anyAlarm = live.Alerts.Any(a => a.Level == "alarm");
            var anyWarn = live.Alerts.Any(a => a.Level == "warn");
            SetNote(m, m.WarnNote, anyWarn);
            SetNote(m, m.AlarmNote, anyAlarm);
            Ok("midi", $"Sending to {m.Device} on channel {m.Channel}");
        }
        catch (Exception ex)
        {
            CloseMidi();
            Fail("midi", ex.Message);
        }
    }

    private void SetNote(MidiSettings m, int note, bool on)
    {
        if (note < 0 || _midi is null) return;
        if (on && _notesOn.Add(note)) _midi.NoteOn(m.Channel, note);
        else if (!on && _notesOn.Remove(note)) _midi.NoteOff(m.Channel, note);
    }

    private void CloseMidi()
    {
        try
        {
            if (_midi is not null)
                foreach (var n in _notesOn) _midi.NoteOff(settings.Current.Integrations.Midi.Channel, n);
        }
        catch { /* device gone */ }
        _notesOn.Clear();
        _midi?.Dispose();
        _midi = null;
        _midiDevice = null;
    }

    // ---------- ProPresenter ----------

    private async Task ProPresenterAsync(ProPresenterSettings p, LevelsSnapshot snap, HubLive live, CancellationToken ct)
    {
        try
        {
            // Alarm text takes over the stage message; otherwise the optional readout line.
            var shown = live.Alerts.Where(a => a.Level == "alarm" ? p.StageAlarms : p.StageWarnings)
                .OrderByDescending(a => a.Level == "alarm").ThenByDescending(a => a.Value - a.Threshold).FirstOrDefault();
            string? stage = null;
            if (shown is not null)
            {
                var src = LevelsView.Find(snap, shown.MicKey);
                var unit = shown.Metric.ToString().StartsWith("LA") ? "dBA" : "dBC";
                stage = $"{(shown.Level == "alarm" ? "LEVEL ALARM" : "Level warning")}: {src?.Label ?? shown.MicKey} {shown.Value:0} {unit} (limit {shown.Threshold:0})";
            }
            else if (p.StageReadout) stage = ProPresenterClient.ReadoutLine(snap, p);

            if (stage != _lastStage)
            {
                if (string.IsNullOrEmpty(stage)) await pro.ClearStageMessageAsync(p, ct);
                else await pro.SetStageMessageAsync(p, stage, ct);
                _lastStage = stage;
            }

            if (p.MessageReadout && !string.IsNullOrWhiteSpace(p.MessageId))
            {
                var text = ProPresenterClient.ReadoutLine(snap, p);
                if (text != _lastMessage)
                {
                    if (string.IsNullOrEmpty(text)) await pro.ClearMessageAsync(p, p.MessageId!, ct);
                    else await pro.TriggerMessageAsync(p, p.MessageId!, p.TokenName, text, ct);
                    _lastMessage = text;
                }
            }
            Ok("propresenter", string.IsNullOrEmpty(stage) ? "Connected (nothing to show)" : $"Stage: {stage}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _lastStage = _lastMessage = null; // resend once it's reachable again
            Fail("propresenter", $"Can't reach ProPresenter at {p.Host}:{p.Port} ({ex.Message}). Check Settings → Network → Enable Network in ProPresenter.");
        }
    }

    // ---------- webhooks ----------

    private async Task WebhookWorker(CancellationToken ct)
    {
        var client = http.CreateClient("webhooks");
        await foreach (var payload in _webhooks.Reader.ReadAllAsync(ct))
        {
            foreach (var url in settings.Current.Integrations.Webhooks.Urls)
            {
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        using var res = await client.PostAsync(url, ProPresenterClient.JsonBody(JsonSerializer.Serialize(payload, ProPresenterClient.Json)), ct);
                        if (res.IsSuccessStatusCode) { Ok("webhooks", $"Last delivered to {new Uri(url).Host}"); break; }
                        throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (attempt == 3) Fail("webhooks", $"{new Uri(url).Host}: {ex.Message}");
                        else await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
                    }
                }
            }
        }
    }

    // ---------- tests from the UI ----------

    public async Task<string> TestAsync(string kind, CancellationToken ct)
    {
        var cfg = settings.Current.Integrations;
        switch (kind)
        {
            case "osc":
                await _osc.SendAsync(cfg.Osc.Targets, [Osc.Message($"{cfg.Osc.Prefix}/test", 1)], ct);
                return $"Sent {cfg.Osc.Prefix}/test 1 to {string.Join(", ", cfg.Osc.Targets.Select(t => $"{t.Host}:{t.Port}"))}.";
            case "midi":
            {
                if (string.IsNullOrWhiteSpace(cfg.Midi.Device)) throw new ArgumentException("Choose a MIDI output first.");
                using var m = cfg.Midi.Device == _midiDevice ? null : MidiOutput.Open(cfg.Midi.Device);
                var o = m ?? _midi!;
                var note = cfg.Midi.WarnNote >= 0 ? cfg.Midi.WarnNote : 60;
                o.NoteOn(cfg.Midi.Channel, note, 100);
                await Task.Delay(300, ct);
                o.NoteOff(cfg.Midi.Channel, note);
                return $"Sent note {note} on channel {cfg.Midi.Channel} to {cfg.Midi.Device}.";
            }
            case "webhook":
            {
                if (cfg.Webhooks.Urls.Count == 0) throw new ArgumentException("Add a webhook URL first.");
                var client = http.CreateClient("webhooks");
                var results = new List<string>();
                foreach (var url in cfg.Webhooks.Urls)
                {
                    using var res = await client.PostAsync(url, ProPresenterClient.JsonBody(JsonSerializer.Serialize(new { @event = "test", hub = settings.Current.Name, time = DateTimeOffset.UtcNow }, ProPresenterClient.Json)), ct);
                    results.Add($"{new Uri(url).Host}: {(int)res.StatusCode} {res.ReasonPhrase}");
                }
                return string.Join("; ", results);
            }
            case "propresenter":
            {
                var version = await pro.VersionAsync(cfg.ProPresenter, ct);
                await pro.SetStageMessageAsync(cfg.ProPresenter, "RTA test message", ct);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(4000);
                    try { await pro.ClearStageMessageAsync(cfg.ProPresenter, CancellationToken.None); } catch { /* ignore */ }
                    _lastStage = null;
                });
                return $"Connected: {version}. A test stage message is showing for 4 seconds.";
            }
            default: throw new ArgumentException("Unknown integration.");
        }
    }
}
