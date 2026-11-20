using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Rta.Core;
using Rta.Core.Calibration;

namespace Rta.App.Backup;

/// <summary>What a backup file contains. Sections: node, hub, admin (and history when included).</summary>
public sealed record BackupManifest
{
    public int Format { get; init; } = 1;
    public string AppVersion { get; init; } = "";
    public DateTimeOffset Created { get; init; }
    public string Machine { get; init; } = "";
    public string Reason { get; init; } = "";
    public string? NodeId { get; init; }
    public string? NodeName { get; init; }
    public string? HubId { get; init; }
    public string? HubName { get; init; }
    public bool IncludesHistory { get; init; }
    public List<string> Sections { get; init; } = [];
}

public sealed record RestoreOptions
{
    public List<string> Sections { get; init; } = [];
    /// <summary>This PC replaces the one the backup came from: take over its node and Hub identity.</summary>
    public bool TakeOverIdentity { get; init; }
}

public sealed record RestoreResult(bool Ok, DateTimeOffset At, string Message, string? SafetyBackup, List<string> Restored);

/// <summary>
/// Settings backups (.rtabackup, a zip). Holds node settings and calibration, Hub settings, floor plan,
/// zones and surveys, mic names, schedule and the admin password, and optionally the logged history.
/// Machine-bound files (sign-in cookie keys, install choices in app.json, logs) are left out.
/// Restores are applied at start-up (see <see cref="ApplyPending"/>) so nothing has the files open.
/// </summary>
public static class BackupArchive
{
    public const string Extension = ".rtabackup";
    public const string Node = "node", Hub = "hub", Admin = "admin", History = "history";

    // Logged data left out of a settings-only copy of hub.db.
    private static readonly string[] HistoryTables = ["seconds", "minutes", "services", "alert_events"];

    public static string BackupsDir(string dataDir) => Path.Combine(dataDir, "backups");
    private static string RestoreDir(string dataDir) => Path.Combine(dataDir, "restore");
    public static string PendingFile(string dataDir) => Path.Combine(RestoreDir(dataDir), "pending" + Extension);
    public static string StagedFile(string dataDir) => Path.Combine(RestoreDir(dataDir), "staged" + Extension);
    private static string PendingOptions(string dataDir) => Path.Combine(RestoreDir(dataDir), "pending.json");
    private static string LastResultFile(string dataDir) => Path.Combine(RestoreDir(dataDir), "last-result.json");

    // ---------- create ----------

    /// <summary>Writes a backup of <paramref name="dataDir"/> to <paramref name="outPath"/>.</summary>
    public static BackupManifest Create(string dataDir, string outPath, string appVersion, string reason, bool includeHistory)
    {
        var tmpDb = Path.Combine(Path.GetTempPath(), $"rta-backup-{Guid.NewGuid():N}.db");
        var sections = new List<string>();
        var node = ReadObject(Path.Combine(dataDir, "node.json"));
        var hub = ReadObject(Path.Combine(dataDir, "hub.json"));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        var partial = outPath + ".partial";
        try
        {
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                if (node is not null)
                {
                    sections.Add(Node);
                    AddFile(zip, Path.Combine(dataDir, "node.json"), "node/node.json");
                    var cal = Path.Combine(dataDir, "calibration");
                    if (Directory.Exists(cal))
                        foreach (var f in Directory.EnumerateFiles(cal, "*.json").Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                            AddFile(zip, f, "node/calibration/" + Path.GetFileName(f));
                }
                if (hub is not null)
                {
                    sections.Add(Hub);
                    AddFile(zip, Path.Combine(dataDir, "hub.json"), "hub/hub.json");
                    AddFile(zip, Path.Combine(dataDir, "plan.json"), "hub/plan.json");
                    var images = Path.Combine(dataDir, "plan-images");
                    if (Directory.Exists(images))
                        foreach (var f in Directory.EnumerateFiles(images))
                            AddFile(zip, f, "hub/plan-images/" + Path.GetFileName(f));
                    var db = Path.Combine(dataDir, "hub.db");
                    if (File.Exists(db))
                    {
                        CopyDatabase(db, tmpDb, includeHistory);
                        AddFile(zip, tmpDb, "hub/hub.db");
                        if (includeHistory) sections.Add(History);
                    }
                }
                AddFile(zip, Path.Combine(dataDir, "pc.json"), "pc/pc.json"); // per-PC options, restored with node or Hub settings
                if (File.Exists(Path.Combine(dataDir, "admin.json")))
                {
                    sections.Add(Admin);
                    AddFile(zip, Path.Combine(dataDir, "admin.json"), "admin/admin.json");
                }
                var manifest = new BackupManifest
                {
                    AppVersion = appVersion, Created = DateTimeOffset.Now, Machine = Environment.MachineName, Reason = reason,
                    NodeId = Str(node, "nodeId"), NodeName = Str(node, "nodeName"), HubId = Str(hub, "hubId"), HubName = Str(hub, "name"),
                    IncludesHistory = sections.Contains(History), Sections = sections,
                };
                var entry = zip.CreateEntry("manifest.json");
                using (var s = entry.Open()) JsonSerializer.Serialize(s, manifest, CalibrationStore.Json);
                zip.Dispose();
                File.Move(partial, outPath, overwrite: true);
                return manifest;
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(tmpDb);
            TryDelete(partial);
        }
    }

    /// <summary>A consistent copy of the Hub database (safe while the app is running), optionally without logged data.</summary>
    private static void CopyDatabase(string db, string target, bool includeHistory)
    {
        TryDelete(target);
        using (var src = new SqliteConnection($"Data Source={db};Mode=ReadOnly;Pooling=False"))
        {
            src.Open();
            using var cmd = src.CreateCommand();
            cmd.CommandText = "VACUUM INTO $p";
            cmd.Parameters.AddWithValue("$p", target);
            cmd.ExecuteNonQuery();
        }
        if (includeHistory) return;
        using var copy = new SqliteConnection($"Data Source={target};Pooling=False");
        copy.Open();
        foreach (var t in HistoryTables.Where(t => TableExists(copy, "main", t)))
            Exec(copy, $"DELETE FROM {t};");
        Exec(copy, "VACUUM;");
    }

    // ---------- read ----------

    public static BackupManifest ReadManifest(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            var entry = zip.GetEntry("manifest.json") ?? throw new InvalidDataException("This isn't an RTA backup file (no manifest).");
            using var s = entry.Open();
            var m = JsonSerializer.Deserialize<BackupManifest>(s, CalibrationStore.Json) ?? throw new InvalidDataException("The backup's manifest is empty.");
            if (m.Format > 1) throw new InvalidDataException($"This backup was made by a newer version ({m.AppVersion}). Update the app first.");
            return m;
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException) { throw new InvalidDataException("This isn't a readable RTA backup file.", ex); }
    }

