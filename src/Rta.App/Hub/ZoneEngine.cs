using Rta.Core.Dsp;
using Rta.Core.Logging;
using Rta.Core.Metering;
using Rta.Core.Protocol;
using Rta.Core.Zones;

namespace Rta.App.Hub;

public sealed record ZoneLiveView(
    int Id, string Key, string Name, string FloorId, ZoneSurveyMode Mode,
    bool Surveyed, int Surveys, double Confidence, bool Stale, bool BandMode, IReadOnlyList<string> Issues,
    double? LA1s, double? LC1s, double? LAeq1m, double? LCeq1m, double? LAeq15m, double? LCeq15m,
    double? LAeqService, double? LCeqService, double[]? Bands, int RefsUsed, int RefsInModel);

public sealed record SurveyRefStatus(string MicKey, string Label, double? Offset, double? ChunkStd, int Chunks);

public sealed record SurveyStatus(
    int ZoneId, string ZoneName, string RoverKey, string RoverLabel, long StartTs, double Seconds, double MinSeconds,
    double Confidence, double? RoverLC, double? RoverLA, IReadOnlyList<SurveyRefStatus> Refs, IReadOnlyList<string> Warnings);

/// <summary>
/// Hub-side zone prediction. Keeps 15 minutes of gap-free interval levels per mic, fits zone models
/// from stored surveys, predicts each zone live (1 s, 1 min, 15 min), logs predicted zones every
/// second and minute as virtual mics, and runs survey sessions.
/// </summary>
public sealed class ZoneEngine : BackgroundService
{
    private sealed record Interval(double T, double Dur, double[] Bands, double A, double C, double Z);

    private readonly HubStore _store;
    private readonly HubState _state;
    private readonly PlanStore _plan;
    private readonly ILogger<ZoneEngine> _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedList<Interval>> _buffers = [];
    private Dictionary<int, (ZoneModelData Model, List<SurveyRow> Surveys)>? _models;
    private IReadOnlyList<ZoneLiveView> _views = [];
    private DateTimeOffset _viewsAt = DateTimeOffset.MinValue;

    private sealed class ActiveSurvey
    {
        public required int ZoneId;
        public required string RoverKey;
        public required long StartTs;
        public required double MinSeconds;
        public required SurveyAccumulator Acc;
    }
    private ActiveSurvey? _survey;

    private sealed class MinuteAcc { public long Key = -1; public double A, C, Z, Dur; public double[]? Bands; }
    private readonly Dictionary<int, MinuteAcc> _minutes = [];

    public static readonly TimeSpan BufferSpan = TimeSpan.FromMinutes(15);

    public ZoneEngine(HubStore store, HubState state, PlanStore plan, ILogger<ZoneEngine> log)
    {
        _store = store; _state = state; _plan = plan; _log = log;
        _state.ZoneProvider = Views;
        _state.SurveyProvider = SurveyStatus;
        _plan.Changed += () => { InvalidateModels(); _store.EnsureZoneMics(_plan.Current.AllZones); _state.InvalidateMics(); };
        _store.EnsureZoneMics(_plan.Current.AllZones);
    }

    private static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public IReadOnlyList<double> BandsHz =>
        _state.Connections.Values.Select(c => c.Hello?.BandsHz).FirstOrDefault(b => b is { Count: > 0 }) ?? ThirdOctaveBank.NominalCenters;

    // ---------- ingest ----------

    public void OnLive(string nodeId, LiveUpdate live)
    {
        var now = Now();
        foreach (var m in live.Mics)
        {
            if (m.Interval is not { Seconds: > 0 } iv) continue;
            var key = HubStore.MicKey(nodeId, m.Ch);
            var item = new Interval(now, iv.Seconds, iv.Bands.Select(Db.ToMeanSquare).ToArray(),
                Db.ToMeanSquare(iv.LA), Db.ToMeanSquare(iv.LC), Db.ToMeanSquare(iv.LZ));
            lock (_lock)
            {
                if (!_buffers.TryGetValue(key, out var buf)) _buffers[key] = buf = new LinkedList<Interval>();
                buf.AddLast(item);
                while (buf.First is { } f && now - f.Value.T > BufferSpan.TotalSeconds) buf.RemoveFirst();
                _survey?.Acc.Add(key, iv, now);
            }
        }
    }

