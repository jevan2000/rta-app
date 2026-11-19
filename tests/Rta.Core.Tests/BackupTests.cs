using System.Text.Json;
using Rta.App;
using Rta.App.Backup;
using Rta.App.Hub;
using Rta.App.Node;
using Rta.App.Security;
using Rta.Core.Calibration;
using Rta.Core.Logging;
using Rta.Core.Protocol;

namespace Rta.Core.Tests;

public class SafeFileTests
{
    private sealed record Doc(string Name);
    private static readonly JsonSerializerOptions J = CalibrationStore.Json;

    [Fact]
    public void A_damaged_file_is_set_aside_and_the_previous_version_is_used()
    {
        using var tmp = new TempData();
        Directory.CreateDirectory(tmp.Dir);
        var path = Path.Combine(tmp.Dir, "x.json");
        SafeFile.WriteJson(path, new Doc("first"), J);
        SafeFile.WriteJson(path, new Doc("second"), J);
        Assert.Equal("second", SafeFile.ReadJson<Doc>(path, J)!.Name);
        Assert.Equal("first", JsonSerializer.Deserialize<Doc>(File.ReadAllText(path + ".bak"), J)!.Name);

        File.WriteAllText(path, "{\"name\": \"trunc");                 // e.g. power cut mid-write
        Assert.Equal("first", SafeFile.ReadJson<Doc>(path, J)!.Name);
        Assert.Single(Directory.GetFiles(tmp.Dir, "x.json.corrupt-*"));
        Assert.Equal("first", JsonSerializer.Deserialize<Doc>(File.ReadAllText(path), J)!.Name); // restored in place
    }

    [Fact]
    public void A_missing_file_comes_back_from_its_backup_and_empty_files_count_as_damaged()
    {
        using var tmp = new TempData();
        Directory.CreateDirectory(tmp.Dir);
        var path = Path.Combine(tmp.Dir, "y.json");
        Assert.Null(SafeFile.ReadJson<Doc>(path, J));
        SafeFile.WriteJson(path, new Doc("a"), J);
        SafeFile.WriteJson(path, new Doc("b"), J);
        File.Delete(path);
        Assert.Equal("a", SafeFile.ReadJson<Doc>(path, J)!.Name);

        File.WriteAllText(path, "");
        File.Delete(path + ".bak");
        Assert.Null(SafeFile.ReadJson<Doc>(path, J));                  // defaults, but the empty file is kept aside
        Assert.Single(Directory.GetFiles(tmp.Dir, "y.json.corrupt-*"));
    }
}

public class BackupTests
{
    private static Hello Hello(string node) => new(node, "Node " + node, Proto.Version, "1.0.0", "buf", 0, 48000, [1000],
        [new MicInfo(0, "Mic 1", null, null, null, null, true)]);

    private static SeqRecord Sec(long seq, long ts, double lc) =>
        new(seq, new LevelRecord { Kind = RecordKind.Second, ChannelIndex = 0, Ts = ts, Coverage = 1, LCeq = lc, LAeq = lc - 3, LZeq = lc, LCpeak = lc + 10, LCSmax = lc, LASmax = lc, Calibrated = true });

    /// <summary>A PC with node, calibration, Hub settings, plan, mic names, schedule, history and a password.</summary>
    private static void Populate(TempData d, string nodeId, string hubName, double level)
    {
        var store = new NodeConfigStore(d.Paths);
        var cal = new CalibrationStore(d.Paths.CalibrationDir);
        var p = cal.Save(new CalibrationProfile { Name = hubName + " cal", OffsetDb = 120 });
        store.Save(new NodeConfig
        {
            NodeId = nodeId, NodeName = hubName + " PC",
            Inputs = [new InputConfig { ChannelIndex = 0, Name = "FOH mic", ProfileId = p.Id }],
        });
        var hs = new HubSettingsStore(d.Paths);
        hs.Save(hs.Current with { Name = hubName });
        new PlanStore(d.Paths).Save(new Plan { Floors = [new Floor { Id = "main", Name = hubName + " floor", Width = 10, Height = 10 }] });
        using var hub = new HubStore(d.Paths);
        hub.UpsertNode(Hello(nodeId), null, NodeAcceptance.Accepted);
        hub.UpdateMic(HubStore.MicKey(nodeId, 0), hubName + " mic", hidden: false);
        hub.StoreRecords(nodeId, [Sec(1, 1000, level), Sec(2, 1001, level)]);
        hub.ReplaceSchedule([new ScheduleRow(0, hubName + " service", 0, 600, 60, true)]);
        new AdminAuth(d.Paths).SetPassword(hubName + "-password");
    }

    private static string[] All => [BackupArchive.Node, BackupArchive.Hub, BackupArchive.Admin, BackupArchive.History];

