using Rta.App.Hub;

namespace Rta.App.Ndi;

public static class NdiEndpoints
{
    public static void MapNdiEndpoints(this IEndpointRouteBuilder app, AppMode mode)
    {
        var g = app.MapGroup("/api/ndi");

        // Status of this PC's NDI sources (node and/or hub).
        g.MapGet("/status", (IServiceProvider sp) =>
        {
            var parts = new[] { sp.GetService<NodeNdiService>()?.Status(), sp.GetService<HubNdiService>()?.Status() }.OfType<NdiStatus>().ToList();
            var senders = parts.SelectMany(p => p.Senders).ToList();
            var total = Math.Round(senders.Sum(s => s.EstimatedMbps), 1);
            var warnings = parts.SelectMany(p => p.Warnings).Distinct().ToList();
            return new NdiStatus(NdiRuntime.Available, NdiRuntime.Message, senders, total, warnings);
        });

        // Sources visible on the network (including this PC's own), for checking feeds arrive.
        g.MapGet("/sources", async (CancellationToken ct) =>
        {
            if (!NdiRuntime.Available) return Results.Problem(NdiRuntime.Message, statusCode: 503);
            return Results.Ok(await Task.Run(() => NdiTools.FindSources(), ct));
        });
        g.MapGet("/probe", async (string source, CancellationToken ct) =>
        {
            if (!NdiRuntime.Available) return Results.Problem(NdiRuntime.Message, statusCode: 503);
            if (string.IsNullOrWhiteSpace(source) || source.Length > 256) return Results.Problem("Source name required.", statusCode: 400);
            return Results.Ok(await Task.Run(() => NdiTools.Probe(source), ct));
        });

        if (mode.RunsHub)
        {
            app.MapGet("/api/hub/ndi", (HubSettingsStore s) => s.Current.Ndi);
            app.MapPut("/api/hub/ndi", (HubNdiSettings body, HubSettingsStore s) =>
            {
                try { HubNdiSettings.Validate(body); }
                catch (ArgumentException ex) { return Results.Problem(ex.Message, statusCode: 400); }
                return Results.Ok(s.Save(s.Current with { Ndi = body }).Ndi);
            });
        }
    }
}
