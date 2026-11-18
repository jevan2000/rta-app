using Microsoft.Data.Sqlite;
using Rta.App.Node;
using Rta.Core.Calibration;
using Rta.Core.Logging;
using Rta.Core.Protocol;

namespace Rta.App.Hub;

public sealed record NodeRow(string Id, string Name, NodeAcceptance Acceptance, string? BufferId, long LastSeq, long FirstSeen, long LastSeen, string? Address, string? Version);

public sealed record MicRow(
    int Id, string Key, string NodeId, int ChannelIndex, string Name, string? DisplayName, string? MicLabel,
    string? ProfileName, CalibrationQuality? Quality, double? UncertaintyDb, bool Calibrated, bool Hidden, long LastSeen)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;
}

public sealed record ServiceRow(long Id, string Name, long StartTs, long? EndTs, string Source, long? ScheduleId);

public sealed record ScheduleRow(long Id, string Name, int Day, int StartMinute, int DurationMinutes, bool Enabled);

public sealed record AlertEventRow(long Id, long RuleId, string RuleName, string MicKey, string Level, long StartTs, long? EndTs, double PeakValue, string Metric);

/// <summary>Columns of a level series. Values are dB; missing seconds are simply absent.</summary>
public sealed record Series(string Mic, string Resolution, long[] Ts, double[] LA, double[] LC, double[] LZ, double[] LCpeak, double[] LCSmax, double[] Coverage);

/// <summary>
/// The Hub's SQLite database. Levels are stored as integer centi-dB (dB×100) to keep a year of
/// per-second rows compact; seconds are keyed by a numeric mic id.
/// </summary>
public sealed partial class HubStore : IDisposable
{
    private readonly SqliteConnection _db;
    private readonly object _lock = new();

    public HubStore(NodePaths paths)
    {
        _db = new SqliteConnection($"Data Source={Path.Combine(paths.DataDir, "hub.db")}");
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Exec("""
            CREATE TABLE IF NOT EXISTS nodes (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, acceptance TEXT NOT NULL, buffer_id TEXT, last_seq INTEGER NOT NULL DEFAULT 0,
                first_seen INTEGER NOT NULL, last_seen INTEGER NOT NULL, address TEXT, version TEXT);
            CREATE TABLE IF NOT EXISTS mics (
                id INTEGER PRIMARY KEY, key TEXT NOT NULL UNIQUE, node_id TEXT NOT NULL, channel INTEGER NOT NULL,
                name TEXT NOT NULL, display_name TEXT, mic_label TEXT, profile_name TEXT, quality TEXT, uncertainty REAL,
                calibrated INTEGER NOT NULL DEFAULT 0, hidden INTEGER NOT NULL DEFAULT 0, last_seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS seconds (
                mic INTEGER NOT NULL, ts INTEGER NOT NULL, la INTEGER, lc INTEGER, lz INTEGER, lcpk INTEGER, lasmax INTEGER, lcsmax INTEGER,
                cov INTEGER, flags INTEGER NOT NULL, PRIMARY KEY (mic, ts)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS minutes (
                mic INTEGER NOT NULL, ts INTEGER NOT NULL, la INTEGER, lc INTEGER, lz INTEGER, lcpk INTEGER, lasmax INTEGER, lcsmax INTEGER,
                cov INTEGER, flags INTEGER NOT NULL, bands BLOB, PRIMARY KEY (mic, ts)) WITHOUT ROWID;
            CREATE TABLE IF NOT EXISTS services (
                id INTEGER PRIMARY KEY, name TEXT NOT NULL, start_ts INTEGER NOT NULL, end_ts INTEGER, source TEXT NOT NULL, schedule_id INTEGER);
            CREATE INDEX IF NOT EXISTS services_start ON services(start_ts);
            CREATE TABLE IF NOT EXISTS schedule (
                id INTEGER PRIMARY KEY, name TEXT NOT NULL, day INTEGER NOT NULL, start_minute INTEGER NOT NULL,
                duration_minutes INTEGER NOT NULL, enabled INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE IF NOT EXISTS alert_events (
                id INTEGER PRIMARY KEY, rule_id INTEGER NOT NULL, rule_name TEXT NOT NULL, mic_key TEXT NOT NULL, level TEXT NOT NULL,
                start_ts INTEGER NOT NULL, end_ts INTEGER, peak REAL NOT NULL, metric TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS alert_events_start ON alert_events(start_ts);
            """);
    }

