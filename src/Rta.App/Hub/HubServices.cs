using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using Rta.Core.Protocol;

namespace Rta.App.Hub;

/// <summary>Broadcasts a small UDP beacon every 2 s on each IPv4 network so nodes can find this Hub.</summary>
public sealed class HubBeaconService(HubSettingsStore settings, AppMode mode, ILogger<HubBeaconService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var warned = false;
        while (!ct.IsCancellationRequested)
        {
            var s = settings.Current;
            var payload = JsonSerializer.SerializeToUtf8Bytes(new HubBeacon(Proto.BeaconApp, Proto.Version, s.HubId, s.Name, mode.Port), Proto.Json);
            foreach (var (local, broadcast) in Ipv4Networks())
            {
                try
                {
                    using var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                    await udp.SendAsync(payload, new IPEndPoint(broadcast, Proto.BeaconPort), ct);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) when (!warned)
                {
                    warned = true;
                    log.LogWarning(ex, "Hub beacon could not be sent on {Address}", local);
                }
                catch { /* already warned once */ }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { return; }
        }
    }

    private static IEnumerable<(IPAddress Local, IPAddress Broadcast)> Ipv4Networks()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                var bc = new byte[4];
                for (var i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                yield return (ua.Address, new IPAddress(bc));
            }
        }
    }
}

/// <summary>
/// Starts and ends services from the weekly schedule (local time). Manual start/stop always wins:
/// a scheduled occurrence that was stopped early is not restarted, and nothing is started while
/// a manual service is running. Manual services end automatically after 6 hours.
/// </summary>
public sealed class ServiceScheduler(HubStore store, HubState state, ILogger<ServiceScheduler> log) : BackgroundService
{
    public static readonly TimeSpan ManualLimit = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        state.SetActiveService(store.ActiveService());
        while (!ct.IsCancellationRequested)
        {
            try { Tick(DateTimeOffset.Now); }
            catch (Exception ex) { log.LogWarning(ex, "Service scheduler tick failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }
        }
    }

    public void Tick(DateTimeOffset nowLocal)
    {
        var now = nowLocal.ToUnixTimeSeconds();
        var active = store.ActiveService();

        // End services whose time is up.
        if (active is not null)
        {
            var end = active.Source == "schedule" && active.ScheduleId is long sid ? ScheduledEnd(sid, active.StartTs) : active.StartTs + (long)ManualLimit.TotalSeconds;
            if (end is long e && now >= e)
            {
                store.EndService(active.Id, e);
                log.LogInformation("Service '{Name}' ended", active.Name);
                active = null;
                state.SetActiveService(null);
            }
        }
        if (active is not null) return;

        foreach (var s in store.ListSchedule().Where(s => s.Enabled))
        {
            var start = OccurrenceStart(s, nowLocal);
            var startTs = start.ToUnixTimeSeconds();
            var endTs = startTs + s.DurationMinutes * 60L;
            if (now < startTs || now >= endTs) continue;
            if (store.FindScheduledOccurrence(s.Id, startTs) is not null) continue; // already ran (or was stopped early)
            var row = store.StartService(s.Name, startTs, "schedule", s.Id);
            log.LogInformation("Scheduled service '{Name}' started", s.Name);
            state.SetActiveService(row);
            return;
        }
    }

    private long? ScheduledEnd(long scheduleId, long startTs)
    {
        var s = store.ListSchedule().FirstOrDefault(x => x.Id == scheduleId);
        return startTs + (s?.DurationMinutes ?? (int)ManualLimit.TotalMinutes) * 60L;
    }

    /// <summary>This week's occurrence of a schedule entry, in local time.</summary>
    public static DateTimeOffset OccurrenceStart(ScheduleRow s, DateTimeOffset nowLocal)
    {
        var today = nowLocal.Date;
        var diff = (int)nowLocal.DayOfWeek - s.Day;
        var day = today.AddDays(-diff);
        var local = day.AddMinutes(s.StartMinute);
        var start = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local));
        // If this week's start is still in the future, the previous week's might still be running (late-night services).
        if (start > nowLocal) start = start.AddDays(-7);
        return start;
    }
}

/// <summary>Evaluates alert rules against live readings twice a second and logs events.</summary>
public sealed class AlertEngine(HubState state, HubStore store, HubSettingsStore settings, ILogger<AlertEngine> log) : BackgroundService
{
    private sealed class Track
    {
        public string Level = "normal"; // normal | warn | alarm
        public string? Pending;
        public DateTimeOffset PendingSince;
        public long? EventId;
        public long Since;
        public double Peak;
    }

