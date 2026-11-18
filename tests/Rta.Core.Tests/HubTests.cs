using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Rta.App.Hub;
using Rta.App.Node;
using Rta.App.Security;
using Rta.Core.Calibration;
using Rta.Core.Dsp;
using Rta.Core.Logging;
using Rta.Core.Metering;
using Rta.Core.Protocol;

namespace Rta.Core.Tests;

/// <summary>A throwaway data directory per test.</summary>
public sealed class TempData : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "rta-test-" + Guid.NewGuid().ToString("N"));
    public NodePaths Paths { get; }
    public TempData()
    {
        Paths = new NodePaths(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DataDir"] = Dir }).Build());
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, true); } catch (IOException) { /* file still closing */ }
    }
}

public class RecordBuilderTests
{
    private static readonly IReadOnlyList<OctaveBand> Bands = new ThirdOctaveBank(48000).Bands;

    private static MeterBlock Block(DateTimeOffset end, double db) => new(
        end, 0.1, Db.ToMeanSquare(db), Db.ToMeanSquare(db - 2), Db.ToMeanSquare(db),
        0, Math.Pow(10, (db + 3) / 20), 0, 0, 0, Db.ToMeanSquare(db), Db.ToMeanSquare(db - 2), Db.ToMeanSquare(db),
        Enumerable.Repeat(Db.ToMeanSquare(db - 15), Bands.Count).ToArray(), false);

    [Fact]
    public void Blocks_become_aligned_second_and_minute_records_with_calibration()
    {
        var profile = new CalibrationProfile { Name = "p", OffsetDb = 100, Quality = CalibrationQuality.High };
        var rb = new RecordBuilder(2, Bands, () => profile);
        var recs = new List<LevelRecord>();
        rb.RecordReady += recs.Add;

        // 70 s of blocks starting at 12:00:00.000 UTC: first 30 s at -20 dBFS, then -30.
        var t0 = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        for (var i = 1; i <= 700; i++) rb.Add(Block(t0.AddMilliseconds(i * 100), i <= 300 ? -20 : -30));
        rb.Flush();

        var secs = recs.Where(r => r.Kind == RecordKind.Second).ToList();
        var mins = recs.Where(r => r.Kind == RecordKind.Minute).ToList();
        Assert.Equal(70, secs.Count);
        Assert.All(secs, s => Assert.InRange(s.Coverage, 0.99, 1.01));
        Assert.Equal(t0.ToUnixTimeSeconds(), secs[0].Ts);
        Assert.InRange(secs[0].LCeq, 79.99, 80.01);           // -20 dBFS + 100
        Assert.InRange(secs[0].LAeq, 77.99, 78.01);
        Assert.InRange(secs[^1].LCeq, 69.99, 70.01);
        Assert.Equal(2, secs[0].ChannelIndex);
        Assert.Equal(CalibrationQuality.High, secs[0].Quality);
        Assert.Null(secs[0].Bands);

        Assert.Equal(2, mins.Count);
        Assert.Equal(t0.ToUnixTimeSeconds(), mins[0].Ts);
        var expected = 10 * Math.Log10((30 * 0.01 + 30 * 0.001) / 60) + 100;
        Assert.InRange(mins[0].LCeq, expected - 0.01, expected + 0.01);
        Assert.InRange(mins[0].Coverage, 59.9, 60.1);
        Assert.Equal(Bands.Count, mins[0].Bands!.Length);
        Assert.InRange(mins[1].Coverage, 9.9, 10.1);           // partial minute flushed
    }
}

public class HubStoreTests
{
    private static Hello Hello(string node, string buffer, params int[] channels) => new(node, "Node " + node, Proto.Version, "1.0.0", buffer, 0, 48000, [1000],
        channels.Select(c => new MicInfo(c, $"Mic {c + 1}", null, null, null, null, true)).ToList());

    private static SeqRecord Sec(long seq, int ch, long ts, double lc, double la = 0) =>
        new(seq, new LevelRecord { Kind = RecordKind.Second, ChannelIndex = ch, Ts = ts, Coverage = 1, LCeq = lc, LAeq = la == 0 ? lc - 3 : la, LZeq = lc, LCpeak = lc + 10, LCSmax = lc + 1, LASmax = lc - 2, Calibrated = true });

