using System.Text.Json;
using Rta.Core.Calibration;
using Rta.Core.Logging;

namespace Rta.Core.Protocol;

/// <summary>
/// Node ↔ Hub protocol over one WebSocket (text frames, JSON envelopes).
/// Node → Hub: hello, live, records. Hub → Node: welcome, ack.
/// </summary>
public static class Proto
{
    public const int Version = 1;
    public const int BeaconPort = 5181;
    public const string BeaconApp = "rta-hub";
    public const string NodeSocketPath = "/ws/node";

    public static readonly JsonSerializerOptions Json = CalibrationStore.Json;

    public static byte[] Encode<T>(string type, T payload) =>
        JsonSerializer.SerializeToUtf8Bytes(new Envelope<T>(type, payload), Json);

    public static (string Type, JsonElement Payload) Decode(ReadOnlySpan<byte> data)
    {
        using var doc = JsonDocument.Parse(data.ToArray());
        var root = doc.RootElement;
        return (root.GetProperty("type").GetString() ?? "", root.GetProperty("payload").Clone());
    }

    public static T Read<T>(JsonElement e) => e.Deserialize<T>(Json) ?? throw new JsonException($"Empty {typeof(T).Name}");
}

public sealed record Envelope<T>(string Type, T Payload);

/// <summary>UDP broadcast sent by a Hub every few seconds so nodes can find it.</summary>
public sealed record HubBeacon(string App, int V, string HubId, string Name, int Port);

public sealed record MicInfo(
    int ChannelIndex,
    string Name,
    string? MicLabel,
    string? ProfileName,
    CalibrationQuality? Quality,
    double? UncertaintyDb,
    bool Calibrated);

public sealed record Hello(
    string NodeId,
    string NodeName,
    int ProtocolVersion,
    string AppVersion,
    /// <summary>Identifies the node's local buffer; if it changes, the Hub restarts acks from 0.</summary>
    string BufferId,
    long LastSeq,
    double SampleRate,
    IReadOnlyList<double> BandsHz,
    IReadOnlyList<MicInfo> Mics);

public enum NodeAcceptance { Pending, Accepted, Rejected }

public sealed record Welcome(string HubId, string HubName, NodeAcceptance Acceptance, long AckSeq, string? Message);

public sealed record Ack(long Seq);

/// <summary>Compact live readings for one input (sent ~5×/s).</summary>
public sealed record LiveMic(
    int Ch,
    double LAS, double LCS, double LAF, double LCF,
    double LAeq1m, double LCeq1m, double LAeq15m, double LCeq15m,
    double LCpeakMax1m, double CMinusA,
    double Coverage1m,
    bool Calibrated,
    bool Clipped,
    double[] Bands,
    IReadOnlyList<string> Warnings);

public sealed record LiveUpdate(long T, bool Running, string? Error, IReadOnlyList<LiveMic> Mics, IReadOnlyList<string> NodeWarnings);

public sealed record SeqRecord(long Seq, LevelRecord Record);

public sealed record RecordBatch(IReadOnlyList<SeqRecord> Items);
