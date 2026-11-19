using System.Threading.Channels;
using Rta.Audio;
using Rta.Core;
using Rta.Core.Calibration;
using Rta.Core.Dsp;
using Rta.Core.Logging;
using Rta.Core.Metering;
using Rta.Core.Protocol;

namespace Rta.App.Node;

public sealed record ChannelStatus(
    int ChannelIndex,
    string Label,
    string? DeviceName,
    string Name,
    string? MicLabel,
    string? ProfileId,
    string? ProfileName,
    CalibrationQuality? ProfileQuality,
    double? ProfileUncertaintyDb,
    IReadOnlyList<string> Warnings,
    LevelSnapshot? Levels);

public sealed record DeviceStatus(int Slot, AudioBackend Backend, string DeviceId, string DeviceName, bool Running, string? Error, int Channels, long DroppedBuffers);

/// <summary>
/// Node status. Running: at least one interface is metering; Error: the interfaces that failed (or why
/// nothing runs). DeviceName/Backend/DeviceChannels summarise all interfaces for older consumers.
/// </summary>
public sealed record NodeStatus(
    string NodeName,
    bool Running,
    string? Error,
    AudioBackend Backend,
    string DeviceName,
    double SampleRate,
    int DeviceChannels,
    long DroppedBuffers,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<double> BandsHz,
    Thresholds Thresholds,
    IReadOnlyList<DeviceStatus> Devices);

/// <summary>
/// Owns the audio interfaces and per-channel meters. Each interface runs on its own: its audio
/// callback copies buffers onto its queue and its own worker thread runs the DSP, so the driver
/// thread never blocks and one interface failing (or being unplugged) doesn't stop the others.
/// Interfaces have independent clocks, which is fine: every channel is metered on its own.
/// </summary>
public sealed class MeteringService(NodeConfigStore configStore, CalibrationStore calibration, ILogger<MeteringService> log) : IHostedService
{
    private sealed class Pipeline(InputConfig config, ChannelMeter meter, LevelAggregator aggregator)
    {
        public InputConfig Config { get; set; } = config;
        public ChannelMeter Meter { get; } = meter;
        public LevelAggregator Aggregator { get; } = aggregator;
        public RecordBuilder? Records { get; set; }
        public List<Tap> Taps { get; } = [];
        public IntervalAccumulator Interval { get; } = new(meter.Bands.Count);
    }

    /// <summary>Sums blocks between live messages so the Hub gets gap-free band energy.</summary>
    private sealed class IntervalAccumulator(int bands)
    {
        private readonly object _lock = new();
        private double _a, _c, _z, _dur;
        private readonly double[] _bands = new double[bands];

        public void Add(MeterBlock b)
        {
            lock (_lock)
            {
                var d = b.DurationSeconds;
                _a += b.MsA * d; _c += b.MsC * d; _z += b.MsZ * d; _dur += d;
                for (var i = 0; i < _bands.Length && i < b.BandMs.Length; i++) _bands[i] += b.BandMs[i] * d;
            }
        }

        public IntervalLevels? Drain(CalibrationProfile? profile, IReadOnlyList<OctaveBand> bandInfo)
        {
            lock (_lock)
            {
                if (_dur <= 0) return null;
                var off = profile?.OffsetDb ?? 0;
                double L(double e) => Math.Round(Db.FromMeanSquare(e / _dur) + off, 2);
                var bands = new double[_bands.Length];
                for (var i = 0; i < bands.Length; i++)
                    bands[i] = Math.Round(Db.FromMeanSquare(_bands[i] / _dur) + off - CorrectionCurve.InterpolateDb(profile?.Correction, bandInfo[i].CenterHz), 2);
                var result = new IntervalLevels(Math.Round(_dur, 3), L(_a), L(_c), L(_z), bands);
                _a = _c = _z = _dur = 0;
                Array.Clear(_bands);
                return result;
            }
        }
    }