    // ---------- restore ----------

    /// <summary>Queues a restore for the next start-up.</summary>
    public static void QueueRestore(string dataDir, string backupFile, RestoreOptions options)
    {
        Directory.CreateDirectory(RestoreDir(dataDir));
        File.Copy(backupFile, PendingFile(dataDir), overwrite: true);
        File.WriteAllText(PendingOptions(dataDir), JsonSerializer.Serialize(options, CalibrationStore.Json));
    }

    public static bool RestorePending(string dataDir) => File.Exists(PendingFile(dataDir));

    public static RestoreResult? LastResult(string dataDir)
    {
        try { return File.Exists(LastResultFile(dataDir)) ? JsonSerializer.Deserialize<RestoreResult>(File.ReadAllText(LastResultFile(dataDir)), CalibrationStore.Json) : null; }
        catch (JsonException) { return null; }
    }

    /// <summary>Applies a queued restore. Call at start-up before anything opens the data folder.</summary>
    public static RestoreResult? ApplyPending(string dataDir, string appVersion)
    {
        var pending = PendingFile(dataDir);
        if (!File.Exists(pending)) return null;
        RestoreResult result;
        try
        {
            var options = File.Exists(PendingOptions(dataDir))
                ? JsonSerializer.Deserialize<RestoreOptions>(File.ReadAllText(PendingOptions(dataDir)), CalibrationStore.Json) ?? new()
                : new RestoreOptions();
            result = Restore(dataDir, pending, options, appVersion);
            File.Delete(pending);
        }
        catch (Exception ex)
        {
            result = new RestoreResult(false, DateTimeOffset.Now, "Restore failed, nothing was changed: " + ex.Message, null, []);
            // Keep the file for a look, but don't retry on every start.
            try { File.Move(pending, Path.Combine(RestoreDir(dataDir), $"failed-{DateTime.Now:yyyyMMdd-HHmmss}{Extension}"), overwrite: true); } catch (IOException) { }
        }
        TryDelete(PendingOptions(dataDir));
        File.WriteAllText(LastResultFile(dataDir), JsonSerializer.Serialize(result, CalibrationStore.Json));
        return result;
    }

