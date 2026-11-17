using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Rta.Core.Logging;
using Rta.Core.Protocol;

namespace Rta.App.Hub;

public sealed record MicUpdate(string? DisplayName, bool Hidden);
public sealed record RenameBody(string Name);
public sealed record ServiceStartBody(string? Name);
public sealed record ServiceEditBody(string Name, long StartTs, long? EndTs);
public sealed record HubSettingsBody(string Name, int SecondsRetentionDays, bool AutoAcceptNodes);

public sealed record MicServiceSummary(
    string MicKey, string Label, double? LAeq, double? LCeq, double? MaxLAeq1m, double? MaxLCeq1m, double? LCpeakMax,
    double Seconds, int SecondsAboveWarnC, int SecondsAboveAlarmC);

public sealed record ServiceSummary(ServiceRow Service, IReadOnlyList<MicServiceSummary> Mics, IReadOnlyList<AlertEventRow> Alerts, double WarnC, double AlarmC);

/// <summary>The Hub's API under /api/hub and the dashboard live stream at /ws/hub.</summary>
public static class HubEndpoints
{
    public static void MapHubEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map(Proto.NodeSocketPath, (HttpContext ctx, NodeIngest ingest) => ingest.HandleAsync(ctx));

        var g = app.MapGroup("/api/hub");

        g.MapGet("/live", (HubState s) => s.Snapshot());

        // ----- settings -----
        g.MapGet("/settings", (HubSettingsStore s) => s.Current);
        g.MapPut("/settings", (HubSettingsBody b, HubSettingsStore s, HubState st) =>
        {
            if (string.IsNullOrWhiteSpace(b.Name)) return Results.Problem("Hub name is required.", statusCode: 400);
            var saved = s.Save(s.Current with { Name = b.Name.Trim(), SecondsRetentionDays = b.SecondsRetentionDays, AutoAcceptNodes = b.AutoAcceptNodes });
            st.NotifyChanged();
            return Results.Ok(saved);
        });

        // ----- nodes -----
        g.MapGet("/nodes", (HubStore store) => store.ListNodes());
        g.MapPost("/nodes/{id}/accept", async (string id, HubStore store, NodeIngest ingest, HubState st) =>
        {
            if (store.GetNode(id) is null) return Results.NotFound();
            store.SetAcceptance(id, NodeAcceptance.Accepted);
            st.InvalidateMics();
            await ingest.NotifyAcceptanceAsync(id);
            return Results.Ok();
        });
        g.MapPost("/nodes/{id}/reject", async (string id, HubStore store, NodeIngest ingest, HubState st) =>
        {
            if (store.GetNode(id) is null) return Results.NotFound();
            store.SetAcceptance(id, NodeAcceptance.Rejected);
            st.InvalidateMics();
            await ingest.NotifyAcceptanceAsync(id);
            return Results.Ok();
        });
        g.MapPut("/nodes/{id}", (string id, RenameBody b, HubStore store, HubState st) =>
        {
            if (string.IsNullOrWhiteSpace(b.Name)) return Results.Problem("Name is required.", statusCode: 400);
            store.RenameNode(id, b.Name.Trim());
            st.InvalidateMics();
            return Results.Ok();
        });
        g.MapDelete("/nodes/{id}", (string id, HubStore store, HubState st) =>
        {
            if (HubState.IsOnline(st.Connections.GetValueOrDefault(id)))
                return Results.Problem("This node is connected. Reject it first, or turn it off, then remove it.", statusCode: 409);
            store.ForgetNode(id);
            st.InvalidateMics();
            return Results.NoContent();
        });

        // ----- mics -----
        g.MapGet("/mics", (HubStore store) => store.ListMics());
        g.MapPut("/mics/{key}", (string key, MicUpdate b, HubStore store, HubState st) =>
        {
            store.UpdateMic(key, b.DisplayName, b.Hidden);
            st.InvalidateMics();
            return Results.Ok();
        });

