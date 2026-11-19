using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using Rta.Core.Protocol;

namespace Rta.App.Node;

public enum HubLinkState { Off, Searching, Connecting, Pending, Connected, Rejected, Error }

public sealed record HubLinkStatus(
    HubLinkState State,
    string? HubName,
    string? HubUrl,
    long UnsentRecords,
    string? Message,
    IReadOnlyList<DiscoveredHub> Discovered);

public sealed record DiscoveredHub(string HubId, string Name, string Url, DateTimeOffset LastSeen);

/// <summary>
/// Sends this node's live readings and logged records to the Hub. Finds the Hub by UDP beacon
/// (or a fixed address), buffers every record locally, and resends whatever the Hub has not
/// acknowledged after a reconnect.
/// </summary>
public sealed class HubClient(MeteringService metering, NodeBuffer buffer, AppMode mode, ILogger<HubClient> log) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DiscoveredHub> _discovered = new();
    private readonly SemaphoreSlim _wake = new(0);

    /// <summary>How often live readings go to the Hub (the node page also updates 10×/s).</summary>
    public const int LiveIntervalMs = 100;
    private volatile HubLinkStatus _status = new(HubLinkState.Off, null, null, 0, null, []);
    private long _ackSeq;

    public HubLinkStatus Status => _status with
    {
        UnsentRecords = buffer.PendingAfter(Interlocked.Read(ref _ackSeq)),
        Discovered = _discovered.Values.Where(h => DateTimeOffset.UtcNow - h.LastSeen < TimeSpan.FromSeconds(15)).ToList(),
    };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        metering.RecordReady += r =>
        {
            try { buffer.Append(r); _wake.Release(); }
            catch (Exception ex) { log.LogWarning(ex, "Could not buffer record"); }
        };
        _ = Task.Run(() => ListenForBeacons(ct), ct);
        _ = Task.Run(() => PruneLoop(ct), ct);

        while (!ct.IsCancellationRequested)
        {
            var cfg = metering.Config;
            string? url = null;
            if (cfg.HubConnection == HubConnectionMode.Off)
            {
                Set(HubLinkState.Off, null, null, "Not sending to a Hub (Settings → Hub).");
            }
            else if (cfg.HubConnection == HubConnectionMode.Manual)
            {
                url = NormaliseUrl(cfg.HubAddress);
                if (url is null) Set(HubLinkState.Error, null, null, "Enter the Hub address in Settings (e.g. 192.168.1.20:5180).");
            }
            else if (mode.RunsHub)
            {
                url = $"ws://127.0.0.1:{mode.Port}{Proto.NodeSocketPath}";
            }
            else
            {
                var hub = _discovered.Values.Where(h => DateTimeOffset.UtcNow - h.LastSeen < TimeSpan.FromSeconds(15))
                    .OrderByDescending(h => h.LastSeen).FirstOrDefault();
                url = hub?.Url;
                if (url is null) Set(HubLinkState.Searching, null, null, "Looking for a Hub on the network…");
            }

            if (url is not null)
            {
                try { await RunConnection(url, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Set(HubLinkState.Error, _status.HubName, url, $"Hub connection failed: {ex.Message}");
                    log.LogDebug(ex, "Hub connection to {Url} failed", url);
                }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private async Task RunConnection(string url, CancellationToken ct)
    {
        Set(HubLinkState.Connecting, null, url, "Connecting…");
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(TimeSpan.FromSeconds(8));
        await ws.ConnectAsync(new Uri(url), connectCts.Token);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var sendLock = new SemaphoreSlim(1);
        async Task Send(byte[] data)
        {
            await sendLock.WaitAsync(linked.Token);
            try { await ws.SendAsync(data, WebSocketMessageType.Text, true, linked.Token); }
            finally { sendLock.Release(); }
        }

        var cfgAtStart = metering.Config;
        string micHash = "";
        async Task SendHello()
        {
            var mics = metering.MicInfos();
            micHash = JsonSerializer.Serialize(mics, Proto.Json) + metering.Config.NodeName;
            var st = metering.Status();
            await Send(Proto.Encode("hello", new Hello(cfgAtStart.NodeId, metering.Config.NodeName, Proto.Version, AppMode.Version,
                buffer.BufferId, buffer.LastSeq, st.SampleRate, st.BandsHz, mics)));
        }
        await SendHello();

        var accepted = false;
        long sentUpTo = 0;
        var lastProgress = DateTimeOffset.UtcNow;
        var hubName = (string?)null;

        var receiver = Task.Run(async () =>
        {
            var buf = new byte[64 * 1024];
            while (ws.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(buf, linked.Token);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);

                var (type, payload) = Proto.Decode(ms.ToArray());
                switch (type)
                {
                    case "welcome":
                        var w = Proto.Read<Welcome>(payload);
                        hubName = w.HubName;
                        accepted = w.Acceptance == NodeAcceptance.Accepted;
                        Interlocked.Exchange(ref _ackSeq, w.AckSeq);
                        sentUpTo = w.AckSeq;
                        Set(w.Acceptance switch
                        {
                            NodeAcceptance.Accepted => HubLinkState.Connected,
                            NodeAcceptance.Rejected => HubLinkState.Rejected,
                            _ => HubLinkState.Pending,
                        }, w.HubName, url, w.Message ?? w.Acceptance switch
                        {
                            NodeAcceptance.Accepted => $"Connected to {w.HubName}.",
                            NodeAcceptance.Rejected => $"{w.HubName} has rejected this node.",
                            _ => $"Waiting for an admin to accept this node on {w.HubName}.",
                        });
                        _wake.Release();
                        break;
                    case "ack":
                        var a = Proto.Read<Ack>(payload);
                        Interlocked.Exchange(ref _ackSeq, a.Seq);
                        lastProgress = DateTimeOffset.UtcNow;
                        _wake.Release();
                        break;
                }
            }
        }, linked.Token);

        try
        {
            var nextLive = DateTimeOffset.MinValue;
            while (ws.State == WebSocketState.Open && !receiver.IsCompleted)
            {
                if (metering.Config.HubConnection != cfgAtStart.HubConnection || metering.Config.HubAddress != cfgAtStart.HubAddress)
                    break; // settings changed: reconnect

                var now = DateTimeOffset.UtcNow;
                if (now >= nextLive)
                {
                    await Send(Proto.Encode("live", metering.BuildLive()));
                    var hash = JsonSerializer.Serialize(metering.MicInfos(), Proto.Json) + metering.Config.NodeName;
                    if (hash != micHash) await SendHello();
                    nextLive = now.AddMilliseconds(LiveIntervalMs);
                }

                if (accepted)
                {
                    var ack = Interlocked.Read(ref _ackSeq);
                    if (sentUpTo > ack && now - lastProgress > TimeSpan.FromSeconds(10)) sentUpTo = ack; // resend unacked
                    if (sentUpTo - ack < 2000)
                    {
                        var batch = buffer.ReadAfter(sentUpTo, 300);
                        if (batch.Count > 0)
                        {
                            var wasIdle = sentUpTo <= ack;
                            await Send(Proto.Encode("records", new RecordBatch(batch)));
                            sentUpTo = batch[^1].Seq;
                            if (wasIdle) lastProgress = now; // start the resend timer from the first unacked send
                        }
                    }
                }

                var untilLive = nextLive - DateTimeOffset.UtcNow;
                await _wake.WaitAsync(untilLive > TimeSpan.Zero ? untilLive : TimeSpan.Zero, ct);
            }
        }
        finally
        {
            linked.Cancel();
            if (ws.State == WebSocketState.Open)
                try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { /* ignore */ }
        }
        Set(HubLinkState.Connecting, hubName, url, "Connection to the Hub was lost; retrying…");
    }

    private async Task ListenForBeacons(CancellationToken ct)
    {
        UdpClient? udp = null;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                udp ??= CreateListener();
                var res = await udp.ReceiveAsync(ct);
                var beacon = JsonSerializer.Deserialize<HubBeacon>(res.Buffer, Proto.Json);
                if (beacon is null || beacon.App != Proto.BeaconApp || beacon.V != Proto.Version) continue;
                var url = $"ws://{res.RemoteEndPoint.Address}:{beacon.Port}{Proto.NodeSocketPath}";
                _discovered[beacon.HubId] = new DiscoveredHub(beacon.HubId, beacon.Name, url, DateTimeOffset.UtcNow);
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

    private static UdpClient CreateListener()
    {
        var udp = new UdpClient { ExclusiveAddressUse = false };
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, Proto.BeaconPort));
        return udp;
    }

    private async Task PruneLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var n = buffer.Prune();
                if (n > 0) log.LogInformation("Dropped {Count} buffered records older than {Days} days", n, NodeBuffer.Retention.TotalDays);
                await Task.Delay(TimeSpan.FromHours(1), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Buffer prune failed"); }
        }
    }

    private void Set(HubLinkState state, string? hubName, string? url, string? message) =>
        _status = _status with { State = state, HubName = hubName, HubUrl = url, Message = message };

    /// <summary>Accepts "host", "host:port", "http://host:port" or "ws://host:port/…" and returns the node socket URL.</summary>
    public static string? NormaliseUrl(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        var a = address.Trim();
        if (!a.Contains("://")) a = "ws://" + a;
        if (!Uri.TryCreate(a, UriKind.Absolute, out var u)) return null;
        var scheme = u.Scheme is "https" or "wss" ? "wss" : "ws";
        var port = u.IsDefaultPort && !address.Contains(':') ? 5180 : u.Port;
        return $"{scheme}://{u.Host}:{port}{Proto.NodeSocketPath}";
    }
}