    /// <summary>Every raw interleaved buffer: device slot, samples, channel count, rate (fired on that device's metering thread; handlers must be quick).</summary>
    public event Action<int, float[], int, double>? RawAudio;

    /// <summary>Per-second and per-minute records for logging (fired on the metering thread).</summary>
    public event Action<LevelRecord>? RecordReady;

    private sealed class Tap(TimeSpan duration, DateTimeOffset startAfter)
    {
        public DateTimeOffset StartAfter { get; } = startAfter;
        public List<MeterBlock> Blocks { get; } = [];
        public double Seconds;
        public TimeSpan Duration { get; } = duration;
        public TaskCompletionSource<List<MeterBlock>> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>One audio interface while running (or the reason it isn't).</summary>
    private sealed class DeviceRun(DeviceConfig config)
    {
        public DeviceConfig Config { get; } = config;
        public IAudioInput? Input;
        public Channel<float[]>? Queue;
        public Task? Worker;
        public int Channels;
        public string? Error;
        public long Dropped;
        public Dictionary<int, Pipeline> Pipelines = [];
    }

    private readonly object _lock = new();
    private List<DeviceRun> _devices = [];
    private Dictionary<int, Pipeline> _pipelines = [];
    private string? _error;
    private readonly List<string> _runtimeWarnings = [];

    public NodeConfig Config { get; private set; } = configStore.Load();

    public Task StartAsync(CancellationToken ct)
    {
        if (Config.AutoStart)
        {
            try { Start(); }
            catch (Exception ex) { log.LogError(ex, "Audio start failed"); }
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { Stop(); return Task.CompletedTask; }

    /// <summary>(Re)starts every configured interface. Throws only if none of them could start.</summary>
    public void Start()
    {
        lock (_lock)
        {
            StopLocked();
            _error = null;
            lock (_runtimeWarnings) _runtimeWarnings.Clear();
            var cfg = Config;
            var runs = new List<DeviceRun>();
            foreach (var dc in cfg.Devices)
            {
                var run = new DeviceRun(dc);
                runs.Add(run);
                try
                {
                    var input = AudioInputFactory.Create(dc.Backend, dc.DeviceId, cfg.SampleRate);
                    input.Faulted += msg => { lock (_runtimeWarnings) if (!_runtimeWarnings.Contains(msg)) _runtimeWarnings.Add(msg); log.LogWarning("Audio: {Message}", msg); };
                    var queue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(200)
                    {
                        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = true,
                    });
                    input.SamplesAvailable += (data, _) => { if (!queue.Writer.TryWrite(data.ToArray())) Interlocked.Increment(ref run.Dropped); };
                    run.Queue = queue;
                    run.Input = input;
                    input.Start();
                    run.Channels = input.ChannelCount;
                }
                catch (Exception ex)
                {
                    run.Error = $"Could not start {DeviceLabel(dc)}: {ex.Message}";
                    log.LogError(ex, "Audio start failed for {Device}", DeviceLabel(dc));
                    try { run.Input?.Dispose(); } catch { /* already failing */ }
                    run.Input = null;
                    run.Queue?.Writer.TryComplete();
                    run.Queue = null;
                }
            }
            _devices = runs;

            // First run on an interface: create an input entry per channel (up to 8 enabled per interface).
            var added = runs.Where(r => r.Input is not null && !cfg.Inputs.Any(i => InputNumber.Slot(i.ChannelIndex) == r.Config.Slot))
                .SelectMany(r => Enumerable.Range(0, Math.Min(r.Channels, InputNumber.PerDevice)).Select(i => new InputConfig
                {
                    ChannelIndex = InputNumber.Of(r.Config.Slot, i),
                    Name = r.Config.Slot == 0 ? $"Mic {i + 1}" : $"{ShortName(r.Input!.DeviceName)} {i + 1}",
                    Enabled = i < 8,
                })).ToList();
            if (added.Count > 0)
            {
                cfg = cfg with { Inputs = [.. cfg.Inputs, .. added] };
                Config = cfg;
                configStore.Save(cfg);
            }

            var all = new Dictionary<int, Pipeline>();
            foreach (var run in runs.Where(r => r.Input is not null))
            {
                var input = run.Input!;
                foreach (var ic in cfg.Inputs.Where(i => i.Enabled && InputNumber.Slot(i.ChannelIndex) == run.Config.Slot
                                                          && InputNumber.DeviceChannel(i.ChannelIndex) < input.ChannelCount))
                {
                    var meter = new ChannelMeter(input.SampleRate);
                    var agg = new LevelAggregator(meter.Bands);
                    var p = new Pipeline(ic, meter, agg);
                    p.Records = new RecordBuilder(ic.ChannelIndex, meter.Bands, () => ProfileFor(p.Config));
                    p.Records.RecordReady += r => RecordReady?.Invoke(r);
                    meter.BlockReady += b => OnBlock(p, b);
                    run.Pipelines[ic.ChannelIndex] = p;
                    all[ic.ChannelIndex] = p;
                }
                var queue = run.Queue!;
                var current = run;
                run.Worker = Task.Factory.StartNew(() => Work(current, queue), TaskCreationOptions.LongRunning).Unwrap();
                log.LogInformation("Metering {Count} channel(s) on {Device} at {Rate} Hz", run.Pipelines.Count, input.DeviceName, input.SampleRate);
            }
            _pipelines = all;

            var failed = runs.Where(r => r.Error is not null).ToList();
            if (failed.Count == runs.Count)
            {
                _error = string.Join(" ", failed.Select(f => f.Error));
                throw new InvalidOperationException(_error);
            }
        }
    }

    public void Stop()
    {
        lock (_lock) StopLocked();
    }

    private void StopLocked()
    {
        foreach (var run in _devices)
        {
            run.Input?.Dispose();
            run.Input = null;
            run.Queue?.Writer.TryComplete();
            try { run.Worker?.Wait(TimeSpan.FromSeconds(2)); } catch { /* worker faulted; already logged */ }
            run.Worker = null;
            run.Queue = null;
        }
        foreach (var p in _pipelines.Values)
        {
            try { p.Records?.Flush(); } catch (Exception ex) { log.LogWarning(ex, "Flushing records failed"); }
            lock (p.Taps) foreach (var t in p.Taps) t.Done.TrySetCanceled();
        }
        _devices = [];
        _pipelines = [];
    }

    private async Task Work(DeviceRun run, Channel<float[]> queue)
    {
        var stride = run.Channels;
        var rate = run.Input?.SampleRate ?? Config.SampleRate;
        var pipelines = run.Pipelines.Values.ToList();
        try
        {
            await foreach (var buf in queue.Reader.ReadAllAsync())
            {
                foreach (var p in pipelines)
                    p.Meter.ProcessInterleaved(buf, InputNumber.DeviceChannel(p.Config.ChannelIndex), stride);
                try { RawAudio?.Invoke(run.Config.Slot, buf, stride, rate); }
                catch (Exception ex) { log.LogWarning(ex, "Raw audio consumer failed"); }
            }
        }
        catch (Exception ex)
        {
            run.Error = "Metering stopped: " + ex.Message;
            log.LogError(ex, "Metering worker failed for {Device}", DeviceLabel(run.Config));
        }
    }

    private static string DeviceLabel(DeviceConfig d) =>
        d.Backend == AudioBackend.TestSignal ? "the test signal" : $"{d.Backend} device '{d.DeviceId}'";

    /// <summary>A short name for default input names, e.g. "Behringer UMC404HD" gives "Behringer".</summary>
    private static string ShortName(string deviceName)
    {
        var name = deviceName.Split(['(', ','], 2)[0].Trim();
        var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Input";
        return first.Length >= 3 ? first : name;
    }

    private CalibrationProfile? ProfileFor(InputConfig ic) => ic.ProfileId is null ? null : calibration.Get(ic.ProfileId);

    private void OnBlock(Pipeline p, MeterBlock b)
    {
        p.Aggregator.Add(b);
        p.Interval.Add(b);
        try { p.Records?.Add(b); } catch (Exception ex) { log.LogWarning(ex, "Record logging failed"); }
        lock (p.Taps)
        {
            for (var i = p.Taps.Count - 1; i >= 0; i--)
            {
                var t = p.Taps[i];
                if (b.End <= t.StartAfter) continue;
                t.Blocks.Add(b);
                t.Seconds += b.DurationSeconds;
                if (t.Seconds >= t.Duration.TotalSeconds - 1e-6)
                {
                    p.Taps.RemoveAt(i);
                    t.Done.TrySetResult(t.Blocks);
                }
            }
        }
    }

    public void UpdateConfig(NodeConfig next)
    {
        if (!NodeConfig.AllowedSampleRates.Contains(next.SampleRate))
            throw new ArgumentException($"Sample rate must be one of {string.Join(", ", NodeConfig.AllowedSampleRates)}.");
        var prev = Config;
        // Identity is never changed from the UI. A page from 1.0.x sends only Backend/DeviceId: that is device 0.
        next = (next.Devices.Count == 0 ? next with { Devices = [new DeviceConfig { Slot = 0, Backend = next.Backend, DeviceId = next.DeviceId }] } : next)
            .Normalized() with { NodeId = prev.NodeId };
        if (next.DeviceProblem() is { } problem) throw new ArgumentException(problem);
        var deviceChanged = next.SampleRate != prev.SampleRate || !next.Devices.SequenceEqual(prev.Devices);
        configStore.Save(next);
        Config = next;

        bool running;
        lock (_lock)
        {
            running = _devices.Any(d => d.Input is not null);
            // Names, mic labels, profiles and thresholds apply live without losing Leq history.
            var enabledNow = next.Inputs.Where(i => i.Enabled).Select(i => i.ChannelIndex).Order().ToList();
            var metering = _pipelines.Keys.Order().ToList();
            var allUp = _devices.Count > 0 && _devices.All(d => d.Input is not null);
            if (allUp && !deviceChanged && enabledNow.SequenceEqual(metering))
            {
                foreach (var ic in next.Inputs.Where(i => i.Enabled)) _pipelines[ic.ChannelIndex].Config = ic;
                return;
            }
        }
        if (running || deviceChanged || next.AutoStart) Start();
    }

    public void ResetSession()
    {
        foreach (var p in _pipelines.Values) p.Aggregator.ResetSession();
    }

    /// <summary>Captures raw blocks from one channel for calibration.</summary>
    public async Task<(List<MeterBlock> Blocks, IReadOnlyList<OctaveBand> Bands)> CaptureAsync(int channel, TimeSpan duration, CancellationToken ct)
    {
        var (all, bands) = await CaptureManyAsync([channel], duration, ct);
        return (all[channel], bands);
    }

    /// <summary>
    /// Captures several channels over the same period. All channels are metered from the same
    /// buffers, so blocks line up by index; capture starts after a short settle delay.
    /// </summary>
    public async Task<(Dictionary<int, List<MeterBlock>> Blocks, IReadOnlyList<OctaveBand> Bands)> CaptureManyAsync(
        IReadOnlyList<int> channels, TimeSpan duration, CancellationToken ct)
    {
        var pipelines = _pipelines;
        foreach (var ch in channels)
            if (!pipelines.ContainsKey(ch))
                throw new InvalidOperationException($"Input {ch + 1} is not being metered. Start audio and enable the input first.");

        var startAfter = DateTimeOffset.UtcNow.AddSeconds(0.5); // let the user settle; skip filter transients
        var taps = channels.Distinct().ToDictionary(ch => ch, _ => new Tap(duration, startAfter));
        foreach (var (ch, tap) in taps) lock (pipelines[ch].Taps) pipelines[ch].Taps.Add(tap);
        using var reg = ct.Register(() => { foreach (var t in taps.Values) t.Done.TrySetCanceled(); });
        try
        {
            var results = await Task.WhenAll(taps.Values.Select(t => t.Done.Task)).WaitAsync(duration + TimeSpan.FromSeconds(10), ct);
            return (taps.Keys.Zip(results).ToDictionary(x => x.First, x => x.Second), pipelines[channels[0]].Meter.Bands);
        }
        finally
        {
            foreach (var (ch, tap) in taps) lock (pipelines[ch].Taps) pipelines[ch].Taps.Remove(tap);
        }
    }

    public NodeStatus Status()
    {
        var devices = _devices;
        var up = devices.Where(d => d.Input is not null).ToList();
        var dropped = devices.Sum(d => Interlocked.Read(ref d.Dropped));
        var bands = _pipelines.Values.FirstOrDefault()?.Meter.Bands.Select(b => b.NominalHz).ToList()
                    ?? new ThirdOctaveBank(Config.SampleRate).Bands.Select(b => b.NominalHz).ToList();
        var warnings = new List<string>();
        lock (_runtimeWarnings) warnings.AddRange(_runtimeWarnings);
        if (Config.SampleRate > 48000)
            warnings.Add($"{Config.SampleRate / 1000.0:0.#} kHz uses about twice the CPU and network of 48 kHz. 48 kHz is enough for SPL metering.");
        foreach (var d in devices.Where(d => d.Dropped > 0))
            warnings.Add($"{d.Dropped} audio buffers dropped on {d.Input?.DeviceName ?? d.Config.DeviceId}: the PC could not keep up; levels may be slightly low.");
        foreach (var d in up)
        {
            var missing = Config.Inputs.Count(i => i.Enabled && InputNumber.Slot(i.ChannelIndex) == d.Config.Slot && InputNumber.DeviceChannel(i.ChannelIndex) >= d.Channels);
            if (missing > 0) warnings.Add($"{d.Input!.DeviceName} has only {d.Channels} inputs; {missing} configured input(s) ignored.");
        }
        if (_pipelines.Count > 16)
            warnings.Add($"{_pipelines.Count} inputs on one node. Above ~16 inputs, CPU and network load grow quickly.");

        // Running if anything meters; the error names whatever didn't start (or why nothing runs).
        var errors = devices.Where(d => d.Error is not null).Select(d => d.Error!).ToList();
        var error = up.Count == 0 ? _error ?? (errors.Count > 0 ? string.Join(" ", errors) : null) : errors.Count > 0 ? string.Join(" ", errors) : null;
        var statuses = devices.Select(d => new DeviceStatus(d.Config.Slot, d.Config.Backend, d.Config.DeviceId, d.Input?.DeviceName ?? d.Config.DeviceId,
            d.Input is not null && d.Error is null, d.Error, d.Channels, d.Dropped)).ToList();
        return new NodeStatus(Config.NodeName, up.Count > 0, error, up.FirstOrDefault()?.Config.Backend ?? Config.Backend,
            string.Join(" + ", up.Select(d => d.Input!.DeviceName)), up.FirstOrDefault()?.Input!.SampleRate ?? Config.SampleRate,
            up.Sum(d => d.Channels), dropped, warnings, bands, Config.Thresholds, statuses);
    }

    public IReadOnlyList<ChannelStatus> Channels()
    {
        var profiles = calibration.List().ToDictionary(p => p.Id);
        var inputs = _devices.Where(d => d.Input is not null).ToDictionary(d => d.Config.Slot, d => d.Input!);
        var list = new List<ChannelStatus>();
        foreach (var p in _pipelines.Values.OrderBy(p => p.Config.ChannelIndex))
        {
            var ic = p.Config;
            var profile = ic.ProfileId is not null && profiles.TryGetValue(ic.ProfileId, out var pr) ? pr : null;
            var input = inputs.GetValueOrDefault(InputNumber.Slot(ic.ChannelIndex));
            var warnings = CalibrationChecks.Warnings(profile,
                new InputContext(input?.DeviceName, ic.ChannelIndex, ic.MicLabel, input?.SampleRate ?? 0)).ToList();
            if (ic.ProfileId is not null && profile is null) warnings.Insert(0, "Assigned calibration profile no longer exists.");
            var snap = p.Aggregator.Snapshot(profile);
            if (snap?.ClippedRecently == true) warnings.Insert(0, "Input is clipping — turn the preamp gain down (and recalibrate).");
            list.Add(new ChannelStatus(ic.ChannelIndex, InputNumber.Label(ic.ChannelIndex), input?.DeviceName, ic.Name, ic.MicLabel, ic.ProfileId, profile?.Name,
                profile?.Quality, profile?.UncertaintyDb, warnings, snap));
        }
        return list;
    }

    public bool IsRunning => _devices.Any(d => d.Input is not null && d.Error is null);

    public IReadOnlyList<MicInfo> MicInfos() => Channels()
        .Select(c => new MicInfo(c.ChannelIndex, c.Name, c.MicLabel, c.ProfileName, c.ProfileQuality, c.ProfileUncertaintyDb,
            c.Levels?.Calibrated ?? false, c.Levels?.OffsetDb ?? 0, c.ProfileId))
        .ToList();

    /// <summary>Drains each input's interval accumulator. Call from one consumer only (the Hub client).</summary>
    private Dictionary<int, IntervalLevels?> DrainIntervals()
    {
        var result = new Dictionary<int, IntervalLevels?>();
        foreach (var p in _pipelines.Values)
            result[p.Config.ChannelIndex] = p.Interval.Drain(ProfileFor(p.Config), p.Meter.Bands);
        return result;
    }

    public LiveUpdate BuildLive()
    {
        static double R(double v) => Math.Round(v, 1);
        var status = Status();
        var intervals = DrainIntervals();
        var mics = Channels().Where(c => c.Levels is not null).Select(c =>
        {
            var L = c.Levels!;
            return new LiveMic(c.ChannelIndex, R(L.LAS), R(L.LCS), R(L.LAF), R(L.LCF),
                R(L.LAeq1m), R(L.LCeq1m), R(L.LAeq15m), R(L.LCeq15m), R(L.LCpeakMax1m), R(L.CMinusA),
                Math.Round(L.Coverage1mSeconds), L.Calibrated, L.ClippedRecently,
                L.Bands.Select(R).ToArray(), c.Warnings, intervals.GetValueOrDefault(c.ChannelIndex));
        }).ToList();
        return new LiveUpdate(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), status.Running, status.Error, mics, status.Warnings);
    }

    /// <summary>Device list. A driver we are currently capturing from is reported from the live input (ASIO drivers are usually single-client).</summary>
    public IReadOnlyList<AudioDeviceInfo> Devices()
    {
        var list = new List<AudioDeviceInfo>();
        var input = _devices.Select(d => d.Input).OfType<AsioInput>().FirstOrDefault();
        var asioInUse = _devices.FirstOrDefault(d => d.Input is AsioInput)?.Config.DeviceId;
        foreach (var name in AsioInput.DriverNames())
        {
            if (input is not null && asioInUse == name)
                list.Add(new AudioDeviceInfo(AudioBackend.Asio, name, name, input.ChannelCount,
                    Enumerable.Range(1, input.ChannelCount).Select(i => $"Input {i}").ToList(),
                    [(int)input.SampleRate], "In use by this app; stop audio to see all rates."));
            else list.Add(AsioInput.Describe(name));
        }
        list.AddRange(WasapiInput.List());
        list.Add(TestSignalInput.Info);
        list.Add(TestSignalInput.SecondInfo);
        return list;
    }
}