    [Fact]
    public void Records_are_stored_idempotently_and_ack_advances()
    {
        using var tmp = new TempData();
        using var store = new HubStore(tmp.Paths);
        store.UpsertNode(Hello("n1", "b1", 0, 1), "10.0.0.5", NodeAcceptance.Accepted);
        var ack = store.StoreRecords("n1", [Sec(1, 0, 1000, 90), Sec(2, 0, 1001, 100), Sec(3, 1, 1000, 80)]);
        Assert.Equal(3, ack);
        // Resending (after a dropped ack) must not duplicate.
        ack = store.StoreRecords("n1", [Sec(2, 0, 1001, 100), Sec(4, 0, 1002, 90)]);
        Assert.Equal(4, ack);

        var s = store.GetSeries(HubStore.MicKey("n1", 0), 900, 2000);
        Assert.Equal("second", s.Resolution);
        Assert.Equal([1000L, 1001, 1002], s.Ts);
        Assert.Equal([90.0, 100, 90], s.LC);

        var (ea, ec, secs, pk) = store.SumSeconds(HubStore.MicKey("n1", 0), 1000, 1003);
        Assert.Equal(3, secs);
        Assert.InRange(10 * Math.Log10(ec / secs), 96.0, 96.05);  // energy average of 90, 100, 90 = 10·log10(4e9)
        Assert.Equal(110, pk);
        Assert.Equal(1, store.SecondsAbove(HubStore.MicKey("n1", 0), 1000, 1003, true, 98));
    }

    [Fact]
    public void Hidden_mics_stay_hidden_when_the_node_reconnects_but_forgotten_nodes_come_back()
    {
        using var tmp = new TempData();
        using var store = new HubStore(tmp.Paths);
        store.UpsertNode(Hello("n1", "b1", 0, 1), null, NodeAcceptance.Accepted);
        store.UpdateMic(HubStore.MicKey("n1", 1), null, hidden: true);
        store.UpsertNode(Hello("n1", "b1", 0, 1), null, NodeAcceptance.Accepted);      // reconnect / hello
        Assert.True(store.ListMics().Single(m => m.ChannelIndex == 1).Hidden);
        Assert.False(store.ListMics().Single(m => m.ChannelIndex == 0).Hidden);

        store.ForgetNode("n1");
        Assert.All(store.ListMics(), m => Assert.True(m.Hidden));
        store.UpsertNode(Hello("n1", "b1", 0, 1), null, NodeAcceptance.Accepted);      // node comes back
        Assert.False(store.ListMics().Single(m => m.ChannelIndex == 0).Hidden);       // un-hidden again
        Assert.True(store.ListMics().Single(m => m.ChannelIndex == 1).Hidden);        // user's choice kept
    }

    [Fact]
    public void New_buffer_id_resets_the_ack_and_acceptance_is_kept()
    {
        using var tmp = new TempData();
        using var store = new HubStore(tmp.Paths);
        store.UpsertNode(Hello("n1", "b1", 0), null, NodeAcceptance.Pending);
        store.SetAcceptance("n1", NodeAcceptance.Accepted);
        store.StoreRecords("n1", [Sec(50, 0, 1000, 90)]);
        Assert.Equal(50, store.UpsertNode(Hello("n1", "b1", 0), null, NodeAcceptance.Pending).LastSeq);
        var row = store.UpsertNode(Hello("n1", "b2", 0), null, NodeAcceptance.Pending);
        Assert.Equal(0, row.LastSeq);
        Assert.Equal(NodeAcceptance.Accepted, row.Acceptance);
    }

