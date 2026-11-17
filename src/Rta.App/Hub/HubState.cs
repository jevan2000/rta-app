using System.Collections.Concurrent;
using Rta.Core.Calibration;
using Rta.Core.Logging;
using Rta.Core.Metering;
using Rta.Core.Protocol;

namespace Rta.App.Hub;

/// <summary>A connected node's socket, used to push welcome/ack messages.</summary>
public sealed class NodeConnection(string nodeId, string? address, Func<byte[], Task> send)
{
    public string NodeId { get; } = nodeId;
    public string? Address { get; } = address;
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;
    public Hello? Hello { get; set; }
    public LiveUpdate? Live { get; set; }
    public DateTimeOffset LastMessage { get; set; } = DateTimeOffset.UtcNow;
    public Task SendAsync(byte[] data) => send(data);
}

public sealed record ServiceLive(long Id, string Name, long StartTs, string Source);

public sealed record MicLiveView(
    string Key, string NodeId, string NodeName, int ChannelIndex, string Name, string Label, string? MicLabel,
    string? ProfileName, CalibrationQuality? Quality, double? UncertaintyDb, bool Calibrated, bool Hidden,
    bool Online, LiveMic? Live, double? LAeqService, double? LCeqService, double ServiceSeconds);

public sealed record NodeLiveView(
    string Id, string Name, NodeAcceptance Acceptance, bool Connected, string? Address, long LastSeen, string? Version,
    bool Running, string? Error, IReadOnlyList<string> Warnings, int MicCount, double? SampleRate);

public sealed record ActiveAlertView(long RuleId, string RuleName, string MicKey, string Level, AlertMetric Metric, double Value, double Threshold, long Since);

public sealed record HubLive(
    long T, string HubId, string HubName, ServiceLive? Service,
    IReadOnlyList<NodeLiveView> Nodes, IReadOnlyList<MicLiveView> Mics, IReadOnlyList<ActiveAlertView> Alerts,
    IReadOnlyList<double> BandsHz);

/// <summary>In-memory live view of the Hub: connected nodes, latest readings and the running service's Leq.</summary>
public sealed class HubState(HubStore store, HubSettingsStore settings)
{
    private readonly ConcurrentDictionary<string, NodeConnection> _connections = new();
    private readonly object _serviceLock = new();
    private ServiceLive? _service;
    private readonly Dictionary<string, (double A, double C, double Seconds)> _serviceSums = [];
    private volatile IReadOnlyList<ActiveAlertView> _alerts = [];
    private List<MicRow> _mics = [];
    private DateTimeOffset _micsLoaded = DateTimeOffset.MinValue;

    public event Action? Changed;

    public IReadOnlyDictionary<string, NodeConnection> Connections => _connections;

    public void Register(NodeConnection c) { _connections[c.NodeId] = c; InvalidateMics(); }

    public void Unregister(NodeConnection c)
    {
        _connections.TryRemove(new KeyValuePair<string, NodeConnection>(c.NodeId, c));
        InvalidateMics();
    }

    public void InvalidateMics() => _micsLoaded = DateTimeOffset.MinValue;

    private List<MicRow> Mics()
    {
        if (DateTimeOffset.UtcNow - _micsLoaded > TimeSpan.FromSeconds(5))
        {
            _mics = store.ListMics();
            _micsLoaded = DateTimeOffset.UtcNow;
        }
        return _mics;
    }

    // ---------- service tracking ----------

    public ServiceLive? Service { get { lock (_serviceLock) return _service; } }

