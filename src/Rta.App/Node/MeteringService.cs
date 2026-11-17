using System.Threading.Channels;
using Rta.Audio;
using Rta.Core.Calibration;
using Rta.Core.Dsp;
using Rta.Core.Metering;

namespace Rta.App.Node;

public sealed record ChannelStatus(
    int ChannelIndex,
    string Name,
    string? MicLabel,
    string? ProfileId,
    string? ProfileName,
    CalibrationQuality? ProfileQuality,
    double? ProfileUncertaintyDb,
    IReadOnlyList<string> Warnings,
    LevelSnapshot? Levels);

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
    Thresholds Thresholds);

/// <summary>
/// Owns the audio input and per-channel meters. Audio callbacks copy buffers onto a queue; a
/// single worker thread runs the DSP so the driver thread never blocks.
/// </summary>
public sealed class MeteringService(NodeConfigStore configStore, CalibrationStore calibration, ILogger<MeteringService> log) : IHostedService
{
    private sealed class Pipeline(InputConfig config, ChannelMeter meter, LevelAggregator aggregator)
    {
        public InputConfig Config { get; set; } = config;
        public ChannelMeter Meter { get; } = meter;
        public LevelAggregator Aggregator { get; } = aggregator;
        public List<Tap> Taps { get; } = [];
    }

    private sealed class Tap(TimeSpan duration, DateTimeOffset startAfter)
    {
        public DateTimeOffset StartAfter { get; } = startAfter;
        public List<MeterBlock> Blocks { get; } = [];
        public double Seconds;
        public TimeSpan Duration { get; } = duration;
        public TaskCompletionSource<List<MeterBlock>> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _lock = new();
    private IAudioInput? _input;
    private Channel<float[]>? _queue;
    private Task? _worker;
    private int _queueChannels;
    private Dictionary<int, Pipeline> _pipelines = [];
    private string? _error;
    private long _dropped;
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

    public void Start()
    {
        lock (_lock)
        {
            StopLocked();
            _error = null;
            _dropped = 0;
            _runtimeWarnings.Clear();
            var cfg = Config;
            IAudioInput input;
            try
            {
                input = AudioInputFactory.Create(cfg.Backend, cfg.DeviceId, cfg.SampleRate);
                input.Faulted += msg => { lock (_runtimeWarnings) if (!_runtimeWarnings.Contains(msg)) _runtimeWarnings.Add(msg); log.LogWarning("Audio: {Message}", msg); };
                input.SamplesAvailable += OnSamples;
                _queue = Channel.CreateBounded<float[]>(new BoundedChannelOptions(200)
                {
                    FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, SingleWriter = true,
                });
                input.Start();
            }
            catch (Exception ex)
            {
                _error = $"Could not start {cfg.Backend} device: {ex.Message}";
                throw new InvalidOperationException(_error, ex);
            }

            _input = input;
            _queueChannels = input.ChannelCount;

            // First run on a device: create an input entry per channel (up to 8 enabled).
            if (cfg.Inputs.Count == 0)
            {
                cfg = cfg with
                {
                    Inputs = Enumerable.Range(0, input.ChannelCount)
                        .Select(i => new InputConfig { ChannelIndex = i, Name = $"Mic {i + 1}", Enabled = i < 8 })
                        .ToList(),
                };
                Config = cfg;
                configStore.Save(cfg);
            }

            var pipelines = new Dictionary<int, Pipeline>();
            foreach (var ic in cfg.Inputs.Where(i => i.Enabled && i.ChannelIndex < input.ChannelCount))
            {
                var meter = new ChannelMeter(input.SampleRate);
                var agg = new LevelAggregator(meter.Bands);
                var p = new Pipeline(ic, meter, agg);
                meter.BlockReady += b => OnBlock(p, b);
                pipelines[ic.ChannelIndex] = p;
            }
            _pipelines = pipelines;

            var queue = _queue!;
            _worker = Task.Factory.StartNew(() => Work(queue), TaskCreationOptions.LongRunning);
            log.LogInformation("Metering {Count} channel(s) on {Device} at {Rate} Hz", pipelines.Count, input.DeviceName, input.SampleRate);
        }
    }

    public void Stop()
    {
        lock (_lock) StopLocked();
    }

    private void StopLocked()
    {
        _input?.Dispose();
        _input = null;
        _queue?.Writer.TryComplete();
        try { _worker?.Wait(TimeSpan.FromSeconds(2)); } catch { /* worker faulted; already logged */ }
        _worker = null;
        _queue = null;
        foreach (var p in _pipelines.Values)
            lock (p.Taps) foreach (var t in p.Taps) t.Done.TrySetCanceled();
    }

    private void OnSamples(ReadOnlySpan<float> interleaved, int channels)
    {
        var q = _queue;
        if (q is null) return;
        if (!q.Writer.TryWrite(interleaved.ToArray())) Interlocked.Increment(ref _dropped);
    }

