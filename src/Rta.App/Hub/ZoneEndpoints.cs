using Rta.Core.Zones;

namespace Rta.App.Hub;

public sealed record SurveyStartBody(int ZoneId, string RoverMicKey, double MinSeconds = 60);
public sealed record SurveyStopBody(bool Save, string? Note);
public sealed record ManualSurveyBody(int ZoneId, double? LA, double? LC, long FromTs, long ToTs, string? Note);
public sealed record SurveyEditBody(bool Excluded, string? Note);

/// <summary>Survey list item without raw band data (and without NaN, which JSON can't carry).</summary>
public sealed record SurveyView(
    long Id, int ZoneId, string Kind, long StartTs, long EndTs, double Seconds, double Confidence, string? RoverKey,
    bool Excluded, string? Note, double? RoverLA, double? RoverLC, bool HasBands, IReadOnlyList<SurveyRefView> Refs, bool InModel);

public sealed record SurveyRefView(string MicKey, double? OffsetA, double? OffsetC, double ChunkStdC, int Chunks);

public static class ZoneEndpoints
{
    private static double? N(double v) => double.IsFinite(v) ? Math.Round(v, 1) : null;

    public static void MapZoneEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/hub");

        // ----- plan -----
        g.MapGet("/plan", (PlanStore p) => p.Current);
        g.MapPut("/plan", (Plan plan, PlanStore p) =>
        {
            try { return Results.Ok(p.Save(plan)); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });
        g.MapPost("/plan/images", async (HttpRequest req, PlanStore p) =>
        {
            if (req.ContentLength > PlanStore.MaxImageBytes) return Results.Problem("Image must be under 15 MB.", statusCode: 413);
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            try { return Results.Ok(new { id = p.SaveImage(ms.ToArray()) }); }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });
        g.MapGet("/plan/images/{id}", (string id, PlanStore p) =>
            p.GetImage(id) is { } img ? Results.File(img.Path, img.ContentType, enableRangeProcessing: false) : Results.NotFound());

        // ----- surveys -----
        g.MapGet("/surveys", (int? zoneId, HubStore store, PlanStore plan) =>
        {
            var surveys = store.ListSurveys(zoneId);
            // Which surveys the model currently uses (Replace: latest included one per zone; Average: all included).
            var inModel = new HashSet<long>();
            foreach (var grp in surveys.Where(s => !s.Excluded).GroupBy(s => s.ZoneId))
            {
                var mode = plan.Current.Zone(grp.Key)?.Mode ?? ZoneSurveyMode.Replace;
                foreach (var s in mode == ZoneSurveyMode.Replace ? [grp.OrderBy(x => x.StartTs).Last()] : grp.ToList()) inModel.Add(s.Id);
            }
            return surveys.OrderByDescending(s => s.StartTs).Select(s => new SurveyView(
                s.Id, s.ZoneId, s.Kind, s.StartTs, s.EndTs, Math.Round(s.Seconds), Math.Round(s.Confidence, 2), s.RoverKey, s.Excluded, s.Note,
                N(s.Measurement.RoverLA), N(s.Measurement.RoverLC), s.Measurement.HasBands,
                s.Measurement.Refs.Select(r => new SurveyRefView(r.MicKey, N(s.Measurement.RoverLA - r.LA), N(s.Measurement.RoverLC - r.LC), Math.Round(r.ChunkStdC, 2), r.Chunks)).ToList(),
                inModel.Contains(s.Id))).ToList();
        });
        g.MapPut("/surveys/{id:long}", (long id, SurveyEditBody b, HubStore store, ZoneEngine z) =>
        {
            store.UpdateSurvey(id, b.Excluded, string.IsNullOrWhiteSpace(b.Note) ? null : b.Note.Trim());
            z.InvalidateModels();
            return Results.Ok();
        });
        g.MapDelete("/surveys/{id:long}", (long id, HubStore store, ZoneEngine z) =>
        {
            store.DeleteSurvey(id);
            z.InvalidateModels();
            return Results.NoContent();
        });

        g.MapGet("/survey/current", (ZoneEngine z) => z.SurveyStatus() is { } s ? Results.Ok(s) : Results.NoContent());
        g.MapPost("/survey/start", (SurveyStartBody b, ZoneEngine z) =>
        {
            try { return Results.Ok(z.StartSurvey(b.ZoneId, b.RoverMicKey, b.MinSeconds)); }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.Problem(ex.Message, statusCode: 400); }
        });
        g.MapPost("/survey/stop", (SurveyStopBody b, ZoneEngine z) =>
        {
            try
            {
                var row = z.StopSurvey(b.Save, string.IsNullOrWhiteSpace(b.Note) ? null : b.Note.Trim());
                return Results.Ok(new { saved = row is not null, id = row?.Id, confidence = row?.Confidence });
            }
            catch (InvalidOperationException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });
        g.MapPost("/survey/manual", (ManualSurveyBody b, ZoneEngine z) =>
        {
            try
            {
                var row = z.AddManual(b.ZoneId, b.LA, b.LC, b.FromTs, b.ToTs, string.IsNullOrWhiteSpace(b.Note) ? null : b.Note.Trim());
                return Results.Ok(new { id = row.Id, confidence = row.Confidence });
            }
            catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });
    }
}
