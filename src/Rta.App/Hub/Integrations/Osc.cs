using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace Rta.App.Hub.Integrations;

/// <summary>Minimal OSC 1.0 message encoder (int32, float32 and string arguments).</summary>
public static class Osc
{
    public static byte[] Message(string address, params object[] args)
    {
        using var ms = new MemoryStream();
        WriteString(ms, address);
        var tags = new StringBuilder(",");
        foreach (var a in args)
            tags.Append(a switch { int => 'i', float or double => 'f', string => 's', bool b => b ? 'T' : 'F', _ => throw new ArgumentException($"Unsupported OSC arg {a.GetType()}") });
        WriteString(ms, tags.ToString());
        Span<byte> buf = stackalloc byte[4];
        foreach (var a in args)
        {
            switch (a)
            {
                case int i: BinaryPrimitives.WriteInt32BigEndian(buf, i); ms.Write(buf); break;
                case float f: BinaryPrimitives.WriteSingleBigEndian(buf, f); ms.Write(buf); break;
                case double d: BinaryPrimitives.WriteSingleBigEndian(buf, (float)d); ms.Write(buf); break;
                case string s: WriteString(ms, s); break;
            }
        }
        return ms.ToArray();
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        s.Write(bytes);
        var pad = 4 - bytes.Length % 4; // always at least one NUL terminator
        for (var i = 0; i < pad; i++) s.WriteByte(0);
    }
}

/// <summary>Sends OSC messages to the configured targets over UDP.</summary>
public sealed class OscSender : IDisposable
{
    private readonly UdpClient _udp = new();

    public async Task SendAsync(IEnumerable<NetTarget> targets, IReadOnlyList<byte[]> messages, CancellationToken ct)
    {
        foreach (var t in targets)
            foreach (var m in messages)
                await _udp.SendAsync(m, t.Host, t.Port, ct);
    }

    /// <summary>Builds level messages: {prefix}/{mic|zone}/{slug}/{metric} f, and …/alert i (0 none, 1 warn, 2 alarm).</summary>
    public static List<byte[]> LevelMessages(string prefix, LevelsSnapshot s)
    {
        var list = new List<byte[]>();
        foreach (var src in s.Mics.Concat(s.Zones))
        {
            var basePath = $"{prefix}/{src.Kind}/{src.Slug}";
            foreach (var (name, v) in src.Values)
                if (v is double d) list.Add(Osc.Message($"{basePath}/{name}", (float)d));
            list.Add(Osc.Message($"{basePath}/alert", src.AlertLevel switch { "alarm" => 2, "warn" => 1, _ => 0 }));
        }
        list.Add(Osc.Message($"{prefix}/service/active", s.Service is null ? 0 : 1));
        return list;
    }

    public void Dispose() => _udp.Dispose();
}
