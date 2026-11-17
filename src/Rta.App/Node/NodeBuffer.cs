using System.Text.Json;
using Microsoft.Data.Sqlite;
using Rta.Core.Logging;
using Rta.Core.Protocol;

namespace Rta.App.Node;

/// <summary>
/// Local store-and-forward buffer of level records (SQLite). Every record gets a sequence number;
/// the Hub acknowledges the highest one it has stored, and anything newer is resent after a
/// reconnect. Records older than the retention period are dropped even if never sent.
/// </summary>
public sealed class NodeBuffer : IDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public string BufferId { get; }

    public NodeBuffer(NodePaths paths)
    {
        _db = new SqliteConnection($"Data Source={Path.Combine(paths.DataDir, "node-buffer.db")}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS records (seq INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, json TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS records_ts ON records(ts);
            """);
        BufferId = GetMeta("buffer_id") ?? SetMeta("buffer_id", Guid.NewGuid().ToString("N"));
    }

    public long Append(LevelRecord r)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "INSERT INTO records (ts, json) VALUES ($ts, $json); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$ts", r.Ts);
            cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(r, Proto.Json));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public long LastSeq
    {
        get
        {
            lock (_lock)
            {
                using var cmd = _db.CreateCommand();
                cmd.CommandText = "SELECT COALESCE(MAX(seq), 0) FROM records";
                return (long)cmd.ExecuteScalar()!;
            }
        }
    }

    /// <summary>Records with seq &gt; <paramref name="afterSeq"/>, oldest first.</summary>
    public List<SeqRecord> ReadAfter(long afterSeq, int max)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT seq, json FROM records WHERE seq > $s ORDER BY seq LIMIT $n";
            cmd.Parameters.AddWithValue("$s", afterSeq);
            cmd.Parameters.AddWithValue("$n", max);
            using var rd = cmd.ExecuteReader();
            var list = new List<SeqRecord>();
            while (rd.Read())
            {
                var rec = JsonSerializer.Deserialize<LevelRecord>(rd.GetString(1), Proto.Json);
                if (rec is not null) list.Add(new SeqRecord(rd.GetInt64(0), rec));
            }
            return list;
        }
    }

    /// <summary>Count of records not yet acknowledged.</summary>
    public long PendingAfter(long ackSeq)
    {
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM records WHERE seq > $s";
            cmd.Parameters.AddWithValue("$s", ackSeq);
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public int Prune()
    {
        var cutoff = DateTimeOffset.UtcNow.Subtract(Retention).ToUnixTimeSeconds();
        lock (_lock)
        {
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "DELETE FROM records WHERE ts < $c";
            cmd.Parameters.AddWithValue("$c", cutoff);
            return cmd.ExecuteNonQuery();
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private string? GetMeta(string key)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private string SetMeta(string key, string value)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
        return value;
    }

    public void Dispose() => _db.Dispose();
}
