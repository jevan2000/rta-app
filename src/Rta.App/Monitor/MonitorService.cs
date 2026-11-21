using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Rta.App.Node;
using Rta.Core;
using Rta.Core.Calibration;
using Rta.Core.Protocol;

namespace Rta.App.Monitor;

public sealed record MonitorSettings
{
    /// <summary>Find the Hub on the network (Auto) or use a fixed address (Manual).</summary>
    public HubConnectionMode HubConnection { get; init; } = HubConnectionMode.Auto;
    /// <summary>For Manual: "host:port" or a URL, e.g. "192.168.1.20:5180".</summary>
    public string? HubAddress { get; init; }
    /// <summary>With several Hubs on the network, the one last watched (kept when found again).</summary>
    public string? PreferredHubId { get; init; }
}

public sealed record DiscoveredHubView(string HubId, string Name, string Url);

public sealed record MonitorStatus(
    HubConnectionMode HubConnection, string? HubAddress, string? HubUrl, string? HubName, bool Connected,
    string Message, IReadOnlyList<DiscoveredHubView> Discovered);

/// <summary>
/// A monitor PC: no mics and no Hub of its own. It finds a Hub (UDP beacon, or a fixed address) and
/// serves that Hub's dashboard here, read-only: reads are passed through, and anything that would
/// change something is refused on this PC, whatever password is known.
/// </summary>
public sealed class MonitorService(NodePaths paths, IHttpClientFactory http, ILogger<MonitorService> log) : BackgroundService
{
    private sealed record Found(string HubId, string Name, string Url, DateTimeOffset LastSeen);

    private readonly ConcurrentDictionary<string, Found> _found = new();
    private readonly object _lock = new();
    private string File => Path.Combine(paths.DataDir, "monitor.json");
    private DateTimeOffset _lastOk = DateTimeOffset.MinValue;
    private string? _hubName;
    private string? _lastError;

    public MonitorSettings Settings
    {
        get { lock (_lock) return SafeFile.ReadJson<MonitorSettings>(File, CalibrationStore.Json) ?? new(); }
    }

    public MonitorSettings Save(MonitorSettings s)
    {
        if (s.HubConnection == HubConnectionMode.Manual && HubBase(s.HubAddress) is null)
            throw new ArgumentException("Enter the Hub's address, e.g. 192.168.1.20 or 192.168.1.20:5180.");
        if (s.HubConnection == HubConnectionMode.Off) throw new ArgumentException("A monitor PC needs a Hub to watch.");
        lock (_lock) SafeFile.WriteJson(File, s, CalibrationStore.Json);
        _hubName = null;
        return s;
    }

    /// <summary>"192.168.1.20" → "http://192.168.1.20:5180".</summary>
    public static string? HubBase(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var a = address.Trim();
        if (!a.Contains("://")) a = "http://" + a;
        if (!Uri.TryCreate(a, UriKind.Absolute, out var u) || u.Host.Length == 0) return null;
        var scheme = u.Scheme is "https" or "wss" ? "https" : "http";
        var port = u.IsDefaultPort && !address.Trim().Split("://").Last().Contains(':') ? 5180 : u.Port;
        return $"{scheme}://{u.Host}:{port}";
    }

    /// <summary>The Hub to watch now, or null.</summary>
    public string? CurrentHub()
    {
        var s = Settings;
        if (s.HubConnection == HubConnectionMode.Manual) return HubBase(s.HubAddress);
        var live = _found.Values.Where(f => DateTimeOffset.UtcNow - f.LastSeen < TimeSpan.FromSeconds(15)).ToList();
        var pick = live.FirstOrDefault(f => f.HubId == s.PreferredHubId) ?? live.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return pick?.Url;
    }

    public MonitorStatus Status()
    {
        var s = Settings;
        var hub = CurrentHub();
        var connected = hub is not null && DateTimeOffset.UtcNow - _lastOk < TimeSpan.FromSeconds(10);
        var message = hub is null
            ? s.HubConnection == HubConnectionMode.Manual ? "Enter the Hub's address." : "Looking for a Hub on the network…"
            : connected ? $"Watching {_hubName ?? hub}." : $"Can't reach the Hub at {hub}{(_lastError is null ? "" : ": " + _lastError)}.";
        var found = _found.Values.Where(f => DateTimeOffset.UtcNow - f.LastSeen < TimeSpan.FromSeconds(15))
            .Select(f => new DiscoveredHubView(f.HubId, f.Name, f.Url)).OrderBy(f => f.Name).ToList();
        return new MonitorStatus(s.HubConnection, s.HubAddress, hub, _hubName, connected, message, found);
    }