    private async Task Work(Channel<float[]> queue)
    {
        try
        {
            await foreach (var buf in queue.Reader.ReadAllAsync())
            {
                var pipelines = _pipelines;
                var stride = _queueChannels;
                foreach (var p in pipelines.Values)
                    p.Meter.ProcessInterleaved(buf, p.Config.ChannelIndex, stride);
            }
        }
        catch (Exception ex)
        {
            _error = "Metering stopped: " + ex.Message;
            log.LogError(ex, "Metering worker failed");
        }
    }

    private static void OnBlock(Pipeline p, MeterBlock b)
    {
        p.Aggregator.Add(b);
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
        var deviceChanged = next.Backend != prev.Backend || next.DeviceId != prev.DeviceId || next.SampleRate != prev.SampleRate;
        configStore.Save(next);
        Config = next;

        lock (_lock)
        {
            // Names, mic labels, profiles and thresholds apply live without losing Leq history.
            var enabledNow = next.Inputs.Where(i => i.Enabled).Select(i => i.ChannelIndex).Order().ToList();
            var running = _pipelines.Keys.Order().ToList();
            if (_input is not null && !deviceChanged && enabledNow.SequenceEqual(running))
            {
                foreach (var ic in next.Inputs.Where(i => i.Enabled)) _pipelines[ic.ChannelIndex].Config = ic;
                return;
            }
        }
        if (_input is not null || deviceChanged || next.AutoStart) Start();
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
        var input = _input;
        var bands = _pipelines.Values.FirstOrDefault()?.Meter.Bands.Select(b => b.NominalHz).ToList()
                    ?? new ThirdOctaveBank(Config.SampleRate).Bands.Select(b => b.NominalHz).ToList();
        var warnings = new List<string>();
        lock (_runtimeWarnings) warnings.AddRange(_runtimeWarnings);
        if (Config.SampleRate > 48000)
            warnings.Add($"{Config.SampleRate / 1000.0:0.#} kHz uses about twice the CPU and network of 48 kHz. 48 kHz is enough for SPL metering.");
        if (_dropped > 0)
            warnings.Add($"{_dropped} audio buffers dropped — the PC could not keep up; levels may be slightly low.");
        var missing = Config.Inputs.Where(i => i.Enabled && input is not null && i.ChannelIndex >= input.ChannelCount).ToList();
        if (missing.Count > 0)
            warnings.Add($"Device has only {input!.ChannelCount} inputs; {missing.Count} configured input(s) ignored.");
        if (_pipelines.Count > 8)
            warnings.Add($"{_pipelines.Count} inputs on one node. Above ~8 inputs, CPU and (in later phases) network load grow quickly.");

        return new NodeStatus(Config.NodeName, input is not null && _error is null, _error, Config.Backend,
            input?.DeviceName ?? "", input?.SampleRate ?? Config.SampleRate, input?.ChannelCount ?? 0,
            _dropped, warnings, bands, Config.Thresholds);
    }

    public IReadOnlyList<ChannelStatus> Channels()
    {
        var profiles = calibration.List().ToDictionary(p => p.Id);
        var input = _input;
        var list = new List<ChannelStatus>();
        foreach (var p in _pipelines.Values.OrderBy(p => p.Config.ChannelIndex))
        {
            var ic = p.Config;
            var profile = ic.ProfileId is not null && profiles.TryGetValue(ic.ProfileId, out var pr) ? pr : null;
            var warnings = CalibrationChecks.Warnings(profile,
                new InputContext(input?.DeviceName, ic.ChannelIndex, ic.MicLabel, input?.SampleRate ?? 0)).ToList();
            if (ic.ProfileId is not null && profile is null) warnings.Insert(0, "Assigned calibration profile no longer exists.");
            var snap = p.Aggregator.Snapshot(profile);
            if (snap?.ClippedRecently == true) warnings.Insert(0, "Input is clipping — turn the preamp gain down (and recalibrate).");
            list.Add(new ChannelStatus(ic.ChannelIndex, ic.Name, ic.MicLabel, ic.ProfileId, profile?.Name,
                profile?.Quality, profile?.UncertaintyDb, warnings, snap));
        }
        return list;
    }

    /// <summary>Device list. A driver we are currently capturing from is reported from the live input (ASIO drivers are usually single-client).</summary>
    public IReadOnlyList<AudioDeviceInfo> Devices()
    {
        var list = new List<AudioDeviceInfo>();
        var input = _input;
        foreach (var name in AsioInput.DriverNames())
        {
            if (input is AsioInput && Config.Backend == AudioBackend.Asio && Config.DeviceId == name)
                list.Add(new AudioDeviceInfo(AudioBackend.Asio, name, name, input.ChannelCount,
                    Enumerable.Range(1, input.ChannelCount).Select(i => $"Input {i}").ToList(),
                    [(int)input.SampleRate], "In use by this app; stop audio to see all rates."));
            else list.Add(AsioInput.Describe(name));
        }
        list.AddRange(WasapiInput.List());
        list.Add(TestSignalInput.Info);
        return list;
    }
}