        // ----- history -----
        g.MapGet("/history", (string mics, long from, long to, HubStore store) =>
        {
            if (to <= from) return Results.Problem("'to' must be after 'from'.", statusCode: 400);
            var list = new List<Series>();
            foreach (var key in mics.Split(',', StringSplitOptions.RemoveEmptyEntries).Take(32))
            {
                try { list.Add(store.GetSeries(key, from, to)); }
                catch (KeyNotFoundException) { /* unknown mic: skip */ }
            }
            return Results.Ok(list);
        });
        g.MapGet("/spectrogram", (string mic, long from, long to, HubStore store) =>
        {
            try
            {
                var (ts, bands) = store.GetSpectrogram(mic, from, to);
                return Results.Ok(new { ts, bands });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });
        g.MapGet("/histogram", (string mic, long from, long to, HubStore store) =>
        {
            try
            {
                var (db, a, c) = store.GetHistogram(mic, from, to);
                return Results.Ok(new { db, secondsA = a, secondsC = c });
            }
            catch (KeyNotFoundException) { return Results.NotFound(); }
        });

        // ----- services -----
        g.MapGet("/services", (long from, long to, HubStore store) => store.ListServices(from, to));
        g.MapGet("/services/{id:long}", (long id, HubStore store, HubSettingsStore settings) =>
        {
            var s = store.GetService(id);
            return s is null ? Results.NotFound() : Results.Ok(Summarise(s, store, settings));
        });
        g.MapPost("/services/start", (ServiceStartBody b, HubStore store, HubState st) =>
        {
            var active = store.ActiveService();
            var now = HubStore.Now();
            if (active is not null) store.EndService(active.Id, now);
            var row = store.StartService(string.IsNullOrWhiteSpace(b.Name) ? $"Service {DateTime.Now:ddd d MMM HH:mm}" : b.Name.Trim(), now, "manual", null);
            st.SetActiveService(row);
            return Results.Ok(row);
        });
        g.MapPost("/services/stop", (HubStore store, HubState st) =>
        {
            var active = store.ActiveService();
            if (active is null) return Results.Problem("No service is running.", statusCode: 409);
            store.EndService(active.Id, HubStore.Now());
            st.SetActiveService(null);
            return Results.Ok();
        });
        g.MapPut("/services/{id:long}", (long id, ServiceEditBody b, HubStore store, HubState st) =>
        {
            if (store.GetService(id) is null) return Results.NotFound();
            if (b.EndTs is long e && e <= b.StartTs) return Results.Problem("End must be after start.", statusCode: 400);
            store.UpdateService(id, string.IsNullOrWhiteSpace(b.Name) ? "Service" : b.Name.Trim(), b.StartTs, b.EndTs);
            st.SetActiveService(store.ActiveService());
            return Results.Ok();
        });
        g.MapDelete("/services/{id:long}", (long id, HubStore store, HubState st) =>
        {
            store.DeleteService(id);
            st.SetActiveService(store.ActiveService());
            return Results.NoContent();
        });

        // ----- schedule -----
        g.MapGet("/schedule", (HubStore store) => store.ListSchedule());
        g.MapPut("/schedule", (List<ScheduleRow> rows, HubStore store) =>
        {
            foreach (var r in rows)
            {
                if (r.Day is < 0 or > 6) return Results.Problem("Day must be 0 (Sunday) to 6 (Saturday).", statusCode: 400);
                if (r.StartMinute is < 0 or >= 1440) return Results.Problem("Start time is out of range.", statusCode: 400);
                if (r.DurationMinutes is < 5 or > 720) return Results.Problem("Duration must be 5 minutes to 12 hours.", statusCode: 400);
            }
            store.ReplaceSchedule(rows);
            return Results.Ok(store.ListSchedule());
        });

        // ----- alerts -----
        g.MapGet("/alerts/rules", (HubSettingsStore s) => s.Current.AlertRules);
        g.MapPut("/alerts/rules", (List<AlertRule> rules, HubSettingsStore s) =>
        {
            foreach (var r in rules)
            {
                if (r.Alarm < r.Warn) return Results.Problem($"'{r.Name}': alarm level must be at or above the warn level.", statusCode: 400);
                if (r.HysteresisDb is < 0 or > 10) return Results.Problem($"'{r.Name}': hysteresis must be 0–10 dB.", statusCode: 400);
                if (r.MinDurationSeconds is < 0 or > 600) return Results.Problem($"'{r.Name}': minimum duration must be 0–600 s.", statusCode: 400);
            }
            return Results.Ok(s.Save(s.Current with { AlertRules = rules }).AlertRules);
        });
        g.MapGet("/alerts/events", (long from, long to, HubStore store) => store.ListAlertEvents(from, to));

        // ----- dashboard live stream -----
        app.Map("/ws/hub", async (HttpContext ctx, HubState st, IOptions<JsonOptions> json) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            var ct = ctx.RequestAborted;
            try
            {
                while (ws.State == WebSocketState.Open && await timer.WaitForNextTickAsync(ct))
                {
                    var payload = JsonSerializer.SerializeToUtf8Bytes(st.Snapshot(), json.Value.SerializerOptions);
                    await ws.SendAsync(payload, WebSocketMessageType.Text, true, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        });
    }

    public static ServiceSummary Summarise(ServiceRow s, HubStore store, HubSettingsStore settings)
    {
        var end = s.EndTs ?? HubStore.Now() + 1;
        var rule = settings.Current.AlertRules.FirstOrDefault(r => r.Enabled && r.Metric is AlertMetric.LCeq1m or AlertMetric.LCS)
                   ?? new AlertRule();
        var mics = new List<MicServiceSummary>();
        foreach (var m in store.ListMics())
        {
            var (ea, ec, secs, pk) = store.SumSeconds(m.Key, s.StartTs, end);
            if (secs <= 0) continue;
            var (maxA, maxC) = store.MaxMinute(m.Key, s.StartTs, end);
            mics.Add(new MicServiceSummary(m.Key, m.Label,
                Math.Round(10 * Math.Log10(ea / secs), 1), Math.Round(10 * Math.Log10(ec / secs), 1),
                double.IsNaN(maxA) ? null : maxA, double.IsNaN(maxC) ? null : maxC,
                double.IsNegativeInfinity(pk) ? null : pk, secs,
                store.SecondsAbove(m.Key, s.StartTs, end, true, rule.Warn),
                store.SecondsAbove(m.Key, s.StartTs, end, true, rule.Alarm)));
        }
        return new ServiceSummary(s, mics, store.ListAlertEvents(s.StartTs, end), rule.Warn, rule.Alarm);
    }
}