    [Fact]
    public void Minute_bands_round_trip_and_histogram_counts_seconds()
    {
        using var tmp = new TempData();
        using var store = new HubStore(tmp.Paths);
        store.UpsertNode(Hello("n1", "b1", 0), null, NodeAcceptance.Accepted);
        var minute = new SeqRecord(1, new LevelRecord { Kind = RecordKind.Minute, ChannelIndex = 0, Ts = 60, Coverage = 60, LCeq = 91.23, LAeq = 88, Bands = [70.12, -45.5, 99.99] });
        store.StoreRecords("n1", [minute, Sec(2, 0, 60, 90.4), Sec(3, 0, 61, 90.6), Sec(4, 0, 62, 95.1)]);
        var (ts, bands) = store.GetSpectrogram(HubStore.MicKey("n1", 0), 0, 120);
        Assert.Equal([60L], ts);
        Assert.Equal([70.12, -45.5, 99.99], bands[0]);
        var (db, _, c) = store.GetHistogram(HubStore.MicKey("n1", 0), 0, 120);
        Assert.Equal(2, c[Array.IndexOf(db, 90)]);
        Assert.Equal(1, c[Array.IndexOf(db, 95)]);
        var (maxA, maxC) = store.MaxMinute(HubStore.MicKey("n1", 0), 0, 120);
        Assert.Equal(88, maxA);
        Assert.Equal(91.23, maxC);
    }
}

public class ServiceSchedulerTests
{
    [Fact]
    public void Occurrence_start_is_this_weeks_or_last_weeks()
    {
        var sundayTen = new ScheduleRow(1, "Sunday", 0, 10 * 60, 90, true);
        // Wednesday 18 Nov 2026 local → previous Sunday 15 Nov 10:00.
        var wed = new DateTimeOffset(new DateTime(2026, 11, 18, 9, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 18, 9, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 15, 10, 0, 0), ServiceScheduler.OccurrenceStart(sundayTen, wed).DateTime);
        // Sunday 09:00 (before start) → last week's Sunday.
        var sun9 = new DateTimeOffset(new DateTime(2026, 11, 22, 9, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 22, 9, 0, 0)));
        Assert.Equal(new DateTime(2026, 11, 15, 10, 0, 0), ServiceScheduler.OccurrenceStart(sundayTen, sun9).DateTime);
    }

    [Fact]
    public void Tick_starts_ends_and_respects_a_manual_stop()
    {
        using var tmp = new TempData();
        using var store = new HubStore(tmp.Paths);
        var state = new HubState(store, new HubSettingsStore(tmp.Paths));
        var sched = new ServiceScheduler(store, state, NullLogger<ServiceScheduler>.Instance);
        store.ReplaceSchedule([new ScheduleRow(0, "Sunday AM", 0, 10 * 60, 90, true)]);

        DateTimeOffset Local(int day, int h, int m)
        {
            var d = new DateTime(2026, 11, day, h, m, 0);
            return new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d));
        }

        sched.Tick(Local(22, 9, 59));
        Assert.Null(store.ActiveService());
        sched.Tick(Local(22, 10, 5));
        var s = store.ActiveService();
        Assert.NotNull(s);
        Assert.Equal("Sunday AM", s!.Name);
        Assert.Equal(Local(22, 10, 0).ToUnixTimeSeconds(), s.StartTs);
        Assert.Equal(s.Id, state.Service?.Id);

        // Manual stop at 10:30 → not restarted at 10:35.
        store.EndService(s.Id, Local(22, 10, 30).ToUnixTimeSeconds());
        sched.Tick(Local(22, 10, 35));
        Assert.Null(store.ActiveService());

        // Next week it starts again and ends on time.
        sched.Tick(Local(29, 10, 1));
        var next = store.ActiveService();
        Assert.NotNull(next);
        sched.Tick(Local(29, 11, 31));
        Assert.Null(store.ActiveService());
        Assert.Equal(Local(29, 11, 30).ToUnixTimeSeconds(), store.GetService(next!.Id)!.EndTs);
    }
}

public class AlertEngineTests
{
    private static LiveMic Mic(double lcs) => new(0, lcs - 2, lcs, lcs - 2, lcs, lcs, lcs, lcs, lcs, lcs + 10, 2, 60, true, false, [], []);

