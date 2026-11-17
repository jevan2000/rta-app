using System.Collections.Concurrent;
using System.Globalization;
using System.Security;
using Rta.App.Hub;
using Rta.App.Hub.Integrations;
using Rta.App.Node;
using Rta.Core.Calibration;
using SkiaSharp;

namespace Rta.App.Ndi;

public sealed record NdiSenderStatus(string Name, string Kind, int Connections, double EstimatedMbps);

public sealed record NdiStatus(bool RuntimeAvailable, string RuntimeMessage, IReadOnlyList<NdiSenderStatus> Senders, double EstimatedMbps, IReadOnlyList<string> Warnings);

/// <summary>Rough NDI bandwidth so we can warn before a network gets swamped.</summary>
public static class NdiBandwidth
{
    /// <summary>NDI High Bandwidth ≈ 125 Mb/s for 1080p60, scaled by pixels × fps (an upper estimate for mostly static graphics).</summary>
    public static double Video(int w, int h, double fps) => 125.0 * (w * (double)h / (1920 * 1080)) * (fps / 60);
    /// <summary>NDI audio is uncompressed 32-bit float.</summary>
    public static double Audio(int channels, double rate) => channels * rate * 32 / 1e6;

    public static List<string> Warnings(IReadOnlyCollection<NdiSenderStatus> senders, double total)
    {
        var w = new List<string>();
        if (total > 100) w.Add($"About {total:0} Mb/s of NDI from this PC. On a busy or 100 Mb/s network, reduce feeds or frame rates.");
        else if (total > 40) w.Add($"About {total:0} Mb/s of NDI from this PC. Fine on gigabit, but watch shared or Wi-Fi links.");
        if (senders.Count > 8) w.Add($"{senders.Count} NDI sources from one PC. More than about 8 adds CPU load and clutters source lists.");
        return w;
    }
}

/// <summary>
/// Node side: per input, an NDI audio source ("RTA &lt;input&gt; audio": raw, unweighted, as captured)
/// and/or a meter source ("RTA &lt;input&gt; meter": rendered video plus XML level metadata).
/// NDI adds the PC name in front, e.g. "BOOTH-PC (RTA Mic 1 meter)".
/// </summary>
public sealed class NodeNdiService(MeteringService metering, ILogger<NodeNdiService> log) : BackgroundService
{
    private sealed class AudioOut(NdiSender sender, int channel)
    {
        public NdiSender Sender { get; } = sender;
        public int Channel { get; } = channel;
        public float[] Buffer = [];
    }

    private readonly ConcurrentDictionary<int, AudioOut> _audio = new();
    private readonly Dictionary<int, NdiSender> _meters = [];
    private readonly Dictionary<int, SKBitmap?> _bitmaps = [];
    private string? _error;

