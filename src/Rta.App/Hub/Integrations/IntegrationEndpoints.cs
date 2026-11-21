using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Rta.App.Hub.Reports;
using Rta.Audio;

namespace Rta.App.Hub.Integrations;

public static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/hub");

        g.MapGet("/integrations", (HubSettingsStore s) => s.Current.Integrations);
        g.MapPut("/integrations", (IntegrationSettings body, HubSettingsStore s) =>
        {
            try { IntegrationSettings.Validate(body); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            if (!string.IsNullOrWhiteSpace(body.Reports.Folder))
            {
                try { Directory.CreateDirectory(body.Reports.Folder); }
                catch (Exception ex) { return Results.Problem($"Report folder can't be used: {ex.Message}", statusCode: 400); }
            }
            return Results.Ok(s.Save(s.Current with { Integrations = body }).Integrations);
        });
        g.MapGet("/integrations/status", (IntegrationService i) => i.Status);
        // A test sends something real (OSC packet, MIDI note, webhook, ProPresenter stage message), so it needs admin like other changes.
        g.MapPost("/integrations/test/{kind}", async (string kind, IntegrationService i, CancellationToken ct) =>
        {
            try { return Results.Ok(new { message = await i.TestAsync(kind, ct) }); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Results.Problem(ex.Message, statusCode: 400); }
        });
        g.MapGet("/midi/devices", () => MidiOutput.Devices());
        g.MapGet("/propresenter/messages", async (HubSettingsStore s, ProPresenterClient pro, CancellationToken ct) =>
        {
            try { return Results.Ok(await pro.MessagesAsync(s.Current.Integrations.ProPresenter, ct)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Results.Problem($"Can't list ProPresenter messages: {ex.Message}", statusCode: 502); }
        });

        // ----- reports & exports -----
        g.MapGet("/export.csv", async (HttpContext ctx, string mics, long from, long to, string? resolution, ReportService r) =>
        {
            if (to <= from || to - from > 366L * 86400) { ctx.Response.StatusCode = 400; await ctx.Response.WriteAsync("Range must be 1 s to 1 year."); return; }
            var minutes = resolution == "minute" || (resolution != "second" && to - from > 3 * 3600);
            ctx.Response.ContentType = "text/csv; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"rta-levels-{DateTimeOffset.FromUnixTimeSeconds(from).ToLocalTime():yyyyMMdd-HHmm}-{(minutes ? "minutes" : "seconds")}.csv\"";
            await r.WriteCsvAsync(ctx.Response.Body, mics.Split(',', StringSplitOptions.RemoveEmptyEntries).Take(64).ToList(), from, to, minutes, ctx.RequestAborted);
        });
        // Opens in the browser by default; ?download=1 saves it as a file.
        g.MapGet("/services/{id:long}/report.pdf", (long id, string? download, HubStore store, ReportService r, HttpContext ctx) =>
        {
            var s = store.GetService(id);
            if (s is null) return Results.NotFound();
            var name = ReportService.FileName(s);
            if (IsDownload(download)) return Results.File(r.ServicePdf(id), "application/pdf", name);
            ctx.Response.Headers.ContentDisposition = $"inline; filename=\"{name}\"";
            return Results.File(r.ServicePdf(id), "application/pdf");
        });
        g.MapGet("/services/{id:long}/export.csv", async (HttpContext ctx, long id, HubStore store, ReportService r) =>
        {
            var s = store.GetService(id);
            if (s is null) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.ContentType = "text/csv; charset=utf-8";
            ctx.Response.Headers.ContentDisposition = $"attachment; filename=\"{Path.GetFileNameWithoutExtension(ReportService.FileName(s))}.csv\"";
            await r.WriteCsvAsync(ctx.Response.Body, store.ListMics().Select(m => m.Key).ToList(), s.StartTs, (s.EndTs ?? HubStore.Now()) + 1, false, ctx.RequestAborted);
        });
        g.MapGet("/reports", (ReportService r) => new { folder = r.Folder, files = r.ListReports().Select(f => new { name = f.Name, bytes = f.Bytes, modified = f.Modified }) });
        g.MapGet("/reports/{name}", (string name, string? download, ReportService r, HttpContext ctx) =>
        {
            if (r.ReportPath(name) is not { } p) return Results.NotFound();
            if (IsDownload(download)) return Results.File(p, "application/pdf", name);
            ctx.Response.Headers.ContentDisposition = $"inline; filename=\"{name}\"";
            return Results.File(p, "application/pdf");
        });

        // ----- public read-only levels API (no sign-in) -----
        var v1 = app.MapGroup("/api/v1");
        v1.MapGet("/levels", (HubState st) => LevelsView.Build(st.Snapshot()));
        v1.MapGet("/levels/{source}", (string source, HubState st) =>
            LevelsView.Find(LevelsView.Build(st.Snapshot()), source) is { } s ? Results.Ok(s) : Results.NotFound());
        // Plain-text single value, e.g. /api/v1/levels/front_left/lceq1m → "84.2" (for title/text sources).
        v1.MapGet("/levels/{source}/{metric}", (string source, string metric, HubState st) =>
        {
            var s = LevelsView.Find(LevelsView.Build(st.Snapshot()), source);
            if (s is null || !s.Values.TryGetValue(LevelsView.MetricName(metric), out var v)) return Results.NotFound();
            return Results.Text(LevelsView.Format(v), "text/plain");
        });
        app.Map("/ws/v1/levels", async (HttpContext ctx, HubState st, IOptions<JsonOptions> json) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            var hz = double.TryParse(ctx.Request.Query["hz"], out var h) ? Math.Clamp(h, 0.2, 10) : 2;
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1 / hz));
            try
            {
                while (ws.State == WebSocketState.Open && await timer.WaitForNextTickAsync(ctx.RequestAborted))
                    await ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(LevelsView.Build(st.Snapshot()), json.Value.SerializerOptions),
                        WebSocketMessageType.Text, true, ctx.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        });
    }

    /// <summary>"?download=1" or "?download=true" saves the file instead of opening it.</summary>
    private static bool IsDownload(string? v) => v is not null && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));
}
