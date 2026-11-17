using SkiaSharp;

namespace Rta.App.Ndi;

/// <summary>What a meter tile shows. Levels in dB (null = no data).</summary>
public sealed record TileData(
    string Title,
    string? Subtitle,
    string Badge,
    TileTone BadgeTone,
    double? LCS, double? LAS,
    double? LCeq1m, double? LCeq15m, double? LCeqService,
    double[]? Bands,
    double Warn, double Alarm,
    string? AlertLevel,
    bool Online,
    bool Calibrated);

public enum TileTone { Ok, Warn, Muted }

public sealed record DashboardData(string HubName, string? Service, DateTimeOffset? ServiceStart, IReadOnlyList<TileData> Tiles, IReadOnlyList<string> Alerts);

/// <summary>Draws meter tiles and the dashboard frame (BGRA) for NDI video, in the dashboard's colours.</summary>
public static class TileRenderer
{
    private static readonly SKColor Bg = SKColor.Parse("#0b1626"), Panel = SKColor.Parse("#11223a"), Line = SKColor.Parse("#24405f"),
        Grid = SKColor.Parse("#1b3352"), Text = SKColor.Parse("#e4edf7"), Muted = SKColor.Parse("#8aa2bd"),
        C = SKColor.Parse("#5fb4ff"), A = SKColor.Parse("#f2b84b"), Ok = SKColor.Parse("#3ccf8e"), WarnC = SKColor.Parse("#f2b84b"), AlarmC = SKColor.Parse("#ff5d5d");