    public NdiStatus Status()
    {
        var list = new List<NdiSenderStatus>();
        var rate = metering.Status().SampleRate;
        foreach (var a in _audio.Values) list.Add(new(a.Sender.Name, "audio", a.Sender.Connections, NdiBandwidth.Audio(1, rate)));
        lock (_meters)
            foreach (var m in _meters.Values) list.Add(new(m.Name, "meter", m.Connections, NdiBandwidth.Video(640, 360, metering.Config.NdiMeterFps)));
        var total = list.Sum(s => s.EstimatedMbps);
        var warnings = NdiBandwidth.Warnings(list, total);
        if (_error is not null) warnings.Insert(0, _error);
        return new NdiStatus(NdiRuntime.Available, NdiRuntime.Message, list, Math.Round(total, 1), warnings);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        metering.RawAudio += OnRawAudio;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var fps = Math.Clamp(metering.Config.NdiMeterFps, 1, 30);
                try { Tick(fps); }
                catch (Exception ex) { _error = "NDI: " + ex.Message; log.LogWarning(ex, "NDI tick failed"); }
                try { await Task.Delay(1000 / fps, ct); } catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            metering.RawAudio -= OnRawAudio;
            foreach (var a in _audio.Values) a.Sender.Dispose();
            lock (_meters) foreach (var m in _meters.Values) m.Dispose();
            foreach (var b in _bitmaps.Values) b?.Dispose();
        }
    }

    private void Tick(int fps)
    {
        var cfg = metering.Config;
        var wantAudio = cfg.Inputs.Where(i => i.Enabled && i.NdiAudio).ToDictionary(i => i.ChannelIndex);
        var wantMeter = cfg.Inputs.Where(i => i.Enabled && i.NdiMeter).ToDictionary(i => i.ChannelIndex);
        if ((wantAudio.Count > 0 || wantMeter.Count > 0) && !NdiRuntime.Available) { _error = NdiRuntime.Message; return; }
        _error = null;

        // Reconcile audio senders (names follow the input name).
        foreach (var (ch, a) in _audio)
            if (!wantAudio.TryGetValue(ch, out var ic) || a.Sender.Name != AudioName(ic)) { _audio.TryRemove(ch, out _); a.Sender.Dispose(); }
        foreach (var (ch, ic) in wantAudio)
            if (!_audio.ContainsKey(ch)) _audio[ch] = new AudioOut(new NdiSender(AudioName(ic)), ch);

        // Meters
        lock (_meters)
        {
            foreach (var ch in _meters.Keys.ToList())
                if (!wantMeter.TryGetValue(ch, out var ic) || _meters[ch].Name != MeterName(ic)) { _meters[ch].Dispose(); _meters.Remove(ch); }
            foreach (var (ch, ic) in wantMeter)
                if (!_meters.ContainsKey(ch)) _meters[ch] = new NdiSender(MeterName(ic));
        }
        if (wantMeter.Count == 0) return;

        var th = cfg.Thresholds;
        var channels = metering.Channels().ToDictionary(c => c.ChannelIndex);
        lock (_meters)
            foreach (var (ch, sender) in _meters)
            {
                channels.TryGetValue(ch, out var st);
                var L = st?.Levels;
                var rough = st?.ProfileQuality is CalibrationQuality.Low or CalibrationQuality.VeryLow;
                var data = new TileData(st?.Name ?? $"Input {ch + 1}", st?.MicLabel ?? cfg.NodeName,
                    L is null ? "" : !L.Calibrated ? "UNCAL dBFS" : rough ? "≈ unreliable cal" : "calibrated",
                    L is null ? TileTone.Muted : !L.Calibrated || rough ? TileTone.Warn : TileTone.Ok,
                    L?.LCS, L?.LAS, L is { Coverage1mSeconds: >= 5 } ? L.LCeq1m : null, L?.LCeq15m, L?.LCeqSession,
                    L?.Bands, th.WarnC, th.AlarmC,
                    L is null ? null : L.LCS >= th.AlarmC ? "alarm" : L.LCS >= th.WarnC ? "warn" : null,
                    metering.IsRunning && L is not null, L?.Calibrated ?? false);
                var bmp = _bitmaps.GetValueOrDefault(ch);
                TileRenderer.Ensure(ref bmp, 640, 360);
                _bitmaps[ch] = bmp;
                TileRenderer.DrawTileFrame(bmp!, data);
                sender.SendBgra(bmp!.GetPixelSpan(), bmp.Width, bmp.Height, bmp.RowBytes, fps);
                if (L is not null) sender.SendMetadata(LevelsXml(st!.Name, L.Calibrated, L.LCS, L.LAS, L.LCeq1m, L.LAeq1m, L.LCeq15m, L.LCeqSession));
            }
    }

    private void OnRawAudio(float[] interleaved, int stride, double rate)
    {
        if (_audio.IsEmpty) return;
        var frames = interleaved.Length / stride;
        foreach (var a in _audio.Values)
        {
            if (a.Channel >= stride) continue;
            if (a.Buffer.Length != frames) a.Buffer = new float[frames];
            for (var i = 0; i < frames; i++) a.Buffer[i] = interleaved[i * stride + a.Channel];
            try { a.Sender.SendAudio(a.Buffer, 1, frames, (int)rate); }
            catch (Exception ex) { _error = "NDI audio: " + ex.Message; }
        }
    }

    private static string AudioName(InputConfig ic) => $"RTA {ic.Name} audio";
    private static string MeterName(InputConfig ic) => $"RTA {ic.Name} meter";

    public static string LevelsXml(string name, bool calibrated, params double?[] v)
    {
        string A(double? x) => x is double d && d > -150 ? d.ToString("0.0", CultureInfo.InvariantCulture) : "";
        return $"<rta_levels name=\"{SecurityElement.Escape(name)}\" calibrated=\"{(calibrated ? "true" : "false")}\" lcs=\"{A(v[0])}\" las=\"{A(v[1])}\" lceq1m=\"{A(v[2])}\" laeq1m=\"{A(v[3])}\" lceq15m=\"{A(v[4])}\" lceqservice=\"{A(v[5])}\"/>";
    }
}

