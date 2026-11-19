using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting.WindowsServices;
using Rta.App.Node;
using Rta.App.Security;
using Rta.Core;

namespace Rta.App.Backup;

/// <summary>Makes a settings backup once a day (kept for two weeks).</summary>
public sealed class DailyBackupService(NodePaths paths, ILogger<DailyBackupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(10), ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var newest = BackupArchive.List(paths.DataDir).FirstOrDefault(f => f.Name.StartsWith("daily-"));
                if (newest is null || DateTime.UtcNow - newest.LastWriteTimeUtc > TimeSpan.FromHours(20))
                {
                    var file = Path.Combine(BackupArchive.BackupsDir(paths.DataDir), $"daily-{DateTime.Now:yyyyMMdd-HHmmss}{BackupArchive.Extension}");
                    BackupArchive.Create(paths.DataDir, file, AppMode.Version, "Daily automatic backup", includeHistory: false);
                    BackupArchive.Prune(paths.DataDir);
                    log.LogInformation("Saved daily settings backup {File}", Path.GetFileName(file));
                }
            }
            catch (Exception ex) { log.LogWarning(ex, "Daily settings backup failed"); }
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch (OperationCanceledException) { return; }
        }
    }
}

public sealed record RestoreRequest(List<string> Sections, bool TakeOverIdentity, string? File);

public static class BackupEndpoints
{
    private static readonly Dictionary<string, string> Labels = new()
    {
        [BackupArchive.Node] = "This PC's inputs, audio device, warn/alarm levels and calibration profiles",
        [BackupArchive.Hub] = "Hub settings, alert rules, integrations, NDI, floor plan, zones, surveys, mic names and schedule",
        [BackupArchive.History] = "Logged levels, services and alert history (replaces this PC's history)",
        [BackupArchive.Admin] = "Admin password",
    };