    private static readonly SKTypeface Regular = SKTypeface.FromFamilyName("Segoe UI") ?? SKTypeface.Default;
    private static readonly SKTypeface Bold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold) ?? SKTypeface.Default;

    /// <summary>Renders into a reusable bitmap (created on first use or resize).</summary>
    public static SKBitmap Ensure(ref SKBitmap? bmp, int w, int h)
    {
        if (bmp is null || bmp.Width != w || bmp.Height != h)
        {
            bmp?.Dispose();
            bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        }
        return bmp;
    }

    public static void DrawTileFrame(SKBitmap bmp, TileData d)
    {
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(Bg);
        DrawTile(canvas, new SKRect(0, 0, bmp.Width, bmp.Height), d, padOuter: false);
    }

    private static SKColor LevelColor(double? v, double warn, double alarm, SKColor normal) =>
        v is null ? Muted : v >= alarm ? AlarmC : v >= warn ? WarnC : normal;

    private static string F(double? v) => v is double d && d > -150 ? d.ToString("0.0") : "—";

    /// <summary>One tile in <paramref name="r"/>. Everything scales from the tile height.</summary>
    public static void DrawTile(SKCanvas c, SKRect r, TileData d, bool padOuter = true)
    {
        var u = r.Height / 360f; // design unit: 640×360 tile
        if (padOuter) r.Inflate(-6 * u, -6 * u);
        var border = d.AlertLevel == "alarm" ? AlarmC : d.AlertLevel == "warn" ? WarnC : Line;
        using (var p = new SKPaint { Color = Panel, IsAntialias = true })
            c.DrawRoundRect(r, 14 * u, 14 * u, p);
        using (var p = new SKPaint { Color = border, IsAntialias = true, IsStroke = true, StrokeWidth = (d.AlertLevel is null ? 2 : 6) * u })
            c.DrawRoundRect(r, 14 * u, 14 * u, p);

        var x = r.Left + 22 * u;
        var right = r.Right - 22 * u;
        var y = r.Top + 46 * u;
        using var title = new SKFont(Bold, 30 * u);
        using var small = new SKFont(Regular, 20 * u);
        using var tiny = new SKFont(Regular, 17 * u);
        using var big = new SKFont(Bold, 112 * u);
        using var mid = new SKFont(Bold, 56 * u);
        using var paint = new SKPaint { IsAntialias = true, Color = Text };

        // Title and badge
        c.DrawText(Clip(d.Title, title, right - x - 200 * u), x, y, SKTextAlign.Left, title, paint);
        var badgeColor = d.BadgeTone switch { TileTone.Ok => Ok, TileTone.Warn => WarnC, _ => Muted };
        paint.Color = badgeColor;
        c.DrawText(d.Badge, right, y - 4 * u, SKTextAlign.Right, small, paint);
        if (d.Subtitle is not null)
        {
            paint.Color = Muted;
            c.DrawText(Clip(d.Subtitle, small, right - x), x, y + 28 * u, SKTextAlign.Left, small, paint);
        }

        if (!d.Online || d.LCS is null)
        {
            paint.Color = Muted;
            c.DrawText(d.Online ? "Waiting for data…" : "Offline", r.MidX, r.MidY + 20 * u, SKTextAlign.Center, mid, paint);
            return;
        }

        // Big numbers
        var bigY = r.Top + 178 * u;
        paint.Color = d.Calibrated ? LevelColor(d.LCS, d.Warn, d.Alarm, C) : C;
        c.DrawText(F(d.LCS), x - 4 * u, bigY, SKTextAlign.Left, big, paint);
        var bigW = big.MeasureText(F(d.LCS));
        paint.Color = Muted;
        c.DrawText(d.Calibrated ? "dBC" : "dBFS C", x + bigW + 6 * u, bigY - 58 * u, SKTextAlign.Left, small, paint);
        paint.Color = A;
        c.DrawText(F(d.LAS), x + bigW + 6 * u, bigY, SKTextAlign.Left, mid, paint);
        paint.Color = Muted;
        c.DrawText("dBA", x + bigW + 6 * u + mid.MeasureText(F(d.LAS)) + 6 * u, bigY, SKTextAlign.Left, small, paint);

        // Leq column on the right
        var colX = right;
        var ly = r.Top + 102 * u;
        foreach (var (label, v) in new[] { ("LCeq 1 min", d.LCeq1m), ("15 min", d.LCeq15m), ("Service", d.LCeqService) })
        {
            paint.Color = Muted;
            c.DrawText(label, colX, ly, SKTextAlign.Right, tiny, paint);
            paint.Color = label == "LCeq 1 min" && d.Calibrated ? LevelColor(v, d.Warn, d.Alarm, Text) : Text;
            c.DrawText(F(v), colX, ly + 30 * u, SKTextAlign.Right, title, paint);
            ly += 62 * u;
        }

        // Horizontal bar meter (40–120 dB) with warn/alarm marks
        var barTop = r.Top + 214 * u;
        var bar = new SKRect(x, barTop, right - 150 * u, barTop + 22 * u); // leaves room for the Leq column
        const double lo = 40, hi = 120;
        float X(double v) => (float)(bar.Left + Math.Clamp((v - lo) / (hi - lo), 0, 1) * bar.Width);
        using (var p = new SKPaint { Color = Grid, IsAntialias = true }) c.DrawRoundRect(bar, 6 * u, 6 * u, p);
        if (d.LCS is double lcs)
        {
            using var p = new SKPaint { Color = d.Calibrated ? LevelColor(lcs, d.Warn, d.Alarm, C) : C, IsAntialias = true };
            c.DrawRoundRect(new SKRect(bar.Left, bar.Top, X(d.Calibrated ? lcs : lcs + 120), bar.Bottom), 6 * u, 6 * u, p);
        }
        if (d.Calibrated)
            foreach (var (v, col) in new[] { (d.Warn, WarnC), (d.Alarm, AlarmC) })
                using (var p = new SKPaint { Color = col, StrokeWidth = 3 * u, IsAntialias = true })
                    c.DrawLine(X(v), bar.Top - 4 * u, X(v), bar.Bottom + 4 * u, p);

        // Mini RTA
        if (d.Bands is { Length: > 0 } bands)
        {
            var rt = new SKRect(x, r.Top + 252 * u, right, r.Bottom - 18 * u);
            var vals = bands.Where(b => b > -150).DefaultIfEmpty(0).ToArray();
            var top = Math.Ceiling((vals.Max() + 3) / 10) * 10;
            var bottom = top - 60;
            var bw = rt.Width / bands.Length;
            using var p = new SKPaint { Color = C, IsAntialias = false };
            for (var i = 0; i < bands.Length; i++)
            {
                var t = (float)Math.Clamp((bands[i] - bottom) / (top - bottom), 0, 1);
                c.DrawRect(new SKRect(rt.Left + i * bw + 1, rt.Bottom - t * rt.Height, rt.Left + (i + 1) * bw - 1, rt.Bottom), p);
            }
        }
    }

    public static void DrawDashboard(SKBitmap bmp, DashboardData d)
    {
        using var c = new SKCanvas(bmp);
        c.Clear(Bg);
        var w = bmp.Width; var h = bmp.Height;
        var u = h / 1080f;
        using var paint = new SKPaint { IsAntialias = true, Color = Text };
        using var head = new SKFont(Bold, 46 * u);
        using var sub = new SKFont(Regular, 28 * u);

        c.DrawText(d.HubName, 40 * u, 70 * u, SKTextAlign.Left, head, paint);
        paint.Color = Muted;
        var svc = d.Service is null ? "No service running" : $"Service: {d.Service}" + (d.ServiceStart is { } s ? $"  ·  since {s.ToLocalTime():HH:mm}" : "");
        c.DrawText(svc, 40 * u, 110 * u, SKTextAlign.Left, sub, paint);
        paint.Color = Text;
        c.DrawText(DateTimeOffset.Now.ToString("HH:mm:ss"), w - 40 * u, 80 * u, SKTextAlign.Right, head, paint);

        var top = 140 * u;
        if (d.Alerts.Count > 0)
        {
            var banner = new SKRect(30 * u, top, w - 30 * u, top + 56 * u);
            using (var p = new SKPaint { Color = SKColor.Parse("#4a1418"), IsAntialias = true }) c.DrawRoundRect(banner, 10 * u, 10 * u, p);
            paint.Color = SKColor.Parse("#ffd0d0");
            c.DrawText(Clip(string.Join("    ", d.Alerts), sub, banner.Width - 40 * u), banner.Left + 20 * u, banner.MidY + 10 * u, SKTextAlign.Left, sub, paint);
            top += 70 * u;
        }

        var n = Math.Max(1, d.Tiles.Count);
        var area = new SKRect(30 * u, top, w - 30 * u, h - 30 * u);
        // Choose columns that keep tiles near 16:9 and as large as possible.
        var (cols, rows, tw, th) = Enumerable.Range(1, n).Select(cn =>
        {
            var rn = (int)Math.Ceiling(n / (double)cn);
            var tileW = area.Width / cn;
            var tileH = Math.Min(area.Height / rn, tileW * 9 / 16f);
            return (cn, rn, tileH * 16 / 9f, tileH);
        }).MaxBy(t => t.Item4);
        for (var i = 0; i < d.Tiles.Count; i++)
        {
            var col = i % cols; var row = i / cols;
            var rx = area.Left + col * (area.Width / cols) + (area.Width / cols - tw) / 2;
            var ry = area.Top + row * th;
            DrawTile(c, new SKRect(rx, ry, rx + tw, ry + th), d.Tiles[i]);
        }
        if (d.Tiles.Count == 0)
        {
            paint.Color = Muted;
            c.DrawText("No mics yet", w / 2f, h / 2f, SKTextAlign.Center, head, paint);
        }
    }

    private static string Clip(string s, SKFont f, float max)
    {
        if (f.MeasureText(s) <= max) return s;
        while (s.Length > 1 && f.MeasureText(s + "…") > max) s = s[..^1];
        return s + "…";
    }
}