public sealed record HubNdiSettings
{
    public bool Dashboard { get; init; }
    public int DashboardWidth { get; init; } = 1920;
    public int DashboardHeight { get; init; } = 1080;
    public int DashboardFps { get; init; } = 5;
    /// <summary>Include zones on the dashboard as well as mics.</summary>
    public bool DashboardZones { get; init; } = true;
    /// <summary>One NDI source per (surveyed) zone.</summary>
    public bool ZoneFeeds { get; init; }
    public int ZoneFps { get; init; } = 10;

    public static void Validate(HubNdiSettings s)
    {
        if (s.DashboardWidth is < 320 or > 3840 || s.DashboardHeight is < 180 or > 2160) throw new ArgumentException("Dashboard size must be between 320×180 and 3840×2160.");
        if (s.DashboardFps is < 1 or > 30 || s.ZoneFps is < 1 or > 30) throw new ArgumentException("Frame rates must be 1–30.");
    }
}

/// <summary>Hub side: "RTA Dashboard" (all mics and zones on one frame) and optional "RTA Zone &lt;name&gt;" feeds.</summary>
public sealed class HubNdiService(HubState state, HubSettingsStore settings, ILogger<HubNdiService> log) : BackgroundService
{
    private NdiSender? _dashboard;
    private SKBitmap? _dashBmp;
    private readonly Dictionary<int, (NdiSender Sender, SKBitmap? Bmp)> _zones = [];
    private DateTimeOffset _nextDash = DateTimeOffset.MinValue, _nextZones = DateTimeOffset.MinValue;
    private string? _error;