    [Fact]
    public void Settings_restore_keeps_this_pcs_identity_and_history()
    {
        using var a = new TempData();
        using var b = new TempData();
        Populate(a, "nodeA", "Old", 90);
        Populate(b, "nodeB", "New", 70);
        var file = Path.Combine(a.Dir, "out" + BackupArchive.Extension);
        var m = BackupArchive.Create(a.Dir, file, "1.0.2", "test", includeHistory: false);
        Assert.Equal(["node", "hub", "admin"], m.Sections);
        Assert.Equal("nodeA", m.NodeId);
        Assert.Equal("Old", m.HubName);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var r = BackupArchive.Restore(b.Dir, file, new RestoreOptions { Sections = [.. All], TakeOverIdentity = false }, "1.0.2");
        Assert.True(r.Ok);
        Assert.NotNull(BackupArchive.FindBackup(b.Dir, r.SafetyBackup!));

        var cfg = new NodeConfigStore(b.Paths).Load();
        Assert.Equal("nodeB", cfg.NodeId);                                  // kept this PC's identity
        Assert.Equal("Old PC", cfg.NodeName);
        var profiles = new CalibrationStore(b.Paths.CalibrationDir).List();
        Assert.Equal("Old cal", Assert.Single(profiles).Name);              // replaced, not merged
        Assert.Equal(cfg.Inputs[0].ProfileId, profiles[0].Id);
        var hs = new HubSettingsStore(b.Paths).Current;
        Assert.Equal("Old", hs.Name);
        Assert.Equal("Old floor", new PlanStore(b.Paths).Current.Floors[0].Name);
        Assert.True(new AdminAuth(b.Paths).Verify("Old-password", out _));

        using var hub = new HubStore(b.Paths);
        Assert.Equal("Old mic", hub.ListMics().Single(x => x.Key == HubStore.MicKey("nodeA", 0)).DisplayName);
        Assert.Equal("Old service", Assert.Single(hub.ListSchedule()).Name);
        Assert.Equal(2, hub.SumSeconds(HubStore.MicKey("nodeB", 0), 0, 5000).Seconds);   // this PC's history kept
        Assert.Equal(0, hub.SumSeconds(HubStore.MicKey("nodeA", 0), 0, 5000).Seconds);   // none imported
        Assert.Equal(0, hub.ListNodes().Single(n => n.Id == "nodeA").LastSeq);          // node re-sends from scratch
    }

    [Fact]
    public void Full_restore_with_takeover_replaces_history_and_identity()
    {
        using var a = new TempData();
        using var b = new TempData();
        Populate(a, "nodeA", "Old", 90);
        Populate(b, "nodeB", "New", 70);
        var oldHubId = new HubSettingsStore(a.Paths).Current.HubId;
        var file = Path.Combine(a.Dir, "full" + BackupArchive.Extension);
        Assert.True(BackupArchive.Create(a.Dir, file, "1.0.2", "test", includeHistory: true).IncludesHistory);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        BackupArchive.QueueRestore(b.Dir, file, new RestoreOptions { Sections = [.. All], TakeOverIdentity = true });
        var r = BackupArchive.ApplyPending(b.Dir, "1.0.2");
        Assert.True(r!.Ok, r.Message);
        Assert.False(BackupArchive.RestorePending(b.Dir));
        Assert.Equal(r.Message, BackupArchive.LastResult(b.Dir)!.Message);

        Assert.Equal("nodeA", new NodeConfigStore(b.Paths).Load().NodeId);
        Assert.Equal(oldHubId, new HubSettingsStore(b.Paths).Current.HubId);
        using var hub = new HubStore(b.Paths);
        Assert.Equal(2, hub.SumSeconds(HubStore.MicKey("nodeA", 0), 0, 5000).Seconds);
        Assert.Equal(0, hub.SumSeconds(HubStore.MicKey("nodeB", 0), 0, 5000).Seconds);
    }

    [Fact]
    public void Only_chosen_sections_are_restored_and_bad_files_are_rejected()
    {
        using var a = new TempData();
        using var b = new TempData();
        Populate(a, "nodeA", "Old", 90);
        Populate(b, "nodeB", "New", 70);
        var file = Path.Combine(a.Dir, "x" + BackupArchive.Extension);
        BackupArchive.Create(a.Dir, file, "1.0.2", "test", includeHistory: false);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        BackupArchive.Restore(b.Dir, file, new RestoreOptions { Sections = [BackupArchive.Node] }, "1.0.2");
        Assert.Equal("Old PC", new NodeConfigStore(b.Paths).Load().NodeName);
        Assert.Equal("New", new HubSettingsStore(b.Paths).Current.Name);
        Assert.True(new AdminAuth(b.Paths).Verify("New-password", out _));

        var junk = Path.Combine(b.Dir, "junk" + BackupArchive.Extension);
        File.WriteAllText(junk, "not a zip");
        Assert.Throws<InvalidDataException>(() => BackupArchive.ReadManifest(junk));
        BackupArchive.QueueRestore(b.Dir, junk, new RestoreOptions { Sections = [BackupArchive.Node] });
        Assert.False(BackupArchive.ApplyPending(b.Dir, "1.0.2")!.Ok);
        Assert.False(BackupArchive.RestorePending(b.Dir));                  // no retry loop on the next start
        Assert.Equal("Old PC", new NodeConfigStore(b.Paths).Load().NodeName);
    }

    [Fact]
    public void A_new_version_saves_the_old_settings_once_and_old_backups_are_pruned()
    {
        using var a = new TempData();
        Populate(a, "nodeA", "Old", 90);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.NotNull(BackupArchive.SnapshotIfVersionChanged(a.Dir, "1.0.2"));   // upgraded from a version without the marker
        Assert.Null(BackupArchive.SnapshotIfVersionChanged(a.Dir, "1.0.2"));
        var snap = Assert.Single(BackupArchive.List(a.Dir));
        Assert.StartsWith("before-update-1.0.2-", snap.Name);
        Assert.Equal("nodeA", BackupArchive.ReadManifest(snap.FullName).NodeId);

        for (var i = 0; i < 20; i++)
            File.WriteAllText(Path.Combine(BackupArchive.BackupsDir(a.Dir), $"daily-2026010{i % 10}-0000{i:00}{BackupArchive.Extension}"), "");
        BackupArchive.Prune(a.Dir);
        Assert.Equal(14, BackupArchive.List(a.Dir).Count(f => f.Name.StartsWith("daily-")));
        Assert.Null(BackupArchive.FindBackup(a.Dir, "..\\node.json"));
    }
}