    public void MarkOk(string? hubName = null) { _lastOk = DateTimeOffset.UtcNow; _lastError = null; if (hubName is not null) _hubName = hubName; }
    public void MarkFailed(string error) => _lastError = error;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var listen = ListenForBeacons(ct);
        // Keep the Hub's name and the connection state fresh even when nobody has the dashboard open.
        var client = http.CreateClient("monitor");
        while (!ct.IsCancellationRequested)
        {
            if (CurrentHub() is { } hub)
            {
                try
                {
                    var app = await client.GetFromJsonAsync<JsonElement>($"{hub}/api/hub/live", ct);
                    MarkOk(app.TryGetProperty("hubName", out var n) ? n.GetString() : null);
                    if (Settings.HubConnection == HubConnectionMode.Auto && app.TryGetProperty("hubId", out var id) && id.GetString() is { } hubId && hubId != Settings.PreferredHubId)
                        lock (_lock) SafeFile.WriteJson(File, Settings with { PreferredHubId = hubId }, CalibrationStore.Json);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { MarkFailed(ex.Message); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
        }
        await listen;
    }

    private async Task ListenForBeacons(CancellationToken ct)
    {
        UdpClient? udp = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (udp is null)
                {
                    udp = new UdpClient { ExclusiveAddressUse = false };
                    udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    udp.Client.Bind(new IPEndPoint(IPAddress.Any, Proto.BeaconPort));
                }
                var res = await udp.ReceiveAsync(ct);
                var b = JsonSerializer.Deserialize<HubBeacon>(res.Buffer, Proto.Json);
                if (b is null || b.App != Proto.BeaconApp || b.V != Proto.Version) continue;
                _found[b.HubId] = new Found(b.HubId, b.Name, $"http://{res.RemoteEndPoint.Address}:{b.Port}", DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Beacon listener error");
                udp?.Dispose();
                udp = null;
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
        }
        udp?.Dispose();
    }
}

public static class MonitorEndpoints
{
    private const string ViewOnly = "This is a monitor PC: it can only view the Hub. Make changes on the Hub PC (or any PC that isn't a monitor).";

    public static void MapMonitorEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/monitor", (MonitorService m) => m.Status());
        app.MapPut("/api/monitor", (MonitorSettings s, MonitorService m) =>
        {
            try { return Results.Ok(m.Save(s with { PreferredHubId = s.PreferredHubId ?? m.Settings.PreferredHubId })); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });

        // Reads of the Hub's API, passed through.
        app.MapGet("/api/hub/{**rest}", (HttpContext ctx, MonitorService m, IHttpClientFactory http) => Forward(ctx, m, http));
        app.MapGet("/api/v1/{**rest}", (HttpContext ctx, MonitorService m, IHttpClientFactory http) => Forward(ctx, m, http));
        // Everything else that would change the Hub is refused here.
        app.MapMethods("/api/hub/{**rest}", ["POST", "PUT", "DELETE", "PATCH"], () => Results.Problem(ViewOnly, statusCode: 403));

        // The dashboard's live stream, relayed from the Hub.
        app.Map("/ws/hub", async (HttpContext ctx, MonitorService m) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            if (m.CurrentHub() is not { } hub) { ctx.Response.StatusCode = 503; return; }
            using var down = await ctx.WebSockets.AcceptWebSocketAsync();
            using var up = new ClientWebSocket();
            var ct = ctx.RequestAborted;
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connect.CancelAfter(TimeSpan.FromSeconds(8));
                await up.ConnectAsync(new Uri(hub.Replace("http", "ws") + "/ws/hub"), connect.Token);
                var buf = new byte[256 * 1024];
                using var ms = new MemoryStream();
                while (up.State == WebSocketState.Open && down.State == WebSocketState.Open)
                {
                    ms.SetLength(0);
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await up.ReceiveAsync(buf, ct);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        ms.Write(buf, 0, r.Count);
                    } while (!r.EndOfMessage);
                    m.MarkOk();
                    if (m.CurrentHub() != hub) return; // switched to another Hub: the dashboard reconnects to it
                    await down.SendAsync(ms.GetBuffer().AsMemory(0, (int)ms.Length), WebSocketMessageType.Text, true, ct);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException) { m.MarkFailed(ex.Message); }
            finally
            {
                if (down.State == WebSocketState.Open)
                    try { await down.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "hub", CancellationToken.None); } catch { /* gone */ }
            }
        });
    }

    private static async Task Forward(HttpContext ctx, MonitorService m, IHttpClientFactory http)
    {
        if (m.CurrentHub() is not { } hub)
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsJsonAsync(new { title = "No Hub", detail = m.Status().Message });
            return;
        }
        try
        {
            var client = http.CreateClient("monitor");
            using var resp = await client.GetAsync(hub + ctx.Request.Path + ctx.Request.QueryString, HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
            ctx.Response.StatusCode = (int)resp.StatusCode;
            if (resp.Content.Headers.ContentType is { } type) ctx.Response.ContentType = type.ToString();
            if (resp.Content.Headers.ContentDisposition is { } disp) ctx.Response.Headers.ContentDisposition = disp.ToString();
            await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
            if (resp.IsSuccessStatusCode) m.MarkOk();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            m.MarkFailed(ex.Message);
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsJsonAsync(new { title = "Hub unreachable", detail = $"Can't reach the Hub at {hub}: {ex.Message}" });
            }
        }
    }
}