    private readonly Dictionary<(long Rule, string Mic), Track> _tracks = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        store.CloseOpenAlertEvents(HubStore.Now());
        while (!ct.IsCancellationRequested)
        {
            try { Evaluate(DateTimeOffset.UtcNow); }
            catch (Exception ex) { log.LogWarning(ex, "Alert evaluation failed"); }
            try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
        }
    }

    public void Evaluate(DateTimeOffset now)
    {
        var snap = state.Snapshot();
        var rules = settings.Current.AlertRules.Where(r => r.Enabled).ToList();
        var seen = new HashSet<(long, string)>();
        var active = new List<ActiveAlertView>();

        var zones = snap.Zones.Where(z => z.Surveyed).Select(HubState.ZoneAsMic).ToList();
        foreach (var rule in rules)
        {
            // "*" = every mic, "zones:*" = every zone, otherwise one mic or zone key.
            var targets = rule.Target switch
            {
                "*" => snap.Mics.Where(m => !m.Hidden),
                "zones:*" => zones,
                _ => snap.Mics.Where(m => !m.Hidden && m.Key == rule.Target).Concat(zones.Where(z => z.Key == rule.Target)),
            };
            foreach (var mic in targets)
            {
                var key = (rule.Id, mic.Key);
                seen.Add(key);
                if (!_tracks.TryGetValue(key, out var t)) _tracks[key] = t = new Track();
                var value = HubState.MetricValue(mic, rule.Metric);
                if (value is not double v) { Close(t, now); continue; }

                // Desired level with hysteresis relative to the current level.
                string target;
                if (v >= rule.Alarm || (t.Level == "alarm" && v > rule.Alarm - rule.HysteresisDb)) target = "alarm";
                else if (v >= rule.Warn || (t.Level != "normal" && v > rule.Warn - rule.HysteresisDb)) target = "warn";
                else target = "normal";

                if (Rank(target) > Rank(t.Level))
                {
                    // Rising: must hold for MinDurationSeconds.
                    if (t.Pending != target || Rank(target) < Rank(t.Pending ?? "normal")) { t.Pending = target; t.PendingSince = now; }
                    if ((now - t.PendingSince).TotalSeconds >= rule.MinDurationSeconds)
                    {
                        Close(t, now);
                        t.Level = target;
                        t.Pending = null;
                        t.Since = now.ToUnixTimeSeconds();
                        t.Peak = v;
                        t.EventId = store.StartAlertEvent(rule.Id, rule.Name, mic.Key, target, t.Since, v, rule.Metric.ToString());
                        log.LogInformation("Alert {Level}: {Rule} on {Mic} = {Value:0.0}", target, rule.Name, mic.Label, v);
                    }
                }
                else
                {
                    t.Pending = null;
                    if (Rank(target) < Rank(t.Level))
                    {
                        Close(t, now);
                        if (target != "normal")
                        {
                            t.Level = target; t.Since = now.ToUnixTimeSeconds(); t.Peak = v;
                            t.EventId = store.StartAlertEvent(rule.Id, rule.Name, mic.Key, target, t.Since, v, rule.Metric.ToString());
                        }
                    }
                    else if (t.EventId is long id && v > t.Peak)
                    {
                        t.Peak = v;
                        store.UpdateAlertEvent(id, v, null);
                    }
                }

                if (t.Level != "normal")
                    active.Add(new ActiveAlertView(rule.Id, rule.Name, mic.Key, t.Level, rule.Metric, Math.Round(v, 1),
                        t.Level == "alarm" ? rule.Alarm : rule.Warn, t.Since));
            }
        }

        foreach (var key in _tracks.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            Close(_tracks[key], now);
            _tracks.Remove(key);
        }
        state.SetActiveAlerts(active);
    }

    private void Close(Track t, DateTimeOffset now)
    {
        if (t.EventId is long id) store.UpdateAlertEvent(id, t.Peak, now.ToUnixTimeSeconds());
        t.EventId = null;
        t.Level = "normal";
    }

    private static int Rank(string level) => level switch { "alarm" => 2, "warn" => 1, _ => 0 };
}

/// <summary>Deletes per-second rows older than the retention period, once an hour.</summary>
public sealed class RetentionService(HubStore store, HubSettingsStore settings, ILogger<RetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(-settings.Current.SecondsRetentionDays).ToUnixTimeSeconds();
                var n = store.PruneSeconds(cutoff);
                if (n > 0) log.LogInformation("Deleted {Count} per-second rows older than {Days} days", n, settings.Current.SecondsRetentionDays);
            }
            catch (Exception ex) { log.LogWarning(ex, "Retention pass failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { return; }
        }
    }
}