    /// <summary>Restores the chosen sections into <paramref name="dataDir"/>, after saving a safety backup of what's there.</summary>
    public static RestoreResult Restore(string dataDir, string zipPath, RestoreOptions options, string appVersion)
    {
        var manifest = ReadManifest(zipPath);
        var want = options.Sections.Where(manifest.Sections.Contains).ToHashSet();
        if (want.Count == 0) throw new InvalidDataException("Nothing selected to restore.");

        var safety = Path.Combine(BackupsDir(dataDir), $"before-restore-{DateTime.Now:yyyyMMdd-HHmmss}{Extension}");
        Create(dataDir, safety, appVersion, "Before restoring a backup", includeHistory: false);

        var work = Path.Combine(Path.GetTempPath(), "rta-restore-" + Guid.NewGuid().ToString("N"));
        try
        {
            ZipFile.ExtractToDirectory(zipPath, work);
            var restored = new List<string>();
            if (want.Contains(Node) && File.Exists(Path.Combine(work, "node", "node.json")))
            {
                var cfg = ReadObject(Path.Combine(work, "node", "node.json"))!;
                KeepIdentity(cfg, "nodeId", ReadObject(Path.Combine(dataDir, "node.json")), options.TakeOverIdentity);
                SafeFile.WriteAllText(Path.Combine(dataDir, "node.json"), cfg.ToJsonString(CalibrationStore.Json));
                ReplaceDirectory(Path.Combine(work, "node", "calibration"), Path.Combine(dataDir, "calibration"));
                restored.Add("node settings and calibration");
            }
            if (want.Contains(Hub) && File.Exists(Path.Combine(work, "hub", "hub.json")))
            {
                var hub = ReadObject(Path.Combine(work, "hub", "hub.json"))!;
                KeepIdentity(hub, "hubId", ReadObject(Path.Combine(dataDir, "hub.json")), options.TakeOverIdentity);
                SafeFile.WriteAllText(Path.Combine(dataDir, "hub.json"), hub.ToJsonString(CalibrationStore.Json));
                var plan = Path.Combine(work, "hub", "plan.json");
                if (File.Exists(plan)) SafeFile.WriteAllText(Path.Combine(dataDir, "plan.json"), File.ReadAllText(plan));
                ReplaceDirectory(Path.Combine(work, "hub", "plan-images"), Path.Combine(dataDir, "plan-images"));
                var db = Path.Combine(work, "hub", "hub.db");
                if (File.Exists(db))
                {
                    if (want.Contains(History) && manifest.IncludesHistory) ReplaceDatabase(db, Path.Combine(dataDir, "hub.db"));
                    else MergeSettings(db, Path.Combine(dataDir, "hub.db"));
                }
                restored.Add(want.Contains(History) && manifest.IncludesHistory
                    ? "Hub settings, plan, zones, surveys and logged history"
                    : "Hub settings, plan, zones, surveys and schedule");
            }
            var pc = Path.Combine(work, "pc", "pc.json");
            if ((want.Contains(Node) || want.Contains(Hub)) && File.Exists(pc))
                SafeFile.WriteAllText(Path.Combine(dataDir, "pc.json"), File.ReadAllText(pc));
            if (want.Contains(Admin) && File.Exists(Path.Combine(work, "admin", "admin.json")))
            {
                SafeFile.WriteAllText(Path.Combine(dataDir, "admin.json"), File.ReadAllText(Path.Combine(work, "admin", "admin.json")));
                restored.Add("admin password");
            }
            var from = $"{manifest.Machine}, {manifest.Created.LocalDateTime:g}";
            return new RestoreResult(true, DateTimeOffset.Now,
                $"Restored {string.Join(", ", restored)} from the backup made on {from}.", Path.GetFileName(safety), restored);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
    }

    /// <summary>Unless taking over, keeps this PC's own id (or lets the app make a new one).</summary>
    private static void KeepIdentity(JsonObject restored, string key, JsonObject? current, bool takeOver)
    {
        if (takeOver) return;
        if (Str(current, key) is { Length: > 0 } id) restored[key] = id;
        else restored.Remove(key);
    }

    private static void ReplaceDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(to)) File.Delete(f);
        if (!Directory.Exists(from)) return;
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    }

    private static void ReplaceDatabase(string from, string to)
    {
        foreach (var suffix in new[] { "-wal", "-shm", "" }) TryDelete(to + suffix);
        File.Copy(from, to);
    }

    /// <summary>
    /// Merges settings from a backup's database into this PC's, keeping this PC's logged history:
    /// nodes and mics are matched by id/key (names, hidden flags and acceptance restored),
    /// and the schedule and surveys are replaced.
    /// </summary>
    private static void MergeSettings(string from, string to)
    {
        if (!File.Exists(to)) { File.Copy(from, to); return; }
        using var db = new SqliteConnection($"Data Source={to};Pooling=False");
        db.Open();
        using (var attach = db.CreateCommand())
        {
            attach.CommandText = "ATTACH DATABASE $p AS b";
            attach.Parameters.AddWithValue("$p", from);
            attach.ExecuteNonQuery();
        }
        using var tx = db.BeginTransaction();
        if (TableExists(db, "b", "nodes", tx) && TableExists(db, "main", "nodes", tx))
            Exec(db, """
                INSERT INTO main.nodes(id, name, acceptance, buffer_id, last_seq, first_seen, last_seen, address, version)
                SELECT id, name, acceptance, NULL, 0, first_seen, last_seen, address, version FROM b.nodes WHERE true
                ON CONFLICT(id) DO UPDATE SET name = excluded.name, acceptance = excluded.acceptance;
                """, tx);
        if (TableExists(db, "b", "mics", tx) && TableExists(db, "main", "mics", tx))
            Exec(db, """
                INSERT INTO main.mics(key, node_id, channel, name, display_name, mic_label, profile_name, quality, uncertainty, calibrated, hidden, last_seen)
                SELECT key, node_id, channel, name, display_name, mic_label, profile_name, quality, uncertainty, calibrated, hidden, last_seen FROM b.mics WHERE true
                ON CONFLICT(key) DO UPDATE SET display_name = excluded.display_name, hidden = excluded.hidden;
                """, tx);
        foreach (var table in new[] { "schedule", "surveys" })
        {
            if (!TableExists(db, "b", table, tx)) continue;
            if (!TableExists(db, "main", table, tx))
                Exec(db, ScalarString(db, $"SELECT sql FROM b.sqlite_master WHERE type='table' AND name='{table}'", tx)!, tx);
            var cols = Columns(db, "main", table, tx).Intersect(Columns(db, "b", table, tx)).ToList();
            var list = string.Join(", ", cols.Select(c => $"\"{c}\""));
            Exec(db, $"DELETE FROM main.{table}; INSERT INTO main.{table} ({list}) SELECT {list} FROM b.{table};", tx);
        }
        tx.Commit();
    }

    // ---------- automatic backups ----------

    /// <summary>Keeps a settings backup from just before a new version first runs.</summary>
    public static string? SnapshotIfVersionChanged(string dataDir, string appVersion)
    {
        var marker = Path.Combine(dataDir, "last-run-version.txt");
        var previous = File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
        string? made = null;
        var hasSettings = File.Exists(Path.Combine(dataDir, "node.json")) || File.Exists(Path.Combine(dataDir, "hub.json"));
        if (previous != appVersion && hasSettings)
        {
            var label = previous is null ? "an earlier version" : previous;
            made = Path.Combine(BackupsDir(dataDir), $"before-update-{appVersion}-{DateTime.Now:yyyyMMdd-HHmmss}{Extension}");
            Create(dataDir, made, previous ?? "unknown", $"Settings from {label}, saved before {appVersion} first ran", includeHistory: false);
        }
        File.WriteAllText(marker, appVersion);
        Prune(dataDir);
        return made;
    }

    public static readonly Dictionary<string, int> Keep = new() { ["daily-"] = 14, ["before-update-"] = 5, ["before-restore-"] = 5, ["before-room-import-"] = 5 };

    public static void Prune(string dataDir)
    {
        var dir = BackupsDir(dataDir);
        if (!Directory.Exists(dir)) return;
        foreach (var (prefix, keep) in Keep)
            foreach (var old in new DirectoryInfo(dir).EnumerateFiles(prefix + "*" + Extension).OrderByDescending(f => f.Name).Skip(keep))
                TryDelete(old.FullName);
    }

    public static IReadOnlyList<FileInfo> List(string dataDir)
    {
        var dir = BackupsDir(dataDir);
        return Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles("*" + Extension).OrderByDescending(f => f.LastWriteTimeUtc).ToList()
            : [];
    }

    /// <summary>A file in the backups folder, or null if the name isn't one.</summary>
    public static string? FindBackup(string dataDir, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || !name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(BackupsDir(dataDir), name);
        return File.Exists(path) ? path : null;
    }

    // ---------- helpers ----------

    private static void AddFile(ZipArchive zip, string path, string entry)
    {
        if (!File.Exists(path)) return;
        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = zip.CreateEntry(entry, CompressionLevel.Optimal).Open();
        src.CopyTo(dst);
    }

    private static JsonObject? ReadObject(string path)
    {
        try { return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject : null; }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonObject? o, string key) =>
        o is null ? null : (o.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value as JsonValue)?.GetValue<string>();

    private static bool TableExists(SqliteConnection db, string schema, string table, SqliteTransaction? tx = null) =>
        ScalarString(db, $"SELECT name FROM {schema}.sqlite_master WHERE type='table' AND name='{table}'", tx) is not null;

    private static List<string> Columns(SqliteConnection db, string schema, string table, SqliteTransaction tx)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"PRAGMA {schema}.table_info({table})";
        using var r = cmd.ExecuteReader();
        var list = new List<string>();
        while (r.Read()) list.Add(r.GetString(1));
        return list;
    }

    private static string? ScalarString(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    private static void Exec(SqliteConnection db, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
