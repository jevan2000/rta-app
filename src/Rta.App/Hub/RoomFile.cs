using System.Text.Json;
using Rta.App.Backup;
using Rta.App.Node;

namespace Rta.App.Hub;

/// <summary>
/// A room exported from the plan editor (.rtaroom, JSON): the plan (floors, shapes, zones, mic places),
/// its underlay images, and the surveys of its zones, so the room can be loaded again exactly.
/// </summary>
public sealed record RoomFile
{
    public string Format { get; init; } = "rta-room";
    public int Version { get; init; } = 1;
    public string AppVersion { get; init; } = "";
    public DateTimeOffset Exported { get; init; }
    public string? HubName { get; init; }
    public Plan Plan { get; init; } = new();
    /// <summary>Underlay images by id, base64.</summary>
    public Dictionary<string, string> Images { get; init; } = [];
    public List<SurveyRow> Surveys { get; init; } = [];
}

public sealed record RoomImportResult(Plan Plan, int Surveys, int MissingMics, string? SafetyBackup);

public static class Rooms
{
    public const string Extension = ".rtaroom";

    public static RoomFile Export(PlanStore plans, HubStore store, HubSettingsStore settings)
    {
        var plan = plans.Current;
        var zoneIds = plan.AllZones.Select(z => z.Id).ToHashSet();
        var images = plan.Floors.Select(f => f.Underlay?.ImageId).OfType<string>().Distinct()
            .Select(id => (id, bytes: plans.ReadImage(id))).Where(x => x.bytes is not null)
            .ToDictionary(x => x.id, x => Convert.ToBase64String(x.bytes!));
        return new RoomFile
        {
            AppVersion = AppMode.Version, Exported = DateTimeOffset.Now, HubName = settings.Current.Name,
            Plan = plan, Images = images, Surveys = store.ListSurveys().Where(s => zoneIds.Contains(s.ZoneId)).ToList(),
        };
    }

    public static RoomFile Read(Stream json)
    {
        RoomFile? room;
        try { room = JsonSerializer.Deserialize<RoomFile>(json, HubStore.SurveyJson); }
        catch (JsonException ex) { throw new ArgumentException("This isn't a readable room file.", ex); }
        if (room is null || room.Format != "rta-room") throw new ArgumentException("This isn't an RTA room file.");
        if (room.Version > 1) throw new ArgumentException($"This room was exported by a newer version ({room.AppVersion}). Update the app first.");
        return room;
    }

    /// <summary>
    /// Replaces the Hub's plan with the room (keeping its zone ids, so its surveys line up) and its zones'
    /// surveys with the file's. A settings backup is saved first.
    /// </summary>
    public static RoomImportResult Import(RoomFile room, PlanStore plans, HubStore store, NodePaths paths, HubState state)
    {
        var safety = Path.Combine(BackupArchive.BackupsDir(paths.DataDir), $"before-room-import-{DateTime.Now:yyyyMMdd-HHmmss}{BackupArchive.Extension}");
        BackupArchive.Create(paths.DataDir, safety, AppMode.Version, "Before importing a room", includeHistory: false);
        BackupArchive.Prune(paths.DataDir);

        // Images get new ids here; point the floors at them.
        var ids = new Dictionary<string, string>();
        foreach (var (id, b64) in room.Images)
        {
            try { ids[id] = plans.SaveImage(Convert.FromBase64String(b64)); }
            catch (FormatException) { /* skip a damaged image; the floor just has no underlay */ }
        }
        var plan = room.Plan with
        {
            Floors = room.Plan.Floors.Select(f => f with
            {
                Underlay = f.Underlay is { } u && ids.TryGetValue(u.ImageId, out var nid) ? u with { ImageId = nid } : null,
            }).ToList(),
        };
        var zoneIds = plan.AllZones.Select(z => z.Id).Where(id => id > 0).ToList();
        store.ReplaceSurveys(zoneIds, room.Surveys);   // before saving the plan, so zone models rebuild with them
        var saved = plans.Save(plan);
        var known = state.CachedMics.Select(m => m.Key).ToHashSet();
        var missing = saved.AllMics.Count(m => !known.Contains(m.Mic.MicKey));
        return new RoomImportResult(saved, room.Surveys.Count(s => zoneIds.Contains(s.ZoneId)), missing, Path.GetFileName(safety));
    }
}