    [Fact]
    public void Alerts_need_hold_time_and_clear_with_hysteresis()
    {
        using var tmp = new TempData();
        using var store = new HubStore(tmp.Paths);
        var settings = new HubSettingsStore(tmp.Paths);
        settings.Save(settings.Current with
        {
            AlertRules = [new AlertRule { Id = 1, Name = "LCS", Metric = AlertMetric.LCS, Warn = 95, Alarm = 100, HysteresisDb = 2, MinDurationSeconds = 3 }],
        });
        var state = new HubState(store, settings);
        var hello = new Hello("n1", "Node", Proto.Version, "1", "b", 0, 48000, [], [new MicInfo(0, "Mic 1", null, null, null, null, true)]);
        store.UpsertNode(hello, null, NodeAcceptance.Accepted);
        var conn = new NodeConnection("n1", null, _ => Task.CompletedTask) { Hello = hello };
        state.Register(conn);
        var engine = new AlertEngine(state, store, settings, NullLogger<AlertEngine>.Instance);

        var t = DateTimeOffset.UtcNow;
        void At(double seconds, double lcs)
        {
            conn.LastMessage = DateTimeOffset.UtcNow;
            conn.Live = new LiveUpdate(0, true, null, [Mic(lcs)], []);
            engine.Evaluate(t.AddSeconds(seconds));
        }

        At(0, 96); At(2, 96);
        Assert.Empty(state.ActiveAlerts);              // not held long enough
        At(3.1, 96);
        Assert.Equal("warn", Assert.Single(state.ActiveAlerts).Level);
        At(4, 101); At(6, 101);
        Assert.Equal("warn", state.ActiveAlerts[0].Level);
        At(7.2, 101);
        Assert.Equal("alarm", state.ActiveAlerts[0].Level);
        At(8, 99);                                      // within hysteresis of 100 → stays alarm
        Assert.Equal("alarm", state.ActiveAlerts[0].Level);
        At(9, 97);                                      // below 98 → back to warn
        Assert.Equal("warn", state.ActiveAlerts[0].Level);
        At(10, 92);                                     // below 93 → clear
        Assert.Empty(state.ActiveAlerts);

        var events = store.ListAlertEvents(0, long.MaxValue / 2);
        Assert.Equal(3, events.Count);                  // warn, alarm, warn
        Assert.All(events, e => Assert.NotNull(e.EndTs));
        Assert.Contains(events, e => e.Level == "alarm" && e.PeakValue == 101);
    }
}

public class NodeHubClientTests
{
    [Theory]
    [InlineData("192.168.1.20", "ws://192.168.1.20:5180/ws/node")]
    [InlineData("192.168.1.20:5200", "ws://192.168.1.20:5200/ws/node")]
    [InlineData("http://hub.local:5180", "ws://hub.local:5180/ws/node")]
    [InlineData("https://hub.example:8443/", "wss://hub.example:8443/ws/node")]
    [InlineData("  ", null)]
    public void Hub_addresses_are_normalised(string input, string? expected)
    {
        Assert.Equal(expected, HubClient.NormaliseUrl(input));
    }

    [Fact]
    public void Node_buffer_assigns_sequence_numbers_and_reads_after_ack()
    {
        using var tmp = new TempData();
        using var buf = new NodeBuffer(tmp.Paths);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < 5; i++) buf.Append(new LevelRecord { Kind = RecordKind.Second, Ts = now + i, LCeq = 90 + i });
        Assert.Equal(5, buf.LastSeq);
        var after = buf.ReadAfter(2, 10);
        Assert.Equal([3L, 4, 5], after.Select(r => r.Seq));
        Assert.Equal(92, after[0].Record.LCeq);
        Assert.Equal(3, buf.PendingAfter(2));
        Assert.False(string.IsNullOrEmpty(buf.BufferId));
    }
}

public class AdminAuthTests
{
    [Fact]
    public void Password_set_verify_and_lockout()
    {
        using var tmp = new TempData();
        var auth = new AdminAuth(tmp.Paths);
        Assert.False(auth.PasswordSet);
        Assert.Throws<ArgumentException>(() => auth.SetPassword("123"));
        auth.SetPassword("correct horse");
        Assert.True(new AdminAuth(tmp.Paths).PasswordSet);   // persisted
        Assert.True(auth.Verify("correct horse", out _));
        for (var i = 0; i < 5; i++) Assert.False(auth.Verify("wrong", out _));
        Assert.False(auth.Verify("correct horse", out var err)); // locked out for a minute
        Assert.Contains("Too many", err);
    }
}
