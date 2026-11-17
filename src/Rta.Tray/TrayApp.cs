using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rta.Tray;

/// <summary>
/// Notification-area icon for the RTA app. Shows status (green / amber / red / grey), opens the
/// dashboard and node page, and pops up a notification when an alarm starts. In "sign-in" mode it
/// also starts RtaApp.exe (hidden) and restarts it if it stops; in "service" mode the Windows
/// service runs the app and the tray only watches it.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private const string ServiceName = "RtaApp";
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RtaApp");

    private readonly NotifyIcon _icon = new() { Visible = true, Text = "RTA: starting…" };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly ToolStripMenuItem _statusItem = new("Starting…") { Enabled = false };
    private readonly ToolStripMenuItem _dashboardItem, _nodeItem, _restartItem;
    private readonly string _runAs;
    private readonly int _port;
    private Process? _child;
    private DateTime _unreachableSince = DateTime.MinValue;
    private DateTime _nextStart = DateTime.MinValue;
    private int _restarts;
    private HashSet<string> _alarms = [];
    private bool _busy;

    public TrayApp()
    {
        (_runAs, _port) = ReadInstallSettings();
        _dashboardItem = new ToolStripMenuItem("Open dashboard", null, (_, _) => Open("/"));
        _nodeItem = new ToolStripMenuItem("Open this PC's node page", null, (_, _) => Open("/node/"));
        _restartItem = new ToolStripMenuItem(_runAs == "service" ? "Restart the RTA service…" : "Restart RTA", null, (_, _) => Restart());
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([
            _statusItem, new ToolStripSeparator(), _dashboardItem, _nodeItem, new ToolStripSeparator(), _restartItem,
            new ToolStripMenuItem("Open log folder", null, (_, _) => OpenFolder(Path.Combine(DataDir, "logs"))),
            new ToolStripMenuItem("Open data folder", null, (_, _) => OpenFolder(DataDir)),
            new ToolStripSeparator(),
            new ToolStripMenuItem(_runAs == "service" ? "Hide this icon" : "Exit (stops RTA)", null, (_, _) => Quit()),
        ]);
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => Open(_dashboardItem.Visible ? "/" : "/node/");
        SetIcon(Color.Gray, "RTA: starting…");
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();
        _ = PollAsync();
    }

    private static (string RunAs, int Port) ReadInstallSettings()
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(DataDir, "app.json")));
            var runAs = (json?["RunAs"]?.ToString() ?? "signin").ToLowerInvariant();
            var urls = json?["Urls"]?.ToString() ?? "http://localhost:5180";
            var port = Uri.TryCreate(urls.Split(';')[0].Replace("0.0.0.0", "localhost").Replace("*", "localhost").Replace("+", "localhost"), UriKind.Absolute, out var u) ? u.Port : 5180;
            return (runAs == "service" ? "service" : "signin", port);
        }
        catch { return ("signin", 5180); }
    }

    private string Base => $"http://localhost:{_port}";

    private async Task PollAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            JsonObject? app = null;
            try { app = await _http.GetFromJsonAsync<JsonObject>($"{Base}/api/app"); } catch { /* not running */ }
            if (app is null)
            {
                if (_unreachableSince == DateTime.MinValue) _unreachableSince = DateTime.Now;
                if (_runAs == "signin") EnsureChildRunning();
                var since = DateTime.Now - _unreachableSince;
                SetIcon(since < TimeSpan.FromSeconds(20) ? Color.Gray : Color.FromArgb(220, 60, 60),
                    _runAs == "service" ? $"RTA: not responding (is the '{ServiceName}' service running?)" : "RTA: starting…");
                return;
            }
            _unreachableSince = DateTime.MinValue;
            _restarts = 0;
            var runsHub = app["runsHub"]?.GetValue<bool>() == true;
            var runsNode = app["runsNode"]?.GetValue<bool>() == true;
            _dashboardItem.Visible = runsHub;
            _nodeItem.Visible = runsNode;

            var parts = new List<string> { $"RTA {app["role"]}" };
            var color = Color.FromArgb(60, 200, 130);
            if (runsNode)
            {
                var st = await _http.GetFromJsonAsync<JsonObject>($"{Base}/api/status");
                if (st?["running"]?.GetValue<bool>() != true) { color = Color.FromArgb(220, 60, 60); parts.Add(st?["error"]?.ToString() ?? "audio stopped"); }
                else parts.Add($"{st["deviceName"]}");
                if (st?["warnings"] is JsonArray { Count: > 0 } && color != Color.FromArgb(220, 60, 60)) color = Color.FromArgb(240, 180, 60);
            }
            if (runsHub)
            {
                var live = await _http.GetFromJsonAsync<JsonObject>($"{Base}/api/hub/live");
                var alerts = live?["alerts"] as JsonArray ?? [];
                var mics = (live?["mics"] as JsonArray)?.Count ?? 0;
                var labels = new Dictionary<string, string>();
                foreach (var m in live?["mics"] as JsonArray ?? []) labels[m!["key"]!.ToString()] = m["label"]!.ToString();
                foreach (var z in live?["zones"] as JsonArray ?? []) labels[z!["key"]!.ToString()] = z["name"]!.ToString();
                parts.Add($"{mics} mic{(mics == 1 ? "" : "s")}");
                if (live?["service"] is JsonObject svc) parts.Add($"service: {svc["name"]}");
                if (alerts.Count > 0)
                {
                    parts.Add($"{alerts.Count} alert{(alerts.Count == 1 ? "" : "s")}");
                    color = alerts.Any(a => a?["level"]?.ToString() == "alarm") ? Color.FromArgb(220, 60, 60) : Color.FromArgb(240, 180, 60);
                }
                var alarms = alerts.Where(a => a?["level"]?.ToString() == "alarm")
                    .Select(a => $"{labels.GetValueOrDefault(a!["micKey"]!.ToString(), a["micKey"]!.ToString())} {a["value"]} (limit {a["threshold"]})").ToHashSet();
                var fresh = alarms.Except(_alarms).ToList();
                if (fresh.Count > 0) _icon.ShowBalloonTip(8000, "Level alarm", string.Join("\n", fresh), ToolTipIcon.Warning);
                _alarms = alarms;
            }
            SetIcon(color, string.Join(" · ", parts));
        }
        catch (Exception ex) { SetIcon(Color.FromArgb(240, 180, 60), "RTA: " + ex.Message); }
        finally { _busy = false; }
    }

    private void EnsureChildRunning()
    {
        if (_child is { HasExited: false } || DateTime.Now < _nextStart) return;
        var exe = Path.Combine(AppContext.BaseDirectory, "RtaApp.exe");
        if (!File.Exists(exe)) { SetIcon(Color.FromArgb(220, 60, 60), "RTA: RtaApp.exe not found next to the tray app."); return; }
        try
        {
            _child = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = AppContext.BaseDirectory,
            });
            _restarts++;
            // Back off if it keeps dying (e.g. the port is taken by another copy).
            _nextStart = DateTime.Now.AddSeconds(Math.Min(60, 5 * _restarts));
        }
        catch (Exception ex) { SetIcon(Color.FromArgb(220, 60, 60), "RTA: couldn't start (" + ex.Message + ")"); }
    }

    private void Restart()
    {
        if (_runAs == "service")
        {
            // Needs admin: Windows asks for permission.
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c net stop {ServiceName} & net start {ServiceName}")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (System.ComponentModel.Win32Exception) { /* user said no */ }
            return;
        }
        StopChild();
        _nextStart = DateTime.MinValue;
        _restarts = 0;
        EnsureChildRunning();
    }

    private void StopChild()
    {
        try { if (_child is { HasExited: false }) { _child.Kill(entireProcessTree: true); _child.WaitForExit(5000); } }
        catch { /* already gone */ }
        _child = null;
    }

    private void Quit()
    {
        _timer.Stop();
        if (_runAs == "signin") StopChild();
        _icon.Visible = false;
        ExitThread();
    }

    private void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo($"{Base}{path}") { UseShellExecute = true }); } catch { /* no browser */ }
    }

    private static void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private Color _lastColor = Color.Empty;

    private void SetIcon(Color c, string text)
    {
        _statusItem.Text = text.Length > 120 ? text[..120] + "…" : text;
        _icon.Text = text.Length > 63 ? text[..62] + "…" : text; // tooltip limit
        if (c == _lastColor) return;
        _lastColor = c;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(11, 22, 38));
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var ring = new Pen(c, 3);
            g.DrawEllipse(ring, 3, 3, 26, 26);
            // three meter bars
            using var bar = new SolidBrush(c);
            g.FillRectangle(bar, 9, 17, 4, 7);
            g.FillRectangle(bar, 14, 12, 4, 12);
            g.FillRectangle(bar, 19, 8, 4, 16);
        }
        var h = bmp.GetHicon();
        var old = _icon.Icon;
        _icon.Icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        old?.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _timer.Dispose(); _icon.Dispose(); _http.Dispose(); }
        base.Dispose(disposing);
    }
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var single = new Mutex(true, @"Local\RtaTray", out var first);
        if (!first) return; // already running for this user
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}
