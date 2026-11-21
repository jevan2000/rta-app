using Rta.App.Node;
using Rta.Core;
using Rta.Core.Calibration;

namespace Rta.App;

public enum StartupPage { Off, Dashboard, Node }

/// <summary>Settings for this PC itself, whatever its role (stored in pc.json).</summary>
public sealed record PcSettings
{
    /// <summary>Page the status icon opens in the default browser when someone signs in and RTA is ready.</summary>
    public StartupPage OpenAtStartup { get; init; } = StartupPage.Off;
}

public sealed class PcSettingsStore(NodePaths paths, AppMode mode)
{
    private readonly object _lock = new();
    private string File => Path.Combine(paths.DataDir, "pc.json");

    public PcSettings Current
    {
        // A monitor PC opens the dashboard at sign-in unless told otherwise.
        get { lock (_lock) return SafeFile.ReadJson<PcSettings>(File, CalibrationStore.Json) ?? new() { OpenAtStartup = mode.IsMonitor ? StartupPage.Dashboard : StartupPage.Off }; }
    }

    public PcSettings Save(PcSettings s)
    {
        if (s.OpenAtStartup == StartupPage.Dashboard && !mode.ServesDashboard)
            throw new ArgumentException("This PC doesn't run the Hub, so there's no dashboard to open. Choose the node page.");
        if (s.OpenAtStartup == StartupPage.Node && !mode.RunsNode)
            throw new ArgumentException("This PC doesn't run a node. Choose the dashboard.");
        lock (_lock) SafeFile.WriteJson(File, s, CalibrationStore.Json);
        return s;
    }

    /// <summary>The page to open, or null; falls back if the role changed since it was chosen.</summary>
    public string? StartupPath() => Current.OpenAtStartup switch
    {
        StartupPage.Dashboard => mode.ServesDashboard ? "/" : "/node/",
        StartupPage.Node => mode.RunsNode ? "/node/" : "/",
        _ => null,
    };
}