    /// <summary>Energy-averaged reading of one mic over (from, to] in hub time, or null if it has no data there.</summary>
    private RefReading? Reading(string micKey, double from, double to)
    {
        double a = 0, c = 0, d = 0;
        double[]? bands = null;
        lock (_lock)
        {
            if (!_buffers.TryGetValue(micKey, out var buf)) return null;
            for (var n = buf.Last; n is not null && n.Value.T > from; n = n.Previous)
            {
                var iv = n.Value;
                if (iv.T > to) continue;
                a += iv.A * iv.Dur; c += iv.C * iv.Dur; d += iv.Dur;
                bands ??= new double[iv.Bands.Length];
                for (var i = 0; i < bands.Length && i < iv.Bands.Length; i++) bands[i] += iv.Bands[i] * iv.Dur;
            }
        }
        if (d < Math.Min((to - from) * 0.5, 30)) return null; // too little data to trust (long windows fill up over time)
        return new RefReading(bands!.Select(e => Db.FromMeanSquare(e / d)).ToArray(), Db.FromMeanSquare(a / d), Db.FromMeanSquare(c / d), CurrentOffset(micKey));
    }

    /// <summary>True if the mic has sent interval data in the last 3 seconds.</summary>
    private bool IsLive(string micKey)
    {
        lock (_lock) return _buffers.TryGetValue(micKey, out var buf) && buf.Last is { } last && Now() - last.Value.T < 3;
    }

    private double CurrentOffset(string micKey) => CurrentInfo(micKey)?.OffsetDb ?? 0;

    private MicInfo? CurrentInfo(string micKey)
    {
        var idx = micKey.LastIndexOf(':');
        if (idx < 0 || !int.TryParse(micKey[(idx + 1)..], out var ch)) return null;
        return _state.Connections.TryGetValue(micKey[..idx], out var conn) ? conn.Hello?.Mics.FirstOrDefault(m => m.ChannelIndex == ch) : null;
    }

    // ---------- models ----------

    public void InvalidateModels()
    {
        lock (_lock) _models = null;
        _viewsAt = DateTimeOffset.MinValue;
    }

    private Dictionary<int, (ZoneModelData Model, List<SurveyRow> Surveys)> Models()
    {
        lock (_lock) if (_models is not null) return _models;
        var plan = _plan.Current;
        var all = _store.ListSurveys().Where(s => !s.Excluded).GroupBy(s => s.ZoneId).ToDictionary(g => g.Key, g => g.ToList());
        var models = new Dictionary<int, (ZoneModelData, List<SurveyRow>)>();
        foreach (var z in plan.AllZones)
        {
            if (!all.TryGetValue(z.Id, out var list) || list.Count == 0) continue;
            var used = z.Mode == ZoneSurveyMode.Replace ? [list.OrderBy(s => s.StartTs).Last()] : list;
            try { models[z.Id] = (ZoneModel.Build(used.Select(s => (s.Measurement, Math.Max(0.05, s.Confidence))).ToList(), BandsHz), used); }
            catch (Exception ex) { _log.LogWarning(ex, "Zone {Zone} model failed", z.Name); }
        }
        lock (_lock) _models = models;
        return models;
    }

    private ZonePrediction? PredictWindow(ZoneModelData model, double from, double to)
    {
        var readings = new Dictionary<string, RefReading>();
        foreach (var r in model.Refs)
            if (Reading(r.MicKey, from, to) is { } rd) readings[r.MicKey] = rd;
        return ZoneModel.Predict(model, readings, BandsHz);
    }

    // ---------- live views ----------

