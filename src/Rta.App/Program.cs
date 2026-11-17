using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Rta.App.Node;
using Rta.Core.Calibration;
using Rta.Core.Dsp;

var builder = WebApplication.CreateBuilder(args);

// Phase 1: local only. Network access (with the config password) arrives with the Hub in Phase 2.
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://localhost:5180");

builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = CalibrationStore.Json.PropertyNamingPolicy;
    foreach (var c in CalibrationStore.Json.Converters) o.SerializerOptions.Converters.Add(c);
});
builder.Services.AddSingleton<NodePaths>();
builder.Services.AddSingleton<NodeConfigStore>();
builder.Services.AddSingleton(sp => new CalibrationStore(sp.GetRequiredService<NodePaths>().CalibrationDir));
builder.Services.AddSingleton<MeteringService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MeteringService>());

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

static IResult Problem(Exception ex) => Results.Problem(ex.Message, statusCode: 400);

var api = app.MapGroup("/api");

api.MapGet("/status", (MeteringService m) => m.Status());
api.MapGet("/levels", (MeteringService m) => m.Channels());
api.MapGet("/devices", (MeteringService m) => m.Devices());
api.MapGet("/config", (MeteringService m) => m.Config);
api.MapPut("/config", (NodeConfig cfg, MeteringService m) =>
{
    try { m.UpdateConfig(cfg); return Results.Ok(m.Config); }
    catch (Exception ex) { return Problem(ex); }
});
api.MapPost("/audio/start", (MeteringService m) =>
{
    try { m.Start(); return Results.Ok(m.Status()); }
    catch (Exception ex) { return Problem(ex); }
});
api.MapPost("/audio/stop", (MeteringService m) => { m.Stop(); return Results.Ok(m.Status()); });
api.MapPost("/session/reset", (MeteringService m) => { m.ResetSession(); return Results.Ok(); });

var cal = api.MapGroup("/calibration");
cal.MapGet("/profiles", (CalibrationStore s) => s.List());
cal.MapPost("/profiles", (CalibrationProfile p, CalibrationStore s) => s.Save(p));
cal.MapDelete("/profiles/{id}", (string id, CalibrationStore s) => s.Delete(id) ? Results.NoContent() : Results.NotFound());
cal.MapGet("/profiles/{id}/export", (string id, CalibrationStore s) =>
{
    var p = s.Get(id);
    if (p is null) return Results.NotFound();
    var safe = string.Concat(p.Name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_')).Trim();
    return Results.File(System.Text.Encoding.UTF8.GetBytes(s.Export(id)), "application/json",
        $"{(safe.Length > 0 ? safe : "calibration")}.rtacal.json");
});
cal.MapPost("/profiles/import", async (HttpRequest req, CalibrationStore s) =>
{
    using var reader = new StreamReader(req.Body);
    try { return Results.Ok(s.Import(await reader.ReadToEndAsync())); }
    catch (Exception ex) { return Problem(ex); }
});
cal.MapPost("/correction/parse", async (HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var points = CorrectionCurve.Parse(await reader.ReadToEndAsync());
    return points.Count >= 2 ? Results.Ok(points) : Results.Problem("No frequency/dB pairs found in file.", statusCode: 400);
});
cal.MapPost("/measure", async (MeasureRequest r, MeteringService m, CalibrationStore store, CancellationToken ct) =>
{
    if (r.Seconds is < 1 or > 60) return Results.Problem("Seconds must be 1–60.", statusCode: 400);
    try
    {
        var duration = TimeSpan.FromSeconds(r.Seconds);
        if (r.Method == CalibrationMethod.Transfer)
        {
            if (r.ReferenceChannelIndex is not int refCh || refCh == r.ChannelIndex)
                return Results.Problem("Choose a different, already-calibrated input as the reference.", statusCode: 400);
            var refProfileId = m.Config.Inputs.FirstOrDefault(i => i.ChannelIndex == refCh)?.ProfileId;
            var refProfile = refProfileId is null ? null : store.Get(refProfileId);
            if (refProfile is null)
                return Results.Problem($"Input {refCh + 1} has no calibration profile, so it can't be used as the reference.", statusCode: 400);
            var (all, bandsT) = await m.CaptureManyAsync([r.ChannelIndex, refCh], duration, ct);
            return Results.Ok(CalibrationCalculator.FromTransfer(all[r.ChannelIndex], all[refCh], bandsT, refProfile, r.Weighting));
        }

        var (blocks, bands) = await m.CaptureAsync(r.ChannelIndex, duration, ct);
        CalibrationResult result = r.Method switch
        {
            CalibrationMethod.Calibrator => CalibrationCalculator.FromCalibrator(blocks, bands, r.ReferenceDb, r.ToneHz, r.CalibratorClass),
            CalibrationMethod.MeterMatch => CalibrationCalculator.FromMeterMatch(blocks, bands, r.ReferenceDb, r.Weighting, r.MeterClass),
            CalibrationMethod.PhoneApp => CalibrationCalculator.FromPhoneApp(blocks, bands, r.ReferenceDb, r.Weighting, r.Phone),
            _ => throw new ArgumentException($"Method {r.Method} is not measured; use the estimate or manual options."),
        };
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is not OperationCanceledException) { return Problem(ex); }
});
cal.MapPost("/estimate", (EstimateRequest r) =>
{
    try { return Results.Ok(CalibrationCalculator.FromSpecs(r.MicSensitivityMvPerPa, r.MaxInputDbuAtMinGain, r.GainAboveMinDb)); }
    catch (Exception ex) { return Problem(ex); }
});

// Live stream: status + all channel levels, 10 times a second.
app.Map("/ws/live", async (HttpContext ctx, MeteringService m) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    var json = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<JsonOptions>>().Value.SerializerOptions;
    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
    var ct = ctx.RequestAborted;
    try
    {
        while (ws.State == WebSocketState.Open && await timer.WaitForNextTickAsync(ct))
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { status = m.Status(), channels = m.Channels() }, json);
            await ws.SendAsync(payload, WebSocketMessageType.Text, true, ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (WebSocketException) { }
});

app.Run();

public sealed record MeasureRequest(
    int ChannelIndex,
    CalibrationMethod Method,
    double ReferenceDb = 94,
    Weighting Weighting = Weighting.Z,
    double Seconds = 5,
    double ToneHz = 1000,
    CalibratorClass CalibratorClass = CalibratorClass.Class2,
    MeterClass MeterClass = MeterClass.Class2,
    PhoneKind Phone = PhoneKind.IPhone,
    int? ReferenceChannelIndex = null);

public sealed record EstimateRequest(double MicSensitivityMvPerPa, double MaxInputDbuAtMinGain, double GainAboveMinDb);