    /// <summary>Sets (or clears) the running service and rebuilds its Leq from stored seconds.</summary>
    public void SetActiveService(ServiceRow? row)
    {
        lock (_serviceLock)
        {
            _serviceSums.Clear();
            _service = row is null ? null : new ServiceLive(row.Id, row.Name, row.StartTs, row.Source);
            if (row is null) return;
            var now = HubStore.Now() + 1;
            foreach (var m in Mics())
            {
                var (a, c, s, _) = store.SumSeconds(m.Key, row.StartTs, now);
                if (s > 0) _serviceSums[m.Key] = (a, c, s);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>Adds newly stored per-second records to the running service's totals.</summary>
    public void OnRecordsStored(string nodeId, IReadOnlyList<SeqRecord> items)
    {
        lock (_serviceLock)
        {
            if (_service is null) return;
            foreach (var (_, r) in items)
            {
                if (r.Kind != RecordKind.Second || r.Ts < _service.StartTs) continue;
                var key = HubStore.MicKey(nodeId, r.ChannelIndex);
                var (a, c, s) = _serviceSums.GetValueOrDefault(key);
                _serviceSums[key] = (a + Math.Pow(10, r.LAeq / 10) * r.Coverage, c + Math.Pow(10, r.LCeq / 10) * r.Coverage, s + r.Coverage);
            }
        }
    }

    public (double? LA, double? LC, double Seconds) ServiceLeq(string micKey)
    {
        lock (_serviceLock)
        {
            if (_service is null || !_serviceSums.TryGetValue(micKey, out var v) || v.Seconds <= 0) return (null, null, 0);
            return (Math.Round(10 * Math.Log10(v.A / v.Seconds), 1), Math.Round(10 * Math.Log10(v.C / v.Seconds), 1), v.Seconds);
        }
    }

    // ---------- alerts ----------

    public IReadOnlyList<ActiveAlertView> ActiveAlerts => _alerts;
    public void SetActiveAlerts(IReadOnlyList<ActiveAlertView> alerts) { _alerts = alerts; }

    // ---------- snapshot ----------

    public static bool IsOnline(NodeConnection? c) => c is not null && DateTimeOffset.UtcNow - c.LastMessage < TimeSpan.FromSeconds(10);

    public HubLive Snapshot()
    {
        var s = settings.Current;
        var nodes = store.ListNodes();
        var nodeNames = nodes.ToDictionary(n => n.Id, n => n.Name);
        var accepted = nodes.Where(n => n.Acceptance == NodeAcceptance.Accepted).Select(n => n.Id).ToHashSet();

        var nodeViews = nodes.Select(n =>
        {
            _connections.TryGetValue(n.Id, out var c);
            var online = IsOnline(c);
            return new NodeLiveView(n.Id, n.Name, n.Acceptance, online, c?.Address ?? n.Address, n.LastSeen, n.Version,
                online && (c!.Live?.Running ?? false), online ? c!.Live?.Error : null, online ? c!.Live?.NodeWarnings ?? [] : [],
                c?.Hello?.Mics.Count ?? Mics().Count(m => m.NodeId == n.Id && !m.Hidden), c?.Hello?.SampleRate);
        }).ToList();

        var mics = new List<MicLiveView>();
        foreach (var m in Mics().Where(m => accepted.Contains(m.NodeId)))
        {
            _connections.TryGetValue(m.NodeId, out var c);
            var online = IsOnline(c);
            var live = online ? c!.Live?.Mics.FirstOrDefault(x => x.Ch == m.ChannelIndex) : null;
            var info = online ? c!.Hello?.Mics.FirstOrDefault(x => x.ChannelIndex == m.ChannelIndex) : null;
            var (sa, sc, ss) = ServiceLeq(m.Key);
            mics.Add(new MicLiveView(m.Key, m.NodeId, nodeNames.GetValueOrDefault(m.NodeId, "?"), m.ChannelIndex,
                info?.Name ?? m.Name, string.IsNullOrWhiteSpace(m.DisplayName) ? (info?.Name ?? m.Name) : m.DisplayName!,
                info?.MicLabel ?? m.MicLabel, info?.ProfileName ?? m.ProfileName, info?.Quality ?? m.Quality,
                info?.UncertaintyDb ?? m.UncertaintyDb, info?.Calibrated ?? m.Calibrated, m.Hidden,
                online && live is not null, live, sa, sc, ss));
        }

        var bands = _connections.Values.Select(c => c.Hello?.BandsHz).FirstOrDefault(b => b is { Count: > 0 }) ?? [];
        return new HubLive(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), s.HubId, s.Name, Service, nodeViews, mics, _alerts, bands);
    }

    /// <summary>Current value of a metric for one mic, or null if unavailable.</summary>
    public static double? MetricValue(MicLiveView m, AlertMetric metric)
    {
        var L = m.Live;
        if (L is null) return null;
        double? v = metric switch
        {
            AlertMetric.LCS => L.LCS,
            AlertMetric.LAS => L.LAS,
            AlertMetric.LCeq1m => L.Coverage1m >= 5 ? L.LCeq1m : null,
            AlertMetric.LAeq1m => L.Coverage1m >= 5 ? L.LAeq1m : null,
            AlertMetric.LCeq15m => L.LCeq15m,
            AlertMetric.LAeq15m => L.LAeq15m,
            AlertMetric.LCeqService => m.LCeqService,
            AlertMetric.LAeqService => m.LAeqService,
            AlertMetric.LCpeakMax1m => L.LCpeakMax1m,
            _ => null,
        };
        return v is double d && d > Db.FloorDb + 1 ? d : null;
    }

    public void NotifyChanged() => Changed?.Invoke();
}