    public IReadOnlyList<ZoneLiveView> Views()
    {
        if (DateTimeOffset.UtcNow - _viewsAt < TimeSpan.FromMilliseconds(400)) return _views;
        var plan = _plan.Current;
        var models = Models();
        var now = Now();
        var list = new List<ZoneLiveView>();
        foreach (var f in plan.Floors)
            foreach (var z in f.Zones)
            {
                var key = HubStore.ZoneKey(z.Id);
                var (sa, sc, _) = _state.ServiceLeq(key);
                if (!models.TryGetValue(z.Id, out var m))
                {
                    list.Add(new ZoneLiveView(z.Id, key, z.Name, f.Id, z.Mode, false, 0, 0, false, false, ["Not surveyed yet."],
                        null, null, null, null, null, null, null, null, null, 0, 0));
                    continue;
                }
                var issues = new List<string>();
                var stale = false;
                var latest = m.Surveys.OrderBy(s => s.StartTs).Last();
                foreach (var r in m.Model.Refs)
                {
                    var label = MicLabel(r.MicKey);
                    var placed = plan.Mic(r.MicKey);
                    if (placed is null) { issues.Add($"{label} is no longer on the plan. Resurvey."); stale = true; }
                    else if (latest.RefPositions.TryGetValue(r.MicKey, out var was) && double.Hypot(was.X - placed.X, was.Y - placed.Y) > 0.5)
                    { issues.Add($"{label} was moved on the plan since the survey. Resurvey."); stale = true; }
                    var info = CurrentInfo(r.MicKey);
                    if (info is not null && r.ProfileIdAtSurvey is not null && info.ProfileId != r.ProfileIdAtSurvey)
                        issues.Add($"{label} was recalibrated since the survey (compensated automatically).");
                }

                var now1 = PredictWindow(m.Model, now - 1.2, now);
                var p1m = PredictWindow(m.Model, now - 60, now);
                var p15 = PredictWindow(m.Model, now - 900, now);
                var used = now1?.RefsUsed ?? 0;
                if (used < m.Model.Refs.Count)
                    issues.Add(used == 0 ? "No reference mics are online, so there's no prediction." : $"Only {used} of {m.Model.Refs.Count} reference mics online. Prediction is less certain.");
                var conf = m.Model.Confidence * (stale ? 0.5 : 1) * (m.Model.Refs.Count > 0 ? (double)used / m.Model.Refs.Count : 0);
                if (m.Model.Confidence < 0.5) issues.Add("Low survey confidence. Survey longer or with steadier sound.");
                if (!m.Model.BandMode) issues.Add("Based on a manual (single-number) entry: less accurate when the mix changes.");

                static double? R(double? v) => v is double d && d > Db.FloorDb + 1 ? Math.Round(d, 1) : null;
                list.Add(new ZoneLiveView(z.Id, key, z.Name, f.Id, z.Mode, true, m.Surveys.Count, Math.Round(conf, 2), stale, m.Model.BandMode, issues,
                    R(now1?.LA), R(now1?.LC), R(p1m?.LA), R(p1m?.LC), R(p15?.LA), R(p15?.LC), sa, sc,
                    now1?.Bands?.Select(b => Math.Round(b, 1)).ToArray(), used, m.Model.Refs.Count));
            }
        _views = list;
        _viewsAt = DateTimeOffset.UtcNow;
        return list;
    }

    private string MicLabel(string key) =>
        _store.ListMics().FirstOrDefault(m => m.Key == key)?.Label ?? key;