    // ---------- nodes ----------

    public NodeRow? GetNode(string id)
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id,name,acceptance,buffer_id,last_seq,first_seen,last_seen,address,version FROM nodes WHERE id=$id", ("$id", id));
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadNode(r) : null;
        }
    }

    public List<NodeRow> ListNodes()
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id,name,acceptance,buffer_id,last_seq,first_seen,last_seen,address,version FROM nodes ORDER BY name");
            using var r = cmd.ExecuteReader();
            var list = new List<NodeRow>();
            while (r.Read()) list.Add(ReadNode(r));
            return list;
        }
    }

    private static NodeRow ReadNode(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), Enum.Parse<NodeAcceptance>(r.GetString(2)), r.IsDBNull(3) ? null : r.GetString(3),
        r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8));

    /// <summary>Registers or updates a node on hello. Returns the stored row (acceptance and ack position).</summary>
    public NodeRow UpsertNode(Hello h, string? address, NodeAcceptance initial)
    {
        var now = Now();
        lock (_lock)
        {
            using (var cmd = Cmd("""
                INSERT INTO nodes (id,name,acceptance,buffer_id,last_seq,first_seen,last_seen,address,version)
                VALUES ($id,$name,$acc,$buf,0,$now,$now,$addr,$ver)
                ON CONFLICT(id) DO UPDATE SET name=$name, last_seen=$now, address=$addr, version=$ver,
                    last_seq = CASE WHEN buffer_id IS $buf THEN last_seq ELSE 0 END, buffer_id=$buf
                """, ("$id", h.NodeId), ("$name", h.NodeName), ("$acc", initial.ToString()), ("$buf", h.BufferId),
                ("$now", now), ("$addr", (object?)address ?? DBNull.Value), ("$ver", h.AppVersion)))
                cmd.ExecuteNonQuery();

            foreach (var m in h.Mics) UpsertMicLocked(h.NodeId, m, now);
        }
        return GetNode(h.NodeId)!;
    }

    public void SetAcceptance(string nodeId, NodeAcceptance a)
    {
        lock (_lock) using (var cmd = Cmd("UPDATE nodes SET acceptance=$a WHERE id=$id", ("$a", a.ToString()), ("$id", nodeId))) cmd.ExecuteNonQuery();
    }

    public void RenameNode(string nodeId, string name)
    {
        lock (_lock) using (var cmd = Cmd("UPDATE nodes SET name=$n WHERE id=$id", ("$n", name), ("$id", nodeId))) cmd.ExecuteNonQuery();
    }

    /// <summary>Removes a node from the list. Its logged history is kept.</summary>
    public void ForgetNode(string nodeId)
    {
        lock (_lock)
        {
            using (var cmd = Cmd("DELETE FROM nodes WHERE id=$id", ("$id", nodeId))) cmd.ExecuteNonQuery();
            // hidden=2 means "hidden because its node was removed"; it clears if the node comes back.
            using (var cmd = Cmd("UPDATE mics SET hidden=2 WHERE node_id=$id AND hidden=0", ("$id", nodeId))) cmd.ExecuteNonQuery();
        }
    }

    public void TouchNode(string nodeId)
    {
        lock (_lock) using (var cmd = Cmd("UPDATE nodes SET last_seen=$t WHERE id=$id", ("$t", Now()), ("$id", nodeId))) cmd.ExecuteNonQuery();
    }

    // ---------- mics ----------

    private void UpsertMicLocked(string nodeId, MicInfo m, long now)
    {
        using var cmd = Cmd("""
            INSERT INTO mics (key,node_id,channel,name,mic_label,profile_name,quality,uncertainty,calibrated,hidden,last_seen)
            VALUES ($key,$node,$ch,$name,$label,$prof,$q,$u,$cal,0,$now)
            ON CONFLICT(key) DO UPDATE SET name=$name, mic_label=$label, profile_name=$prof, quality=$q, uncertainty=$u, calibrated=$cal, last_seen=$now,
                hidden = CASE WHEN hidden = 2 THEN 0 ELSE hidden END
            """, ("$key", MicKey(nodeId, m.ChannelIndex)), ("$node", nodeId), ("$ch", m.ChannelIndex), ("$name", m.Name),
            ("$label", (object?)m.MicLabel ?? DBNull.Value), ("$prof", (object?)m.ProfileName ?? DBNull.Value),
            ("$q", (object?)m.Quality?.ToString() ?? DBNull.Value), ("$u", (object?)m.UncertaintyDb ?? DBNull.Value),
            ("$cal", m.Calibrated ? 1 : 0), ("$now", now));
        cmd.ExecuteNonQuery();
    }

    public static string MicKey(string nodeId, int channel) => $"{nodeId}:{channel}";

    public List<MicRow> ListMics()
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id,key,node_id,channel,name,display_name,mic_label,profile_name,quality,uncertainty,calibrated,hidden,last_seen FROM mics ORDER BY node_id, channel");
            using var r = cmd.ExecuteReader();
            var list = new List<MicRow>();
            while (r.Read())
                list.Add(new MicRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : Enum.Parse<CalibrationQuality>(r.GetString(8)), r.IsDBNull(9) ? null : r.GetDouble(9),
                    r.GetInt32(10) == 1, r.GetInt32(11) != 0, r.GetInt64(12)));
            return list;
        }
    }

    public void UpdateMic(string key, string? displayName, bool hidden)
    {
        lock (_lock)
            using (var cmd = Cmd("UPDATE mics SET display_name=$d, hidden=$h WHERE key=$k",
                ("$d", string.IsNullOrWhiteSpace(displayName) ? DBNull.Value : displayName.Trim()), ("$h", hidden ? 1 : 0), ("$k", key)))
                cmd.ExecuteNonQuery();
    }

    private int MicIdLocked(string key)
    {
        using var cmd = Cmd("SELECT id FROM mics WHERE key=$k", ("$k", key));
        return cmd.ExecuteScalar() is long id ? (int)id : throw new KeyNotFoundException($"Unknown mic {key}");
    }

    // ---------- records ----------

    /// <summary>Stores a batch from one node (idempotent: re-sent records overwrite) and advances its ack.</summary>
    public long StoreRecords(string nodeId, IReadOnlyList<SeqRecord> items)
    {
        if (items.Count == 0) return GetNode(nodeId)?.LastSeq ?? 0;
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var micIds = new Dictionary<int, int>();
            using var sec = Cmd("INSERT OR REPLACE INTO seconds (mic,ts,la,lc,lz,lcpk,lasmax,lcsmax,cov,flags) VALUES ($m,$ts,$la,$lc,$lz,$pk,$asm,$csm,$cov,$f)");
            using var min = Cmd("INSERT OR REPLACE INTO minutes (mic,ts,la,lc,lz,lcpk,lasmax,lcsmax,cov,flags,bands) VALUES ($m,$ts,$la,$lc,$lz,$pk,$asm,$csm,$cov,$f,$b)");
            foreach (var cmd in new[] { sec, min })
                foreach (var p in new[] { "$m", "$ts", "$la", "$lc", "$lz", "$pk", "$asm", "$csm", "$cov", "$f" }) cmd.Parameters.Add(new SqliteParameter(p, 0));
            min.Parameters.Add(new SqliteParameter("$b", Array.Empty<byte>()));
            sec.Transaction = tx; min.Transaction = tx;

            long maxSeq = 0;
            foreach (var (seq, r) in items)
            {
                maxSeq = Math.Max(maxSeq, seq);
                if (!micIds.TryGetValue(r.ChannelIndex, out var micId))
                {
                    try { micId = MicIdLocked(MicKey(nodeId, r.ChannelIndex)); }
                    catch (KeyNotFoundException)
                    {
                        UpsertMicLocked(nodeId, new MicInfo(r.ChannelIndex, $"Mic {r.ChannelIndex + 1}", null, null, r.Quality, null, r.Calibrated), Now());
                        micId = MicIdLocked(MicKey(nodeId, r.ChannelIndex));
                    }
                    micIds[r.ChannelIndex] = micId;
                }
                var cmd = r.Kind == RecordKind.Second ? sec : min;
                cmd.Parameters["$m"].Value = micId;
                cmd.Parameters["$ts"].Value = r.Ts;
                cmd.Parameters["$la"].Value = C(r.LAeq);
                cmd.Parameters["$lc"].Value = C(r.LCeq);
                cmd.Parameters["$lz"].Value = C(r.LZeq);
                cmd.Parameters["$pk"].Value = C(r.LCpeak);
                cmd.Parameters["$asm"].Value = C(r.LASmax);
                cmd.Parameters["$csm"].Value = C(r.LCSmax);
                cmd.Parameters["$cov"].Value = (int)Math.Round(r.Coverage * 100);
                cmd.Parameters["$f"].Value = Flags(r);
                if (r.Kind == RecordKind.Minute) min.Parameters["$b"].Value = PackBands(r.Bands);
                cmd.ExecuteNonQuery();
            }
            using (var up = Cmd("UPDATE nodes SET last_seq=MAX(last_seq,$s), last_seen=$t WHERE id=$id", ("$s", maxSeq), ("$t", Now()), ("$id", nodeId)))
            {
                up.Transaction = tx;
                up.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return GetNode(nodeId)?.LastSeq ?? 0;
    }

    private static int C(double db) => (int)Math.Round(Math.Clamp(db, -300, 300) * 100);
    private static double D(long c) => c / 100.0;

    private static int Flags(LevelRecord r) =>
        (r.Calibrated ? 1 : 0) | (r.Clipped ? 2 : 0) | ((int)(r.Quality ?? CalibrationQuality.Unknown) << 4);

    private static byte[] PackBands(double[]? bands)
    {
        if (bands is null) return [];
        var b = new byte[bands.Length * 2];
        for (var i = 0; i < bands.Length; i++)
            BitConverter.TryWriteBytes(b.AsSpan(i * 2), (short)Math.Clamp(Math.Round(bands[i] * 100), short.MinValue, short.MaxValue));
        return b;
    }

    private static double[] UnpackBands(byte[] b)
    {
        var v = new double[b.Length / 2];
        for (var i = 0; i < v.Length; i++) v[i] = BitConverter.ToInt16(b, i * 2) / 100.0;
        return v;
    }

    /// <summary>Level series for one mic. Uses seconds for ranges up to <paramref name="secondsLimit"/>, minutes beyond.</summary>
    public Series GetSeries(string micKey, long from, long to, TimeSpan? secondsLimit = null)
    {
        var useSeconds = to - from <= (secondsLimit ?? TimeSpan.FromHours(3)).TotalSeconds;
        var table = useSeconds ? "seconds" : "minutes";
        lock (_lock)
        {
            var micId = MicIdLocked(micKey);
            using var cmd = Cmd($"SELECT ts,la,lc,lz,lcpk,lcsmax,cov FROM {table} WHERE mic=$m AND ts>=$f AND ts<$t ORDER BY ts",
                ("$m", micId), ("$f", from), ("$t", to));
            using var r = cmd.ExecuteReader();
            var ts = new List<long>(); var la = new List<double>(); var lc = new List<double>(); var lz = new List<double>();
            var pk = new List<double>(); var csm = new List<double>(); var cov = new List<double>();
            while (r.Read())
            {
                ts.Add(r.GetInt64(0)); la.Add(D(r.GetInt64(1))); lc.Add(D(r.GetInt64(2))); lz.Add(D(r.GetInt64(3)));
                pk.Add(D(r.GetInt64(4))); csm.Add(D(r.GetInt64(5))); cov.Add(r.GetInt64(6) / 100.0);
            }
            return new Series(micKey, useSeconds ? "second" : "minute", ts.ToArray(), la.ToArray(), lc.ToArray(), lz.ToArray(), pk.ToArray(), csm.ToArray(), cov.ToArray());
        }
    }

    public (long[] Ts, double[][] Bands) GetSpectrogram(string micKey, long from, long to)
    {
        lock (_lock)
        {
            var micId = MicIdLocked(micKey);
            using var cmd = Cmd("SELECT ts,bands FROM minutes WHERE mic=$m AND ts>=$f AND ts<$t ORDER BY ts", ("$m", micId), ("$f", from), ("$t", to));
            using var r = cmd.ExecuteReader();
            var ts = new List<long>(); var bands = new List<double[]>();
            while (r.Read()) { ts.Add(r.GetInt64(0)); bands.Add(r.IsDBNull(1) ? [] : UnpackBands((byte[])r[1])); }
            return (ts.ToArray(), bands.ToArray());
        }
    }

    /// <summary>Seconds spent at each whole-dB level (A and C), from per-second Leq.</summary>
    public (int[] Db, int[] SecondsA, int[] SecondsC) GetHistogram(string micKey, long from, long to)
    {
        lock (_lock)
        {
            var micId = MicIdLocked(micKey);
            using var cmd = Cmd("SELECT la, lc FROM seconds WHERE mic=$m AND ts>=$f AND ts<$t", ("$m", micId), ("$f", from), ("$t", to));
            using var r = cmd.ExecuteReader();
            var a = new SortedDictionary<int, int>(); var c = new SortedDictionary<int, int>();
            while (r.Read())
            {
                var ka = (int)Math.Floor(D(r.GetInt64(0))); var kc = (int)Math.Floor(D(r.GetInt64(1)));
                if (ka > -150) a[ka] = a.GetValueOrDefault(ka) + 1;
                if (kc > -150) c[kc] = c.GetValueOrDefault(kc) + 1;
            }
            if (a.Count + c.Count == 0) return ([], [], []);
            var lo = Math.Min(a.Keys.DefaultIfEmpty(int.MaxValue).Min(), c.Keys.DefaultIfEmpty(int.MaxValue).Min());
            var hi = Math.Max(a.Keys.DefaultIfEmpty(int.MinValue).Max(), c.Keys.DefaultIfEmpty(int.MinValue).Max());
            var dbs = Enumerable.Range(lo, hi - lo + 1).ToArray();
            return (dbs, dbs.Select(d => a.GetValueOrDefault(d)).ToArray(), dbs.Select(d => c.GetValueOrDefault(d)).ToArray());
        }
    }

    /// <summary>Energy sums over a range from per-second data: (sumA, sumC, seconds, maxPeakC).</summary>
    public (double EnergyA, double EnergyC, double Seconds, double PeakC) SumSeconds(string micKey, long from, long to)
    {
        lock (_lock)
        {
            int micId;
            try { micId = MicIdLocked(micKey); } catch (KeyNotFoundException) { return (0, 0, 0, double.NegativeInfinity); }
            using var cmd = Cmd("SELECT la, lc, cov, lcpk FROM seconds WHERE mic=$m AND ts>=$f AND ts<$t", ("$m", micId), ("$f", from), ("$t", to));
            using var r = cmd.ExecuteReader();
            double ea = 0, ec = 0, d = 0, pk = double.NegativeInfinity;
            while (r.Read())
            {
                var cov = r.GetInt64(2) / 100.0;
                ea += Math.Pow(10, D(r.GetInt64(0)) / 10) * cov;
                ec += Math.Pow(10, D(r.GetInt64(1)) / 10) * cov;
                d += cov;
                pk = Math.Max(pk, D(r.GetInt64(3)));
            }
            return (ea, ec, d, pk);
        }
    }

    /// <summary>Highest LCeq of any single minute in the range (from minute records).</summary>
    public (double MaxLAeq1m, double MaxLCeq1m) MaxMinute(string micKey, long from, long to)
    {
        lock (_lock)
        {
            int micId;
            try { micId = MicIdLocked(micKey); } catch (KeyNotFoundException) { return (double.NaN, double.NaN); }
            using var cmd = Cmd("SELECT MAX(la), MAX(lc) FROM minutes WHERE mic=$m AND ts>=$f AND ts<$t AND cov >= 3000", ("$m", micId), ("$f", from), ("$t", to));
            using var r = cmd.ExecuteReader();
            return r.Read() && !r.IsDBNull(0) ? (D(r.GetInt64(0)), D(r.GetInt64(1))) : (double.NaN, double.NaN);
        }
    }

    /// <summary>Seconds in range whose 1-s LCeq (or LAeq) was at or above each threshold.</summary>
    public int SecondsAbove(string micKey, long from, long to, bool cWeighted, double threshold)
    {
        lock (_lock)
        {
            int micId;
            try { micId = MicIdLocked(micKey); } catch (KeyNotFoundException) { return 0; }
            using var cmd = Cmd($"SELECT COUNT(*) FROM seconds WHERE mic=$m AND ts>=$f AND ts<$t AND {(cWeighted ? "lc" : "la")} >= $th",
                ("$m", micId), ("$f", from), ("$t", to), ("$th", C(threshold)));
            return (int)(long)cmd.ExecuteScalar()!;
        }
    }

    public int PruneSeconds(long olderThan)
    {
        lock (_lock) using (var cmd = Cmd("DELETE FROM seconds WHERE ts < $t", ("$t", olderThan))) return cmd.ExecuteNonQuery();
    }

    // ---------- services ----------

    public List<ServiceRow> ListServices(long from, long to)
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id,name,start_ts,end_ts,source,schedule_id FROM services WHERE start_ts < $t AND COALESCE(end_ts, $t) > $f ORDER BY start_ts DESC",
                ("$f", from), ("$t", to));
            return ReadServices(cmd);
        }
    }

    public ServiceRow? GetService(long id)
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id,name,start_ts,end_ts,source,schedule_id FROM services WHERE id=$id", ("$id", id));
            return ReadServices(cmd).FirstOrDefault();
        }
    }

    public ServiceRow? ActiveService()
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id,name,start_ts,end_ts,source,schedule_id FROM services WHERE end_ts IS NULL ORDER BY start_ts DESC LIMIT 1");
            return ReadServices(cmd).FirstOrDefault();
        }
    }

    public ServiceRow? FindScheduledOccurrence(long scheduleId, long startTs)
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id,name,start_ts,end_ts,source,schedule_id FROM services WHERE schedule_id=$s AND start_ts>=$a AND start_ts<$b",
                ("$s", scheduleId), ("$a", startTs - 12 * 3600), ("$b", startTs + 12 * 3600));
            return ReadServices(cmd).FirstOrDefault();
        }
    }

    private static List<ServiceRow> ReadServices(SqliteCommand cmd)
    {
        using var r = cmd.ExecuteReader();
        var list = new List<ServiceRow>();
        while (r.Read())
            list.Add(new ServiceRow(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetInt64(5)));
        return list;
    }

    public ServiceRow StartService(string name, long startTs, string source, long? scheduleId)
    {
        lock (_lock)
        {
            using var cmd = Cmd("INSERT INTO services (name,start_ts,end_ts,source,schedule_id) VALUES ($n,$s,NULL,$src,$sch); SELECT last_insert_rowid();",
                ("$n", name), ("$s", startTs), ("$src", source), ("$sch", (object?)scheduleId ?? DBNull.Value));
            var id = (long)cmd.ExecuteScalar()!;
            return new ServiceRow(id, name, startTs, null, source, scheduleId);
        }
    }

    public void EndService(long id, long endTs)
    {
        lock (_lock) using (var cmd = Cmd("UPDATE services SET end_ts=$e WHERE id=$id AND end_ts IS NULL", ("$e", endTs), ("$id", id))) cmd.ExecuteNonQuery();
    }

    public void UpdateService(long id, string name, long startTs, long? endTs)
    {
        lock (_lock)
            using (var cmd = Cmd("UPDATE services SET name=$n, start_ts=$s, end_ts=$e WHERE id=$id",
                ("$n", name), ("$s", startTs), ("$e", (object?)endTs ?? DBNull.Value), ("$id", id)))
                cmd.ExecuteNonQuery();
    }

    public void DeleteService(long id)
    {
        lock (_lock) using (var cmd = Cmd("DELETE FROM services WHERE id=$id", ("$id", id))) cmd.ExecuteNonQuery();
    }

    // ---------- schedule ----------

    public List<ScheduleRow> ListSchedule()
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id,name,day,start_minute,duration_minutes,enabled FROM schedule ORDER BY day, start_minute");
            using var r = cmd.ExecuteReader();
            var list = new List<ScheduleRow>();
            while (r.Read()) list.Add(new ScheduleRow(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetInt32(5) == 1));
            return list;
        }
    }

    public void ReplaceSchedule(IEnumerable<ScheduleRow> rows)
    {
        lock (_lock)
        {
            using var tx = _db.BeginTransaction();
            var keep = new List<long>();
            foreach (var s in rows)
            {
                using var cmd = s.Id > 0
                    ? Cmd("UPDATE schedule SET name=$n, day=$d, start_minute=$s, duration_minutes=$m, enabled=$e WHERE id=$id; SELECT $id;",
                        ("$n", s.Name), ("$d", s.Day), ("$s", s.StartMinute), ("$m", s.DurationMinutes), ("$e", s.Enabled ? 1 : 0), ("$id", s.Id))
                    : Cmd("INSERT INTO schedule (name,day,start_minute,duration_minutes,enabled) VALUES ($n,$d,$s,$m,$e); SELECT last_insert_rowid();",
                        ("$n", s.Name), ("$d", s.Day), ("$s", s.StartMinute), ("$m", s.DurationMinutes), ("$e", s.Enabled ? 1 : 0));
                cmd.Transaction = tx;
                keep.Add((long)cmd.ExecuteScalar()!);
            }
            using (var del = Cmd($"DELETE FROM schedule WHERE id NOT IN ({(keep.Count == 0 ? "-1" : string.Join(",", keep))})"))
            {
                del.Transaction = tx;
                del.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    // ---------- alert events ----------

    public long StartAlertEvent(long ruleId, string ruleName, string micKey, string level, long startTs, double value, string metric)
    {
        lock (_lock)
        {
            using var cmd = Cmd("INSERT INTO alert_events (rule_id,rule_name,mic_key,level,start_ts,end_ts,peak,metric) VALUES ($r,$rn,$m,$l,$s,NULL,$p,$mt); SELECT last_insert_rowid();",
                ("$r", ruleId), ("$rn", ruleName), ("$m", micKey), ("$l", level), ("$s", startTs), ("$p", value), ("$mt", metric));
            return (long)cmd.ExecuteScalar()!;
        }
    }

    public void UpdateAlertEvent(long id, double peak, long? endTs)
    {
        lock (_lock)
            using (var cmd = Cmd("UPDATE alert_events SET peak=MAX(peak,$p), end_ts=COALESCE($e, end_ts) WHERE id=$id",
                ("$p", peak), ("$e", (object?)endTs ?? DBNull.Value), ("$id", id)))
                cmd.ExecuteNonQuery();
    }

    /// <summary>Closes events left open by a previous run.</summary>
    public void CloseOpenAlertEvents(long endTs)
    {
        lock (_lock) using (var cmd = Cmd("UPDATE alert_events SET end_ts=$e WHERE end_ts IS NULL", ("$e", endTs))) cmd.ExecuteNonQuery();
    }

    public List<AlertEventRow> ListAlertEvents(long from, long to, int limit = 500)
    {
        lock (_lock)
        {
            using var cmd = Cmd("SELECT id,rule_id,rule_name,mic_key,level,start_ts,end_ts,peak,metric FROM alert_events WHERE start_ts < $t AND COALESCE(end_ts,$t) >= $f ORDER BY start_ts DESC LIMIT $n",
                ("$f", from), ("$t", to), ("$n", limit));
            using var r = cmd.ExecuteReader();
            var list = new List<AlertEventRow>();
            while (r.Read())
                list.Add(new AlertEventRow(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5),
                    r.IsDBNull(6) ? null : r.GetInt64(6), r.GetDouble(7), r.GetString(8)));
            return list;
        }
    }

    // ---------- helpers ----------

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private SqliteCommand Cmd(string sql, params (string Name, object Value)[] ps)
    {
        var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in ps) cmd.Parameters.AddWithValue(n, v);
        return cmd;
    }

    private void Exec(string sql)
    {
        using var cmd = Cmd(sql);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
