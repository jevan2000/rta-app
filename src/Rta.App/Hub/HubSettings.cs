using Rta.Core;
using System.Text.Json;
using Rta.App.Hub.Integrations;
using Rta.App.Ndi;
using Rta.App.Node;
using Rta.Core.Calibration;

namespace Rta.App.Hub;

public enum AlertMetric { LCS, LAS, LCeq1m, LAeq1m, LCeq15m, LAeq15m, LCeqService, LAeqService, LCpeakMax1m }

public sealed record AlertRule
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
    /// <summary>"*" for every mic, or a mic key ("nodeId:channel").</summary>
    public string Target { get; init; } = "*";
    public AlertMetric Metric { get; init; } = AlertMetric.LCeq1m;
    public double Warn { get; init; } = 98;
    public double Alarm { get; init; } = 103;
    /// <summary>The level must fall this far below a threshold before the state drops back.</summary>
    public double HysteresisDb { get; init; } = 1;
    /// <summary>The level must stay above a threshold this long before the state rises.</summary>
    public double MinDurationSeconds { get; init; } = 5;
    public bool Enabled { get; init; } = true;
}

public sealed record HubSettings
{
    public string HubId { get; init; } = "";
    public string Name { get; init; } = Environment.MachineName + " Hub";
    /// <summary>Per-second data older than this is deleted (minute summaries are kept forever).</summary>
    public int SecondsRetentionDays { get; init; } = 365;
    /// <summary>Accept new nodes without an admin approving them.</summary>
    public bool AutoAcceptNodes { get; init; }
    public List<AlertRule> AlertRules { get; init; } =
    [
        new AlertRule { Id = 1, Name = "dBC 1-minute", Metric = AlertMetric.LCeq1m, Warn = 98, Alarm = 103 },
    ];
    public IntegrationSettings Integrations { get; init; } = new();
    public HubNdiSettings Ndi { get; init; } = new();
}

public sealed class HubSettingsStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private HubSettings _current;

    public HubSettingsStore(NodePaths paths)
    {
        _file = Path.Combine(paths.DataDir, "hub.json");
        HubSettings s;
        s = SafeFile.ReadJson<HubSettings>(_file, CalibrationStore.Json) ?? new();
        if (string.IsNullOrWhiteSpace(s.HubId)) s = s with { HubId = Guid.NewGuid().ToString("N") };
        _current = s;
        Save(s);
    }

    public HubSettings Current { get { lock (_lock) return _current; } }

    public HubSettings Save(HubSettings s)
    {
        lock (_lock)
        {
            var current = _current;
            s = s with { HubId = current?.HubId is { Length: > 0 } id ? id : s.HubId };
            if (s.SecondsRetentionDays < 7) s = s with { SecondsRetentionDays = 7 };
            // Give new rules ids.
            var next = s.AlertRules.Count == 0 ? 1 : s.AlertRules.Max(r => r.Id) + 1;
            s = s with { AlertRules = s.AlertRules.Select(r => r.Id > 0 ? r : r with { Id = next++ }).ToList() };
            SafeFile.WriteJson(_file, s, CalibrationStore.Json);
            _current = s;
            return s;
        }
    }
}