    // ---------- logging ----------

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Run 1.3 s after each second ends so late intervals have arrived.
            var now = Now();
            var next = Math.Floor(now) + 1.3;
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(0.05, next - now)), ct); } catch (OperationCanceledException) { return; }
            try { LogSecond((long)Math.Floor(next - 1.3) - 1); }
            catch (Exception ex) { _log.LogWarning(ex, "Zone logging failed"); }
        }
    }

    /// <summary>Predicts and stores second <paramref name="sec"/> (arrivals in (sec, sec+1]) for every surveyed zone.</summary>
    public void LogSecond(long sec)
    {
        var models = Models();
        if (models.Count == 0) return;
        var items = new List<SeqRecord>();
        foreach (var (zoneId, (model, _)) in models)
        {
            var p = PredictWindow(model, sec, sec + 1);
            if (p is null) continue;
            var lz = p.Bands is not null ? ZoneModel.BandSum(p.Bands, BandsHz, Weighting.Z) : p.LC;
            items.Add(new SeqRecord(0, new LevelRecord
            {
                Kind = RecordKind.Second, ChannelIndex = zoneId, Ts = sec, Coverage = 1,
                LAeq = Math.Round(p.LA, 2), LCeq = Math.Round(p.LC, 2), LZeq = Math.Round(lz, 2),
                LCpeak = Db.FloorDb, LASmax = Math.Round(p.LA, 2), LCSmax = Math.Round(p.LC, 2), Calibrated = true,
            }));

            if (!_minutes.TryGetValue(zoneId, out var acc)) _minutes[zoneId] = acc = new MinuteAcc();
            var minute = sec - sec % 60;
            if (acc.Key != minute)
            {
                if (acc.Key >= 0 && acc.Dur > 0) items.Add(new SeqRecord(0, MinuteRecord(zoneId, acc)));
                acc.Key = minute; acc.A = acc.C = acc.Z = acc.Dur = 0; acc.Bands = null;
            }
            acc.A += Db.ToMeanSquare(p.LA); acc.C += Db.ToMeanSquare(p.LC); acc.Z += Db.ToMeanSquare(lz); acc.Dur += 1;
            if (p.Bands is not null)
            {
                acc.Bands ??= new double[p.Bands.Length];
                for (var i = 0; i < acc.Bands.Length && i < p.Bands.Length; i++) acc.Bands[i] += Db.ToMeanSquare(p.Bands[i]);
            }
        }
        if (items.Count == 0) return;
        _store.StoreRecords(HubStore.ZoneNodeId, items);
        _state.OnRecordsStored(HubStore.ZoneNodeId, items);
    }

    private static LevelRecord MinuteRecord(int zoneId, MinuteAcc a)
    {
        double L(double e) => Math.Round(Db.FromMeanSquare(e / a.Dur), 2);
        return new LevelRecord
        {
            Kind = RecordKind.Minute, ChannelIndex = zoneId, Ts = a.Key, Coverage = a.Dur,
            LAeq = L(a.A), LCeq = L(a.C), LZeq = L(a.Z), LCpeak = Db.FloorDb, LASmax = L(a.A), LCSmax = L(a.C), Calibrated = true,
            Bands = a.Bands?.Select(L).ToArray(),
        };
    }

    // ---------- surveys ----------

    public SurveyStatus StartSurvey(int zoneId, string roverKey, double minSeconds)
    {
        var plan = _plan.Current;
        var zone = plan.Zone(zoneId) ?? throw new ArgumentException("That zone doesn't exist. Save the plan first.");
        if (!IsLive(roverKey)) throw new ArgumentException("The roving mic isn't sending live data. Check its node is online and accepted.");
        var refs = plan.AllMics.Where(x => x.Mic.Role == MicRole.Permanent && x.Mic.MicKey != roverKey).Select(x => x.Mic.MicKey).ToList();
        if (refs.Count == 0) throw new ArgumentException("Place at least one permanent mic on the plan first (Plan editor → Mics).");
        if (!refs.Any(IsLive)) throw new ArgumentException("None of the permanent mics on the plan are online.");
        lock (_lock)
        {
            if (_survey is not null) throw new InvalidOperationException("A survey is already running. Stop it first.");
            var start = Now();
            _survey = new ActiveSurvey
            {
                ZoneId = zoneId, RoverKey = roverKey, StartTs = (long)start, MinSeconds = Math.Clamp(minSeconds, 10, 1800),
                Acc = new SurveyAccumulator(roverKey, refs, BandsHz.Count, start),
            };
        }
        _log.LogInformation("Survey of zone {Zone} started with rover {Rover}", zone.Name, roverKey);
        return SurveyStatus()!;
    }

    public SurveyStatus? SurveyStatus()
    {
        ActiveSurvey? s;
        lock (_lock) s = _survey;
        if (s is null) return null;
        var m = s.Acc.Measure(s.MinSeconds, BandsHz);
        var warnings = new List<string>();
        var roverInfo = CurrentInfo(s.RoverKey);
        if (roverInfo is { Calibrated: false }) warnings.Add("The roving mic is uncalibrated, so zone predictions will be in its dBFS scale. Calibrate it first.");
        if (roverInfo?.Quality is Rta.Core.Calibration.CalibrationQuality.Low or Rta.Core.Calibration.CalibrationQuality.VeryLow)
            warnings.Add("The roving mic has an unreliable calibration, so zone levels will be approximate.");
        var missing = s.Acc.RefKeys.Except(s.Acc.RefsWithData).ToList();
        if (missing.Count > 0) warnings.Add($"No data from {string.Join(", ", missing.Select(MicLabel))}.");
        if (m is not null && m.Seconds < s.MinSeconds) warnings.Add($"Keep going: at least {s.MinSeconds:0} s is needed.");
        return new SurveyStatus(s.ZoneId, _plan.Current.Zone(s.ZoneId)?.Name ?? "?", s.RoverKey, MicLabel(s.RoverKey), s.StartTs,
            Math.Round(m?.Seconds ?? 0, 1), s.MinSeconds, m?.Confidence ?? 0,
            m is null ? null : Math.Round(m.RoverLC, 1), m is null ? null : Math.Round(m.RoverLA, 1),
            (m?.Refs ?? []).Select(r => new SurveyRefStatus(r.MicKey, MicLabel(r.MicKey), Math.Round(m!.RoverLC - r.LC, 1), r.ChunkStdC, r.Chunks)).ToList(),
            warnings);
    }

    public SurveyRow? StopSurvey(bool save, string? note)
    {
        ActiveSurvey? s;
        lock (_lock) { s = _survey; _survey = null; }
        if (s is null) throw new InvalidOperationException("No survey is running.");
        if (!save) return null;
        var m = s.Acc.Measure(s.MinSeconds, BandsHz) ?? throw new InvalidOperationException("The roving mic sent no data; nothing to save.");
        if (m.Refs.Count == 0) throw new InvalidOperationException("No reference mic had enough data during the survey; nothing to save.");
        m = m with { Refs = m.Refs.Select(r => r with { OffsetDbAtSurvey = CurrentOffset(r.MicKey), ProfileIdAtSurvey = CurrentInfo(r.MicKey)?.ProfileId }).ToList() };
        var row = _store.AddSurvey(s.ZoneId, "measured", s.StartTs, (long)Now(), s.RoverKey, note, m, RefPositions(m.Refs.Select(r => r.MicKey)));
        InvalidateModels();
        _log.LogInformation("Survey {Id} of zone {Zone} saved: {Seconds:0} s, confidence {Conf:0.00}", row.Id, s.ZoneId, m.Seconds, m.Confidence);
        return row;
    }

    /// <summary>A reading typed in from a handheld meter, matched to the reference mics' stored levels over the same time.</summary>
    public SurveyRow AddManual(int zoneId, double? la, double? lc, long fromTs, long toTs, string? note)
    {
        var plan = _plan.Current;
        if (plan.Zone(zoneId) is null) throw new ArgumentException("That zone doesn't exist.");
        if (la is null && lc is null) throw new ArgumentException("Enter a dBA or dBC reading (or both).");
        if (toTs - fromTs is < 5 or > 3600) throw new ArgumentException("The reading's time window must be 5 s to 1 hour.");
        var refs = new List<SurveyRef>();
        foreach (var (_, mic) in plan.AllMics.Where(x => x.Mic.Role == MicRole.Permanent))
        {
            var (ea, ec, secs, _) = _store.SumSeconds(mic.MicKey, fromTs, toTs);
            if (secs < (toTs - fromTs) * 0.5) continue;
            refs.Add(new SurveyRef(mic.MicKey, null, 10 * Math.Log10(ea / secs), 10 * Math.Log10(ec / secs),
                CurrentOffset(mic.MicKey), CurrentInfo(mic.MicKey)?.ProfileId, 1.5, 1));
        }
        if (refs.Count == 0) throw new ArgumentException("No permanent mic on the plan has logged data for that time. Check the time window.");
        var m = new SurveyMeasurement(null, la ?? double.NaN, lc ?? double.NaN, refs, toTs - fromTs, toTs - fromTs >= 30 ? 0.3 : 0.2);
        var row = _store.AddSurvey(zoneId, "manual", fromTs, toTs, null, note, m, RefPositions(refs.Select(r => r.MicKey)));
        InvalidateModels();
        return row;
    }

    private Dictionary<string, Pt> RefPositions(IEnumerable<string> keys)
    {
        var plan = _plan.Current;
        var d = new Dictionary<string, Pt>();
        foreach (var k in keys) if (plan.Mic(k) is { } p) d[k] = new Pt(p.X, p.Y);
        return d;
    }
}
