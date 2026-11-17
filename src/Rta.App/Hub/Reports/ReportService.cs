using System.Globalization;
using System.Text;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Rta.App.Node;
using Rta.Core.Calibration;

namespace Rta.App.Hub.Reports;

/// <summary>
/// Service reports (PDF) and CSV exports. A background pass writes a PDF for each service that
/// ended at least 90 s ago (so the last minute records are in) into the reports folder.
/// </summary>
public sealed class ReportService(HubStore store, HubSettingsStore settings, NodePaths paths, ILogger<ReportService> log) : BackgroundService
{
    static ReportService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        // Mic and zone names are user text; an unusual character must never stop a report.
        QuestPDF.Settings.ThrowOnMissingTextGlyphs = false;
    }

    private static readonly string[] Palette = ["#1f77b4", "#d62728", "#2ca02c", "#ff7f0e", "#9467bd", "#17becf", "#8c564b", "#e377c2"];

    public string Folder
    {
        get
        {
            var f = settings.Current.Integrations.Reports.Folder;
            var dir = string.IsNullOrWhiteSpace(f) ? Path.Combine(paths.DataDir, "reports") : f;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string FileName(ServiceRow s)
    {
        var start = DateTimeOffset.FromUnixTimeSeconds(s.StartTs).ToLocalTime();
        var safe = string.Concat(s.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
        return $"{start:yyyy-MM-dd HHmm} {safe} ({s.Id}).pdf";
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), ct).ContinueWith(_ => { });
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (settings.Current.Integrations.Reports.AutoGenerate)
                {
                    var now = HubStore.Now();
                    foreach (var s in store.ListServices(now - 2 * 86400, now).Where(s => s.EndTs is long e && e <= now - 90))
                    {
                        var path = Path.Combine(Folder, FileName(s));
                        if (File.Exists(path)) continue;
                        await File.WriteAllBytesAsync(path, ServicePdf(s.Id), ct);
                        log.LogInformation("Service report written: {Path}", path);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Automatic report failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch (OperationCanceledException) { break; }
        }
    }

    public IReadOnlyList<(string Name, long Bytes, DateTimeOffset Modified)> ListReports() =>
        new DirectoryInfo(Folder).EnumerateFiles("*.pdf").OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => (f.Name, f.Length, (DateTimeOffset)f.LastWriteTimeUtc)).ToList();

    public string? ReportPath(string name)
    {
        if (name.Length > 200 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Path.Combine(Folder, name);
        return File.Exists(path) ? path : null;
    }

    // ---------- CSV ----------

    /// <summary>Long-format CSV: one row per source per second (or minute).</summary>
    public async Task WriteCsvAsync(Stream output, IReadOnlyList<string> keys, long from, long to, bool minutes, CancellationToken ct)
    {
        var mics = store.ListMics().ToDictionary(m => m.Key);
        await using var w = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
        await w.WriteLineAsync("utc_time,local_time,source,source_key,kind,laeq,lceq,lzeq,lcpeak,lcs_max,coverage_s,calibrated");
        var inv = CultureInfo.InvariantCulture;
        foreach (var key in keys)
        {
            if (!mics.TryGetValue(key, out var mic)) continue;
            var s = store.GetSeries(key, from, to, minutes ? TimeSpan.Zero : TimeSpan.FromDays(3660));
            var kind = mic.NodeId == HubStore.ZoneNodeId ? "zone" : "mic";
            for (var i = 0; i < s.Ts.Length; i++)
            {
                var t = DateTimeOffset.FromUnixTimeSeconds(s.Ts[i]);
                string V(double d) => d <= -150 ? "" : d.ToString("0.00", inv);
                await w.WriteLineAsync(string.Join(',',
                    t.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", inv), t.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", inv),
                    Csv(mic.Label), Csv(key), kind, V(s.LA[i]), V(s.LC[i]), V(s.LZ[i]), V(s.LCpeak[i]), V(s.LCSmax[i]),
                    s.Coverage[i].ToString("0.##", inv), mic.Calibrated ? "yes" : "no"));
            }
            ct.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Quotes a CSV field and defuses spreadsheet formulas (=, +, -, @).</summary>
    public static string Csv(string v)
    {
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        return v.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }

    // ---------- PDF ----------

    public byte[] ServicePdf(long serviceId) => ServiceDocument(serviceId).GeneratePdf();

    /// <summary>Page images (PNG) of the report, for previews.</summary>
    public IEnumerable<byte[]> ServiceImages(long serviceId) => ServiceDocument(serviceId).GenerateImages(new ImageGenerationSettings { RasterDpi = 110 });

    private IDocument ServiceDocument(long serviceId)
    {
        var s = store.GetService(serviceId) ?? throw new KeyNotFoundException("Service not found.");
        var sum = HubEndpoints.Summarise(s, store, settings);
        var micRows = store.ListMics().ToDictionary(m => m.Key);
        var end = s.EndTs ?? HubStore.Now();
        var start = DateTimeOffset.FromUnixTimeSeconds(s.StartTs).ToLocalTime();
        var finish = DateTimeOffset.FromUnixTimeSeconds(end).ToLocalTime();
        var sources = sum.Mics.OrderBy(m => micRows.GetValueOrDefault(m.MicKey)?.NodeId == HubStore.ZoneNodeId).ThenBy(m => m.Label).ToList();
        // Uncalibrated (dBFS) mics would squash the dB SPL scale, so they're listed but not graphed.
        var graphed = sources.Take(Palette.Length).Where(m => micRows.GetValueOrDefault(m.MicKey)?.Calibrated != false).Select(m => m.MicKey).ToList();
        var colorOf = sources.Select((m, i) => (m.MicKey, i)).ToDictionary(x => x.MicKey, x => x.i < Palette.Length ? Palette[x.i] : "#666");
        var chart = Chart(graphed, graphed.Select(k => colorOf[k]).ToList(), s.StartTs, end, sum.WarnC, sum.AlarmC);
        var hub = settings.Current.Name;

        static string F(double? v) => v is double d && d > -150 ? d.ToString("0.0", CultureInfo.InvariantCulture) : "—";
        static string Dur(double seconds) =>
            seconds >= 3600 ? $"{(int)(seconds / 3600)} h {(int)(seconds % 3600 / 60)} min"
            : seconds >= 60 ? $"{(int)Math.Round(seconds / 60)} min" : $"{(int)Math.Round(seconds)} s";

        var notes = new List<string>();
        foreach (var m in sources)
        {
            var row = micRows.GetValueOrDefault(m.MicKey);
            if (row is null) continue;
            if (row.NodeId == HubStore.ZoneNodeId) continue;
            if (!row.Calibrated) notes.Add($"{m.Label} was uncalibrated: values are dBFS, not dB SPL.");
            else if (row.Quality is CalibrationQuality.Low or CalibrationQuality.VeryLow)
                notes.Add($"{m.Label} uses an unreliable calibration ({row.ProfileName}{(row.UncertaintyDb is double u ? $", about ±{u:0} dB" : "")}).");
        }
        if (sources.Any(m => micRows.GetValueOrDefault(m.MicKey)?.NodeId == HubStore.ZoneNodeId))
            notes.Add("Zones are predicted from the permanent mics using survey data, not measured directly.");

        return Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(36);
            page.DefaultTextStyle(t => t.FontSize(9.5f).FontColor("#1b2430"));
            page.Header().Column(c =>
            {
                c.Item().Row(r =>
                {
                    r.RelativeItem().Text("Service sound report").FontSize(18).Bold();
                    r.ConstantItem(200).AlignRight().Text(hub).FontSize(10).FontColor("#5b6b80");
                });
                c.Item().Text($"{s.Name}  ·  {start:dddd d MMMM yyyy}, {start:HH:mm}–{finish:HH:mm}  ·  {Dur(end - s.StartTs)}  ·  {(s.Source == "schedule" ? "scheduled" : "started manually")}{(s.EndTs is null ? "  ·  still running" : "")}")
                    .FontSize(10.5f).FontColor("#3b4a5c");
                c.Item().PaddingTop(6).LineHorizontal(0.75f).LineColor("#c9d3df");
            });

            page.Content().PaddingTop(10).Column(col =>
            {
                col.Spacing(12);
                col.Item().Text("Levels").FontSize(12).Bold();
                if (sources.Count == 0) col.Item().Text("No levels were logged during this service.").Italic();
                else col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd =>
                    {
                        cd.RelativeColumn(3); cd.RelativeColumn(1.1f); cd.RelativeColumn(1.1f); cd.RelativeColumn(1.4f);
                        cd.RelativeColumn(1.3f); cd.RelativeColumn(1.3f); cd.RelativeColumn(1.3f); cd.RelativeColumn(1.2f);
                    });
                    void H(string text) => t.Cell().BorderBottom(1).BorderColor("#9aa9bb").PaddingVertical(3).Text(text).SemiBold().FontSize(8.5f);
                    H("Mic / zone"); H("LCeq"); H("LAeq"); H("Max 1-min LCeq"); H("LCpeak max"); H($"Time at {sum.WarnC:0}+ dBC"); H($"Time at {sum.AlarmC:0}+ dBC"); H("Logged");
                    var i = 0;
                    foreach (var m in sources)
                    {
                        var zone = micRows.GetValueOrDefault(m.MicKey)?.NodeId == HubStore.ZoneNodeId;
                        var color = i < Palette.Length ? Palette[i] : "#666";
                        void C(string text, bool bold = false)
                        {
                            var x = t.Cell().BorderBottom(0.5f).BorderColor("#e1e7ee").PaddingVertical(3).Text(text);
                            if (bold) x.SemiBold();
                        }
                        t.Cell().BorderBottom(0.5f).BorderColor("#e1e7ee").PaddingVertical(3).Row(r =>
                        {
                            r.ConstantItem(8).AlignMiddle().Height(8).Background(color);
                            r.ConstantItem(5);
                            r.RelativeItem().Text(tx =>
                            {
                                tx.Span(m.Label);
                                if (zone) tx.Span(" (zone, predicted)").FontColor("#6b7a8c").FontSize(8);
                            });
                        });
                        C(F(m.LCeq), true); C(F(m.LAeq)); C(F(m.MaxLCeq1m)); C(zone ? "—" : F(m.LCpeakMax));
                        C(m.SecondsAboveWarnC > 0 ? Dur(m.SecondsAboveWarnC) : "—"); C(m.SecondsAboveAlarmC > 0 ? Dur(m.SecondsAboveAlarmC) : "—"); C(Dur(m.Seconds));
                        i++;
                    }
                });

                if (chart is not null)
                {
                    col.Item().Text("LCeq over the service (10-second averages)").FontSize(12).Bold();
                    col.Item().AspectRatio(720f / 230f).Svg(chart);
                    col.Item().Text($"Dashed lines: warn {sum.WarnC:0} dBC (amber), alarm {sum.AlarmC:0} dBC (red).{(graphed.Count < Math.Min(sources.Count, Palette.Length) ? " Uncalibrated mics are not graphed." : "")}").FontSize(8).FontColor("#6b7a8c");
                }

                col.Item().Text("Alerts").FontSize(12).Bold();
                if (sum.Alerts.Count == 0) col.Item().Text("None.");
                else col.Item().Table(t =>
                {
                    t.ColumnsDefinition(cd => { cd.RelativeColumn(1); cd.RelativeColumn(2.5f); cd.RelativeColumn(2.5f); cd.RelativeColumn(1); cd.RelativeColumn(1); cd.RelativeColumn(1.2f); });
                    foreach (var h in new[] { "Time", "Mic / zone", "Rule", "Level", "Peak", "Lasted" })
                        t.Cell().BorderBottom(1).BorderColor("#9aa9bb").PaddingVertical(3).Text(h).SemiBold().FontSize(8.5f);
                    foreach (var a in sum.Alerts.OrderBy(a => a.StartTs))
                    {
                        void C(string text, string? color = null)
                        {
                            var x = t.Cell().BorderBottom(0.5f).BorderColor("#e1e7ee").PaddingVertical(2).Text(text);
                            if (color is not null) x.FontColor(color);
                        }
                        C(DateTimeOffset.FromUnixTimeSeconds(a.StartTs).ToLocalTime().ToString("HH:mm:ss"));
                        C(micRows.GetValueOrDefault(a.MicKey)?.Label ?? a.MicKey);
                        C(a.RuleName);
                        C(a.Level, a.Level == "alarm" ? "#c62828" : "#b26a00");
                        C(F(a.PeakValue));
                        C(a.EndTs is long e ? Dur(Math.Max(60, e - a.StartTs)) : "ongoing");
                    }
                });

                if (notes.Count > 0)
                {
                    col.Item().Text("Notes").FontSize(12).Bold();
                    foreach (var n in notes) col.Item().Text("• " + n).FontSize(9);
                }
                col.Item().Text("\"Time at\" counts seconds whose 1-second LCeq reached the level (warn and alarm from the first dBC alert rule). Leq values are energy averages over the service.")
                    .FontSize(8).FontColor("#6b7a8c");
            });

            page.Footer().Row(r =>
            {
                r.RelativeItem().Text($"Generated {DateTimeOffset.Now:yyyy-MM-dd HH:mm} by RTA Hub").FontSize(8).FontColor("#6b7a8c");
                r.RelativeItem().AlignRight().Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(8).FontColor("#6b7a8c"));
                    t.Span("Page "); t.CurrentPageNumber(); t.Span(" of "); t.TotalPages();
                });
            });
        }));
    }

    /// <summary>SVG line chart of 10-s LCeq for each source, with warn/alarm guides.</summary>
    private string? Chart(IReadOnlyList<string> keys, IReadOnlyList<string> colors, long from, long to, double warn, double alarm)
    {
        const double W = 720, H = 230, L = 40, R = 10, T = 10, B = 24;
        var series = new List<(string Color, List<(long T, double V)> Pts)>();
        for (var i = 0; i < keys.Count; i++)
        {
            Series s;
            try { s = store.GetSeries(keys[i], from, to, TimeSpan.FromHours(6)); } catch (KeyNotFoundException) { continue; }
            var bin = s.Resolution == "second" ? 10 : 60;
            var pts = s.Ts.Select((t, k) => (t, v: s.LC[k], c: s.Coverage[k])).Where(x => x.v > -150)
                .GroupBy(x => x.t / bin * bin)
                .Select(g => (g.Key, 10 * Math.Log10(g.Sum(x => Math.Pow(10, x.v / 10) * x.c) / Math.Max(1e-9, g.Sum(x => x.c)))))
                .OrderBy(p => p.Key).ToList();
            if (pts.Count > 1) series.Add((colors[i], pts));
        }
        if (series.Count == 0) return null;

        var all = series.SelectMany(s => s.Pts.Select(p => p.V)).Append(warn).ToList();
        var lo = Math.Floor((all.Min() - 3) / 10) * 10;
        var hi = Math.Ceiling((Math.Max(all.Max(), alarm) + 3) / 10) * 10;
        if (hi - lo < 20) hi = lo + 20;
        double X(long t) => L + (t - from) / (double)Math.Max(1, to - from) * (W - L - R);
        double Y(double v) => T + (hi - v) / (hi - lo) * (H - T - B);
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append(inv, $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {W} {H}\">");
        sb.Append(inv, $"<rect x=\"{L}\" y=\"{T}\" width=\"{W - L - R}\" height=\"{H - T - B}\" fill=\"#fafbfd\" stroke=\"#c9d3df\" stroke-width=\"0.8\"/>");
        for (var v = lo; v <= hi; v += 10)
        {
            sb.Append(inv, $"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{Y(v):0.0}\" y2=\"{Y(v):0.0}\" stroke=\"#e1e7ee\" stroke-width=\"0.6\"/>");
            sb.Append(inv, $"<text x=\"{L - 5}\" y=\"{Y(v) + 3:0.0}\" font-size=\"9\" text-anchor=\"end\" fill=\"#6b7a8c\" font-family=\"Arial\">{v:0}</text>");
        }
        var span = to - from;
        var step = span > 4 * 3600 ? 3600 : span > 3600 ? 1800 : span > 1800 ? 600 : 300;
        for (var t = (from / step + 1) * step; t < to; t += step)
        {
            var x = X(t);
            sb.Append(inv, $"<line x1=\"{x:0.0}\" x2=\"{x:0.0}\" y1=\"{T}\" y2=\"{H - B}\" stroke=\"#eef2f6\" stroke-width=\"0.6\"/>");
            sb.Append(inv, $"<text x=\"{x:0.0}\" y=\"{H - 8}\" font-size=\"9\" text-anchor=\"middle\" fill=\"#6b7a8c\" font-family=\"Arial\">{DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime():HH:mm}</text>");
        }
        foreach (var (v, c) in new[] { (warn, "#e0a100"), (alarm, "#d32f2f") })
            if (v > lo && v < hi)
                sb.Append(inv, $"<line x1=\"{L}\" x2=\"{W - R}\" y1=\"{Y(v):0.0}\" y2=\"{Y(v):0.0}\" stroke=\"{c}\" stroke-width=\"1\" stroke-dasharray=\"5 4\"/>");
        foreach (var (color, pts) in series)
        {
            // Break the line where there is a gap of more than two bins.
            var path = new StringBuilder();
            long? prev = null;
            foreach (var (t, v) in pts)
            {
                path.Append(inv, $"{(prev is long p && t - p <= 130 ? "L" : "M")}{X(t):0.0},{Y(Math.Clamp(v, lo, hi)):0.0} ");
                prev = t;
            }
            sb.Append(inv, $"<path d=\"{path}\" fill=\"none\" stroke=\"{color}\" stroke-width=\"1.3\" stroke-linejoin=\"round\"/>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
