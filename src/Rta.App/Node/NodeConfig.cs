using System.Text.Json;
using Rta.Audio;
using Rta.Core.Calibration;

namespace Rta.App.Node;

public sealed record InputConfig
{
    public int ChannelIndex { get; init; }
    public bool Enabled { get; init; } = true;
    public string Name { get; init; } = "";
    /// <summary>Which physical mic is plugged in (free text, e.g. "dbx RTA-M #1"). Used for calibration suggestions.</summary>
    public string? MicLabel { get; init; }
    public string? ProfileId { get; init; }
    /// <summary>Publish this input's raw audio as an NDI source.</summary>
    public bool NdiAudio { get; init; }
    /// <summary>Publish a rendered meter (video + level metadata) for this input as an NDI source.</summary>
    public bool NdiMeter { get; init; }
}

public sealed record Thresholds
{
    public double WarnC { get; init; } = 98;
    public double AlarmC { get; init; } = 103;
    public double WarnA { get; init; } = 92;
    public double AlarmA { get; init; } = 97;
}

public enum HubConnectionMode { Auto, Manual, Off }

public sealed record NodeConfig
{
    /// <summary>Stable identity of this node (generated on first run).</summary>
    public string NodeId { get; init; } = "";
    public string NodeName { get; init; } = Environment.MachineName;
    /// <summary>How this node finds the Hub: auto-discover on the LAN, a fixed address, or not at all.</summary>
    public HubConnectionMode HubConnection { get; init; } = HubConnectionMode.Auto;
    /// <summary>For Manual: "host:port" or a URL, e.g. "192.168.1.20:5180".</summary>
    public string? HubAddress { get; init; }
    public AudioBackend Backend { get; init; } = AudioBackend.TestSignal;
    public string DeviceId { get; init; } = TestSignalInput.DeviceId;
    public int SampleRate { get; init; } = 48000;
    public bool AutoStart { get; init; } = true;
    public List<InputConfig> Inputs { get; init; } = [];
    public Thresholds Thresholds { get; init; } = new();
    /// <summary>Frames per second for NDI meter video (1–30).</summary>
    public int NdiMeterFps { get; init; } = 10;

    public static readonly int[] AllowedSampleRates = [44100, 48000, 88200, 96000];
}

public sealed class NodePaths
{
    public string DataDir { get; }
    public string ConfigFile => Path.Combine(DataDir, "node.json");
    public string CalibrationDir => Path.Combine(DataDir, "calibration");

    public NodePaths(IConfiguration config)
    {
        DataDir = config["DataDir"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RtaApp");
        Directory.CreateDirectory(DataDir);
    }
}

public sealed class NodeConfigStore(NodePaths paths)
{
    private readonly object _lock = new();

    public NodeConfig Load()
    {
        lock (_lock)
        {
            NodeConfig cfg;
            if (!File.Exists(paths.ConfigFile)) cfg = new NodeConfig();
            else
            {
                try { cfg = JsonSerializer.Deserialize<NodeConfig>(File.ReadAllText(paths.ConfigFile), CalibrationStore.Json) ?? new NodeConfig(); }
                catch (JsonException) { cfg = new NodeConfig(); }
            }
            if (string.IsNullOrWhiteSpace(cfg.NodeId))
            {
                cfg = cfg with { NodeId = Guid.NewGuid().ToString("N") };
                SaveLocked(cfg);
            }
            return cfg;
        }
    }

    public void Save(NodeConfig config)
    {
        lock (_lock) SaveLocked(config);
    }

    private void SaveLocked(NodeConfig config)
    {
        var tmp = paths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(config, CalibrationStore.Json));
        File.Move(tmp, paths.ConfigFile, overwrite: true);
    }
}
