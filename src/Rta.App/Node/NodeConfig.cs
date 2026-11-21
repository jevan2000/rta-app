using Rta.Core;
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

/// <summary>One audio interface on this node. Its inputs are numbered Slot·100 + channel (see <see cref="InputNumber"/>).</summary>
public sealed record DeviceConfig
{
    public int Slot { get; init; }
    public AudioBackend Backend { get; init; } = AudioBackend.TestSignal;
    public string DeviceId { get; init; } = TestSignalInput.DeviceId;
    /// <summary>
    /// The interface's name when last seen. Windows gives a WASAPI device a new id on another USB port,
    /// so this is how a moved interface is recognised (see <see cref="DeviceMatch"/>).
    /// </summary>
    public string? DeviceName { get; init; }

    /// <summary>Same interface choice (ignores the remembered name).</summary>
    public bool SameDevice(DeviceConfig o) => Slot == o.Slot && Backend == o.Backend && string.Equals(DeviceId, o.DeviceId, StringComparison.OrdinalIgnoreCase);
}

public sealed record NodeConfig
{
    /// <summary>Stable identity of this node (generated on first run).</summary>
    public string NodeId { get; init; } = "";
    public string NodeName { get; init; } = Environment.MachineName;
    /// <summary>How this node finds the Hub: auto-discover on the LAN, a fixed address, or not at all.</summary>
    public HubConnectionMode HubConnection { get; init; } = HubConnectionMode.Auto;
    /// <summary>For Manual: "host:port" or a URL, e.g. "192.168.1.20:5180".</summary>
    public string? HubAddress { get; init; }
    /// <summary>The first audio interface (kept in step with <see cref="Devices"/>[0] for settings files from 1.0.x).</summary>
    public AudioBackend Backend { get; init; } = AudioBackend.TestSignal;
    public string DeviceId { get; init; } = TestSignalInput.DeviceId;
    /// <summary>All audio interfaces, metered side by side. Settings from 1.0.x have none: the first is then Backend/DeviceId.</summary>
    public List<DeviceConfig> Devices { get; init; } = [];
    /// <summary>Sample rate for every interface, so all mics share the same 1/3-octave bands.</summary>
    public int SampleRate { get; init; } = 48000;
    public bool AutoStart { get; init; } = true;
    public List<InputConfig> Inputs { get; init; } = [];
    public Thresholds Thresholds { get; init; } = new();
    /// <summary>Frames per second for NDI meter video (1–30).</summary>
    public int NdiMeterFps { get; init; } = 10;

    public static readonly int[] AllowedSampleRates = [44100, 48000, 88200, 96000];

    /// <summary>Fills <see cref="Devices"/> from the single-device fields of older settings, and mirrors the first device back.</summary>
    public NodeConfig Normalized()
    {
        var devices = (Devices.Count > 0 ? Devices : [new DeviceConfig { Slot = 0, Backend = Backend, DeviceId = DeviceId }])
            .OrderBy(d => d.Slot).ToList();
        return this with { Devices = devices, Backend = devices[0].Backend, DeviceId = devices[0].DeviceId };
    }

    /// <summary>Problems that stop these devices being used together, or null.</summary>
    public string? DeviceProblem()
    {
        if (Devices.Count == 0) return "Choose at least one audio interface.";
        if (Devices.Count > InputNumber.MaxDevices) return $"Up to {InputNumber.MaxDevices} audio interfaces per node.";
        if (Devices.Any(d => d.Slot is < 0 or >= InputNumber.MaxDevices) || Devices.Select(d => d.Slot).Distinct().Count() != Devices.Count)
            return "Each audio interface needs its own slot.";
        if (Devices.GroupBy(d => (d.Backend, d.DeviceId.ToLowerInvariant())).Any(g => g.Count() > 1))
            return "The same audio interface is listed twice.";
        if (Devices.Count(d => d.Backend == AudioBackend.Asio) > 1)
            return "Only one interface can use ASIO: Windows allows one ASIO driver per program. Add the others through Windows audio (WASAPI).";
        return null;
    }
}

public sealed class NodePaths
{
    public string DataDir { get; }
    public string ConfigFile => Path.Combine(DataDir, "node.json");
    public string CalibrationDir => Path.Combine(DataDir, "calibration");

    public NodePaths(IConfiguration config)
    {
        // Always a full path: files served from it (reports, plan images, backups) must not be taken as web paths.
        DataDir = Path.GetFullPath(config["DataDir"] ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RtaApp"));
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
            var cfg = (SafeFile.ReadJson<NodeConfig>(paths.ConfigFile, CalibrationStore.Json) ?? new NodeConfig()).Normalized();
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
        SafeFile.WriteJson(paths.ConfigFile, config, CalibrationStore.Json);
    }
}
