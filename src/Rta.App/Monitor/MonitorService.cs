using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
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
    string Message, IReadOnlyList<DiscoveredHubView> Discovered, DiscoveryHealth Discovery);

/// <summary>How automatic discovery is going, for troubleshooting on the monitor's Settings page.</summary>
public sealed record DiscoveryHealth(bool Listening, string? ListenError, int AnnouncementsLastMinute, long? LastAnnouncement,
    IReadOnlyList<string> OtherVersions, string? Hint);

/// <summary>The result of trying a Hub address (or one found by searching the network).</summary>
public sealed record ProbeResult(string Address, string Url, bool Ok, string? HubName, string? HubId, string? AppVersion, string? Error);

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
    private volatile bool _listening;
    private volatile string? _listenError;
    private readonly ConcurrentQueue<DateTimeOffset> _heard = new();
    private readonly ConcurrentDictionary<string, string> _otherVersions = new();

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
        return new MonitorStatus(s.HubConnection, s.HubAddress, hub, _hubName, connected, message, found, Health());
    }

    private DiscoveryHealth Health()
    {
        while (_heard.TryPeek(out var t) && DateTimeOffset.UtcNow - t > TimeSpan.FromMinutes(1)) _heard.TryDequeue(out _);
        var count = _heard.Count;
        var last = _heard.LastOrDefault();
        var others = _otherVersions.Select(kv => $"{kv.Key} ({kv.Value})").ToList();
        string? hint = null;
        if (!_listening)
            hint = $"This PC can't listen for Hub announcements (UDP port {Proto.BeaconPort}){(_listenError is null ? "" : ": " + _listenError)}. Use a fixed address or Search this network.";
        else if (others.Count > 0 && _found.IsEmpty)
            hint = "A Hub was heard, but it runs a different version of RTA. Install the same version on this PC and the Hub.";
        else if (count == 0)
            hint = $"No Hub announcements reach this PC. Usually that's Windows Firewall here (the network is set to Public, or RTA wasn't allowed through during setup), "
                 + "or the Hub is on another subnet/VLAN. Search this network, or enter the Hub's IP address.";
        return new DiscoveryHealth(_listening, _listenError, count, count > 0 ? last.ToUnixTimeSeconds() : null, others, hint);
    }

    // ---------- trying an address, searching the network ----------

    /// <summary>Checks that a Hub answers at this address (and that it is a Hub, not a node-only PC).</summary>
    public async Task<ProbeResult> Probe(string address, CancellationToken ct, TimeSpan? timeout = null)
    {
        var url = HubBase(address);
        if (url is null) return new ProbeResult(address, "", false, null, null, null, "That isn't an address. Use the Hub PC's IP, e.g. 192.168.1.20 or 192.168.1.20:5180.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(5));
        var client = http.CreateClient("monitor");
        try
        {
            var app = await client.GetFromJsonAsync<JsonElement>($"{url}/api/app", cts.Token);
            var version = app.TryGetProperty("version", out var v) ? v.GetString() : null;
            var runsHub = app.TryGetProperty("runsHub", out var rh) && rh.GetBoolean();
            if (!runsHub)
                return new ProbeResult(address, url, false, null, null, version, "RTA answers there, but that PC doesn't run the Hub (it's a node or a monitor). Use the Hub PC's address.");
            var live = await client.GetFromJsonAsync<JsonElement>($"{url}/api/hub/live", cts.Token);
            return new ProbeResult(address, url, true, live.GetProperty("hubName").GetString(), live.GetProperty("hubId").GetString(), version, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            var why = ex is TaskCanceledException or OperationCanceledException
                ? "no answer (is the Hub PC on, and does its firewall allow RTA on port " + new Uri(url).Port + "?)"
                : ex is HttpRequestException h && h.InnerException is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused
                    ? "nothing is listening on that port (check the port; the default is 5180)"
                    : ex.Message;
            return new ProbeResult(address, url, false, null, null, null, $"Can't reach a Hub at {url}: {why}.");
        }
    }

    /// <summary>
    /// The addresses to try on one network: the whole subnet when it has up to 1,024 addresses (a /22),
    /// otherwise the 1,024 around this PC (larger networks would take too long to sweep).
    /// </summary>
    public static IEnumerable<IPAddress> SearchRange(IPAddress self, int prefixLength)
    {
        var me = BitConverter.ToUInt32(self.GetAddressBytes().Reverse().ToArray());
        var prefix = Math.Clamp(prefixLength, 22, 30);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var network = me & mask;
        var broadcast = network | ~mask;
        for (var a = network + 1; a < broadcast; a++)
            if (a != me) yield return new IPAddress(BitConverter.GetBytes(a).Reverse().ToArray());
    }

    /// <summary>
    /// Looks for Hubs on this PC's own subnets by trying port 5180 on every address (see <see cref="SearchRange"/>). Works where the UDP announcements are blocked, since it only needs an outgoing connection.
    /// </summary>
    public async Task<IReadOnlyList<ProbeResult>> Search(CancellationToken ct)
    {
        var targets = new HashSet<string>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ua.Address)) continue;
                var b = ua.Address.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;   // no DHCP address: nothing to search
                foreach (var t in SearchRange(ua.Address, ua.PrefixLength)) targets.Add(t.ToString());
            }
        }
        var open = new ConcurrentBag<string>();
        using var gate = new SemaphoreSlim(64);
        await Task.WhenAll(targets.Select(async ip =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(600));
                await tcp.ConnectAsync(ip, 5180, cts.Token);
                open.Add(ip);
            }
            catch (Exception) { /* nothing there */ }
            finally { gate.Release(); }
        }));
        var results = await Task.WhenAll(open.Select(ip => Probe(ip, ct, TimeSpan.FromSeconds(4))));
        // Only PCs running RTA: a Hub, or RTA that isn't one (worth telling apart); other web servers on 5180 are dropped.
        // A Hub with two network connections answers on both: list it once.
        return results.Where(r => r.Ok || r.AppVersion is not null).OrderByDescending(r => r.Ok).ThenBy(r => r.Address)
            .DistinctBy(r => r.Ok ? r.HubId : r.Url).ToList();
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
                    _listening = true;
                    _listenError = null;
                }
                var res = await udp.ReceiveAsync(ct);
                var b = JsonSerializer.Deserialize<HubBeacon>(res.Buffer, Proto.Json);
                if (b is null || b.App != Proto.BeaconApp) continue;
                if (b.V != Proto.Version)
                {
                    _otherVersions[$"{b.Name} at {res.RemoteEndPoint.Address}"] = $"protocol {b.V}, this PC {Proto.Version}";
                    continue;
                }
                _heard.Enqueue(DateTimeOffset.UtcNow);
                _found[b.HubId] = new Found(b.HubId, b.Name, $"http://{res.RemoteEndPoint.Address}:{b.Port}", DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Beacon listener error");
                _listening = false;
                _listenError = ex.Message;
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
        app.MapGet("/api/monitor/probe", (string address, MonitorService m, CancellationToken ct) => m.Probe(address, ct));
        app.MapPost("/api/monitor/search", (MonitorService m, CancellationToken ct) => m.Search(ct));
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