    public NdiStatus Status()
    {
        var s = settings.Current.Ndi;
        var list = new List<NdiSenderStatus>();
        lock (_zones)
        {
            if (_dashboard is { } d) list.Add(new(d.Name, "dashboard", d.Connections, NdiBandwidth.Video(s.DashboardWidth, s.DashboardHeight, s.DashboardFps)));
            foreach (var (z, _) in _zones.Values) list.Add(new(z.Name, "zone", z.Connections, NdiBandwidth.Video(640, 360, s.ZoneFps)));
        }
        var total = list.Sum(x => x.EstimatedMbps);
        var w = NdiBandwidth.Warnings(list, total);
        if (_error is not null) w.Insert(0, _error);
        return new NdiStatus(NdiRuntime.Available, NdiRuntime.Message, list, Math.Round(total, 1), w);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception ex) { _error = "NDI: " + ex.Message; log.LogWarning(ex, "Hub NDI tick failed"); }
                try { await Task.Delay(50, ct); } catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            lock (_zones)
            {
                _dashboard?.Dispose();
                foreach (var (s, b) in _zones.Values) { s.Dispose(); b?.Dispose(); }
            }
            _dashBmp?.Dispose();
        }
    }

    private void Tick()
    {
        var s = settings.Current.Ndi;
        if ((s.Dashboard || s.ZoneFeeds) && !NdiRuntime.Available) { _error = NdiRuntime.Message; return; }
        _error = null;
        var now = DateTimeOffset.UtcNow;
        var rules = settings.Current.AlertRules;

        lock (_zones)
        {
            if (!s.Dashboard && _dashboard is not null) { _dashboard.Dispose(); _dashboard = null; }
            if (s.Dashboard && _dashboard is null) _dashboard = new NdiSender("RTA Dashboard");
        }

        HubLive? live = null;
        if (_dashboard is not null && now >= _nextDash)
        {
            _nextDash = now.AddSeconds(1.0 / s.DashboardFps);
            live = state.Snapshot();
            var tiles = live.Mics.Where(m => !m.Hidden).Select(m => Tile(m, live, rules, false)).ToList();
            if (s.DashboardZones) tiles.AddRange(live.Zones.Where(z => z.Surveyed).Select(z => Tile(HubState.ZoneAsMic(z), live, rules, true)));
            var labels = live.Mics.ToDictionary(m => m.Key, m => m.Label);
            foreach (var z in live.Zones) labels[z.Key] = z.Name;
            var alerts = live.Alerts.Select(a => $"{(a.Level == "alarm" ? "ALARM" : "Warning")}: {labels.GetValueOrDefault(a.MicKey, a.MicKey)} {a.Value:0} (limit {a.Threshold:0})").ToList();
            TileRenderer.Ensure(ref _dashBmp, s.DashboardWidth, s.DashboardHeight);
            TileRenderer.DrawDashboard(_dashBmp!, new DashboardData(live.HubName, live.Service?.Name,
                live.Service is { } sv ? DateTimeOffset.FromUnixTimeSeconds(sv.StartTs) : null, tiles, alerts));
            _dashboard.SendBgra(_dashBmp!.GetPixelSpan(), _dashBmp.Width, _dashBmp.Height, _dashBmp.RowBytes, s.DashboardFps);
        }

        lock (_zones)
        {
            var want = s.ZoneFeeds ? (live ?? state.Snapshot()).Zones.Where(z => z.Surveyed).ToDictionary(z => z.Id) : [];
            foreach (var id in _zones.Keys.ToList())
                if (!want.TryGetValue(id, out var z) || _zones[id].Sender.Name != $"RTA Zone {z.Name}") { _zones[id].Sender.Dispose(); _zones[id].Bmp?.Dispose(); _zones.Remove(id); }
            foreach (var (id, z) in want)
                if (!_zones.ContainsKey(id)) _zones[id] = (new NdiSender($"RTA Zone {z.Name}"), null);
            if (_zones.Count == 0 || now < _nextZones) return;
            _nextZones = now.AddSeconds(1.0 / s.ZoneFps);
            live ??= state.Snapshot();
            foreach (var id in _zones.Keys.ToList())
            {
                var z = want[id];
                var (sender, bmp) = _zones[id];
                TileRenderer.Ensure(ref bmp, 640, 360);
                _zones[id] = (sender, bmp);
                var m = HubState.ZoneAsMic(z);
                TileRenderer.DrawTileFrame(bmp!, Tile(m, live, rules, true));
                sender.SendBgra(bmp!.GetPixelSpan(), bmp.Width, bmp.Height, bmp.RowBytes, s.ZoneFps);
                sender.SendMetadata(NodeNdiService.LevelsXml(z.Name, true, z.LC1s, z.LA1s, z.LCeq1m, z.LAeq1m, z.LCeq15m, z.LCeqService));
            }
        }
    }

    private static TileData Tile(MicLiveView m, HubLive live, IReadOnlyList<AlertRule> rules, bool zone)
    {
        var rule = rules.FirstOrDefault(r => r.Enabled && r.Metric is AlertMetric.LCeq1m or AlertMetric.LCS
            && (r.Target == m.Key || r.Target == (zone ? "zones:*" : "*"))) ?? new AlertRule();
        var alert = live.Alerts.Where(a => a.MicKey == m.Key).Select(a => a.Level).OrderByDescending(l => l == "alarm").FirstOrDefault();
        var rough = m.Quality is CalibrationQuality.Low or CalibrationQuality.VeryLow;
        var L = m.Live;
        static double? V(double? v) => v is double d && d > -150 ? d : null;
        var zv = zone ? live.Zones.FirstOrDefault(z => z.Key == m.Key) : null;
        return new TileData(m.Label, zone ? $"Zone · predicted{(zv is not null ? $" · {zv.Confidence:P0} conf." : "")}" : m.NodeName,
            zone ? "zone" : !m.Calibrated ? "UNCAL dBFS" : rough ? "≈ unreliable cal" : "calibrated",
            zone ? TileTone.Ok : !m.Calibrated || rough ? TileTone.Warn : TileTone.Ok,
            V(L?.LCS), V(L?.LAS), L is { Coverage1m: >= 5 } ? V(L.LCeq1m) : null, V(L?.LCeq15m), m.LCeqService,
            L?.Bands is { Length: > 0 } b ? b : null, rule.Warn, rule.Alarm, alert, m.Online, m.Calibrated);
    }
}