    public static void MapBackupEndpoints(this IEndpointRouteBuilder app, AppMode mode, IConfiguration config)
    {
        var g = app.MapGroup("/api/backup");
        var autoRestart = WindowsServiceHelpers.IsWindowsService() || string.Equals(config["RunAs"], "signin", StringComparison.OrdinalIgnoreCase);

        g.MapGet("", (NodePaths paths) => new
        {
            backups = BackupArchive.List(paths.DataDir).Select(f => new { name = f.Name, size = f.Length, created = new DateTimeOffset(f.LastWriteTimeUtc).ToUnixTimeSeconds() }),
            folder = BackupArchive.BackupsDir(paths.DataDir),
            lastRestore = BackupArchive.LastResult(paths.DataDir),
            restorePending = BackupArchive.RestorePending(paths.DataDir),
            notes = SafeFile.Notes.ToArray(),
            autoRestart,
        });

        // Downloads contain the admin password hash, so they need admin like any change.
        g.MapGet("/export", (bool? history, HttpContext ctx, NodePaths paths, AdminAuth auth) =>
        {
            if (!auth.IsAdmin(ctx)) return Results.Problem("Sign in as admin to download a backup.", statusCode: 401);
            var tmp = Path.Combine(Path.GetTempPath(), $"rta-export-{Guid.NewGuid():N}{BackupArchive.Extension}");
            BackupArchive.Create(paths.DataDir, tmp, AppMode.Version, history == true ? "Full backup" : "Settings backup", history == true);
            ctx.Response.RegisterForDispose(new DeleteOnDispose(tmp));
            var name = $"RTA {Environment.MachineName} {(history == true ? "full" : "settings")} {DateTime.Now:yyyy-MM-dd HHmm}{BackupArchive.Extension}";
            return Results.File(tmp, "application/zip", name);
        });

        g.MapGet("/files/{name}", (string name, HttpContext ctx, NodePaths paths, AdminAuth auth) =>
        {
            if (!auth.IsAdmin(ctx)) return Results.Problem("Sign in as admin to download a backup.", statusCode: 401);
            return BackupArchive.FindBackup(paths.DataDir, name) is { } f ? Results.File(f, "application/zip", name) : Results.NotFound();
        });

        // Upload a backup file (raw body) and see what's in it before restoring.
        g.MapPost("/upload", async (HttpContext ctx, NodePaths paths) =>
        {
            if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 4L * 1024 * 1024 * 1024;
            var staged = BackupArchive.StagedFile(paths.DataDir);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            await using (var fs = File.Create(staged)) await ctx.Request.Body.CopyToAsync(fs, ctx.RequestAborted);
            try { return Results.Ok(Preview(BackupArchive.ReadManifest(staged), mode, ThisNodeId(paths), null)); }
            catch (InvalidDataException ex) { File.Delete(staged); return Results.Problem(ex.Message, statusCode: 400); }
        });

        // Look inside one of this PC's own backups (e.g. to roll back an update).
        g.MapGet("/files/{name}/preview", (string name, NodePaths paths) =>
        {
            if (BackupArchive.FindBackup(paths.DataDir, name) is not { } f) return Results.NotFound();
            try { return Results.Ok(Preview(BackupArchive.ReadManifest(f), mode, ThisNodeId(paths), name)); }
            catch (InvalidDataException ex) { return Results.Problem(ex.Message, statusCode: 400); }
        });

        g.MapPost("/restore", (RestoreRequest r, NodePaths paths, IHostApplicationLifetime life, ILogger<BackupArchiveLog> log) =>
        {
            var source = r.File is { Length: > 0 } name ? BackupArchive.FindBackup(paths.DataDir, name) : BackupArchive.StagedFile(paths.DataDir);
            if (source is null || !File.Exists(source)) return Results.Problem("Upload the backup file again.", statusCode: 400);
            BackupManifest m;
            try { m = BackupArchive.ReadManifest(source); }
            catch (InvalidDataException ex) { return Results.Problem(ex.Message, statusCode: 400); }
            var sections = r.Sections.Where(s => m.Sections.Contains(s) && Applies(s, mode)).ToList();
            if (sections.Count == 0) return Results.Problem("Choose at least one part to restore.", statusCode: 400);

            BackupArchive.QueueRestore(paths.DataDir, source, new RestoreOptions { Sections = sections, TakeOverIdentity = r.TakeOverIdentity });
            log.LogWarning("Restore of {Sections} from {Machine} ({Created}) queued; restarting", string.Join(", ", sections), m.Machine, m.Created);
            if (autoRestart)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(800);
                    // The tray icon (sign-in mode) or Windows service recovery starts the app again,
                    // and the restore is applied before anything opens the files.
                    Environment.Exit(3);
                });
            return Results.Ok(new
            {
                restarting = autoRestart,
                message = autoRestart
                    ? "Restarting to restore. This page reconnects in a few seconds."
                    : "The restore will be applied the next time RTA starts. Restart it now.",
            });
        });

        g.MapPost("/cancel", (NodePaths paths) =>
        {
            foreach (var f in new[] { BackupArchive.PendingFile(paths.DataDir), BackupArchive.StagedFile(paths.DataDir) })
                if (File.Exists(f)) File.Delete(f);
            return Results.Ok();
        });
    }

    private static bool Applies(string section, AppMode mode) => section switch
    {
        BackupArchive.Node => mode.RunsNode,
        BackupArchive.Hub or BackupArchive.History => mode.RunsHub,
        _ => true,
    };

    private static object Preview(BackupManifest m, AppMode mode, string? thisNodeId, string? file) => new
    {
        manifest = m,
        file,
        sections = m.Sections.Select(s => new
        {
            id = s,
            label = Labels.GetValueOrDefault(s, s),
            applies = Applies(s, mode),
            note = Applies(s, mode) ? null : $"This PC doesn't run the {(s == BackupArchive.Node ? "mics (node)" : "Hub")}, so this part isn't restored here.",
        }),
        samePc = thisNodeId is not null && thisNodeId == m.NodeId,
        // Zones and surveys refer to the backup PC's mics, so a Hub restore normally takes over its identity.
        suggestTakeOver = m.Sections.Contains(BackupArchive.Hub) && mode.RunsHub,
    };

    /// <summary>This PC's node id, read from disk so it also works on a Hub-only PC.</summary>
    private static string? ThisNodeId(NodePaths paths) =>
        SafeFile.ReadJson<NodeConfig>(paths.ConfigFile, Rta.Core.Calibration.CalibrationStore.Json)?.NodeId;

    private sealed class DeleteOnDispose(string path) : IDisposable
    {
        public void Dispose() { try { File.Delete(path); } catch (IOException) { } }
    }
}

/// <summary>Log category for backup and restore.</summary>
public sealed class BackupArchiveLog;

