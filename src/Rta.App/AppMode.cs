namespace Rta.App;

public enum AppRole { Node, Hub, Both }

/// <summary>Which parts of the app run in this process (from config "Mode"; default Both).</summary>
public sealed class AppMode
{
    public AppRole Role { get; }
    public bool RunsNode => Role is AppRole.Node or AppRole.Both;
    public bool RunsHub => Role is AppRole.Hub or AppRole.Both;
    /// <summary>Port this process listens on (for the local node to reach a local hub).</summary>
    public int Port { get; }

    public AppMode(AppRole role, int port)
    {
        Role = role;
        Port = port;
    }

    public static AppRole Parse(string? value) =>
        Enum.TryParse<AppRole>(value, ignoreCase: true, out var r) ? r : AppRole.Both;

    public static string Version => typeof(AppMode).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
