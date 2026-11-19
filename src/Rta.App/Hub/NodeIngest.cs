using System.Net;
using System.Net.WebSockets;
using System.Threading.Channels;
using Rta.Core.Protocol;

namespace Rta.App.Hub;

/// <summary>Handles one node's WebSocket on the Hub: hello → welcome, live updates, record batches → ack.</summary>
public sealed class NodeIngest(HubStore store, HubState state, HubSettingsStore settings, AppMode mode, ZoneEngine zones, IServiceProvider services, ILogger<NodeIngest> log)
{
    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var ct = ctx.RequestAborted;
        var remote = ctx.Connection.RemoteIpAddress;
        var address = remote?.ToString();
        var sendLock = new SemaphoreSlim(1);
        async Task Send(byte[] data)
        {
            await sendLock.WaitAsync(ct);
            try { await ws.SendAsync(data, WebSocketMessageType.Text, true, ct); }
            finally { sendLock.Release(); }
        }

        NodeConnection? conn = null;
        var buf = new byte[256 * 1024];

        // Record batches are saved on their own task so a backlog (after a reconnect) or a busy
        // database never holds up the live readings arriving on this socket. The node keeps at most
        // ~2000 records unacknowledged, so this queue stays small.
        var batches = Channel.CreateUnbounded<(string NodeId, RecordBatch Batch)>(new UnboundedChannelOptions { SingleReader = true });
        var saver = Task.Run(async () =>
        {
            await foreach (var (nodeId, batch) in batches.Reader.ReadAllAsync(ct))
            {
                var ack = store.StoreRecords(nodeId, batch.Items);
                state.OnRecordsStored(nodeId, batch.Items);
                try { await Send(Proto.Encode("ack", new Ack(ack))); }
                catch (Exception) when (ws.State != WebSocketState.Open) { /* node left; it resends anything unacknowledged */ }
            }
        }, ct);

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult res;
                do
                {
                    res = await ws.ReceiveAsync(buf, ct);
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, res.Count);
                    if (ms.Length > 16 * 1024 * 1024) throw new InvalidDataException("Message too large");
                } while (!res.EndOfMessage);

                var (type, payload) = Proto.Decode(ms.ToArray());
                if (conn is not null) conn.LastMessage = DateTimeOffset.UtcNow;

                switch (type)
                {
                    case "hello":
                    {
                        var hello = Proto.Read<Hello>(payload);
                        if (hello.ProtocolVersion != Proto.Version)
                        {
                            await Send(Proto.Encode("welcome", new Welcome(settings.Current.HubId, settings.Current.Name, NodeAcceptance.Rejected, 0,
                                $"Protocol version {hello.ProtocolVersion} is not supported by this Hub (needs {Proto.Version}). Update the app.")));
                            return;
                        }
                        var isLocal = remote is not null && IPAddress.IsLoopback(remote) && IsLocalNode(hello.NodeId);
                        var initial = isLocal || settings.Current.AutoAcceptNodes ? NodeAcceptance.Accepted : NodeAcceptance.Pending;
                        var row = store.UpsertNode(hello, address, initial);
                        if (isLocal && row.Acceptance != NodeAcceptance.Accepted)
                        {
                            store.SetAcceptance(row.Id, NodeAcceptance.Accepted);
                            row = store.GetNode(hello.NodeId)!;
                        }
                        if (conn is null)
                        {
                            conn = new NodeConnection(hello.NodeId, address, Send);
                            state.Register(conn);
                            log.LogInformation("Node {Name} ({Id}) connected from {Address}: {Acceptance}", hello.NodeName, hello.NodeId, address, row.Acceptance);
                        }
                        conn.Hello = hello;
                        state.InvalidateMics();
                        await Send(Proto.Encode("welcome", new Welcome(settings.Current.HubId, settings.Current.Name, row.Acceptance, row.LastSeq, null)));
                        state.NotifyChanged();
                        break;
                    }
                    case "live":
                        if (conn is null) break;
                        conn.Live = Proto.Read<LiveUpdate>(payload);
                        if (state.IsAccepted(conn.NodeId)) zones.OnLive(conn.NodeId, conn.Live);
                        state.NotifyLive();
                        break;
                    case "records":
                    {
                        if (conn is null) break;
                        if (!state.IsAccepted(conn.NodeId)) break; // ignore until accepted
                        if (saver.IsFaulted) await saver; // surface a database failure and drop the connection
                        batches.Writer.TryWrite((conn.NodeId, Proto.Read<RecordBatch>(payload)));
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (Exception ex) { log.LogWarning(ex, "Node connection from {Address} failed", address); }
        finally
        {
            batches.Writer.TryComplete();
            try { await saver; }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (Exception ex) { log.LogWarning(ex, "Saving records from {Address} failed", address); }
            if (conn is not null)
            {
                state.Unregister(conn);
                store.TouchNode(conn.NodeId);
                state.NotifyChanged();
                log.LogInformation("Node {Id} disconnected", conn.NodeId);
            }
        }
    }

    private bool IsLocalNode(string nodeId)
    {
        if (!mode.RunsNode) return false;
        var metering = services.GetService<Node.MeteringService>();
        return metering?.Config.NodeId == nodeId;
    }

    /// <summary>Tells a connected node its new acceptance state (after an admin accepts or rejects it).</summary>
    public async Task NotifyAcceptanceAsync(string nodeId)
    {
        if (!state.Connections.TryGetValue(nodeId, out var c)) return;
        var row = store.GetNode(nodeId);
        if (row is null) return;
        try
        {
            await c.SendAsync(Proto.Encode("welcome", new Welcome(settings.Current.HubId, settings.Current.Name, row.Acceptance, row.LastSeq, null)));
        }
        catch (Exception ex) { log.LogDebug(ex, "Could not notify node {Id}", nodeId); }
    }
}
