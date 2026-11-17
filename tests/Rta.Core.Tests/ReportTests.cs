using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Rta.App.Hub;
using Rta.App.Hub.Integrations;
using Rta.App.Hub.Reports;
using Rta.Core.Logging;
using Rta.Core.Protocol;

namespace Rta.Core.Tests;

public class ReportTests
{
    private static (TempData Tmp, HubStore Store, ReportService Reports, ServiceRow Service) Setup()
    {
        var tmp = new TempData();
        var store = new HubStore(tmp.Paths);
        var settings = new HubSettingsStore(tmp.Paths);
        var hello = new Hello("n1", "Booth PC", Proto.Version, "1", "b", 0, 48000, [],
            [new MicInfo(0, "Front 😀 mic", null, "p", null, null, true), new MicInfo(1, "=SUM(A1)", null, null, null, null, false)]);
        store.UpsertNode(hello, null, NodeAcceptance.Accepted);
        store.EnsureZoneMics([new PlanZone { Id = 7, Name = "Balcony", Points = [new(0, 0), new(1, 0), new(1, 1)] }]);

        var start = 1_800_000_000L;
        var items = new List<SeqRecord>();
        long seq = 0;
        for (var t = start; t < start + 1800; t++)
        {
            var lc = 90 + 8 * Math.Sin((t - start) / 200.0);
            items.Add(new SeqRecord(++seq, new LevelRecord { Kind = RecordKind.Second, ChannelIndex = 0, Ts = t, Coverage = 1, LCeq = lc, LAeq = lc - 4, LZeq = lc + 1, LCpeak = lc + 12, LCSmax = lc + 1, Calibrated = true }));
            items.Add(new SeqRecord(++seq, new LevelRecord { Kind = RecordKind.Second, ChannelIndex = 1, Ts = t, Coverage = 1, LCeq = lc - 120, LAeq = lc - 124, LZeq = lc - 119, LCpeak = lc - 110, LCSmax = lc - 119 }));
        }
        store.StoreRecords("n1", items);
        store.StoreRecords(HubStore.ZoneNodeId, Enumerable.Range(0, 1800).Select(i =>
            new SeqRecord(0, new LevelRecord { Kind = RecordKind.Second, ChannelIndex = 7, Ts = start + i, Coverage = 1, LCeq = 85, LAeq = 81, LZeq = 86, LCpeak = -200, LCSmax = 85, Calibrated = true })).ToList());
        store.StartAlertEvent(1, "dBC 1-minute", "n1:0", "alarm", start + 300, 104.2, "LCeq1m");
        var svc = store.StartService("Sunday 11am", start, "schedule", null);
        store.EndService(svc.Id, start + 1800);
        return (tmp, store, new ReportService(store, settings, tmp.Paths, NullLogger<ReportService>.Instance), store.GetService(svc.Id)!);
    }

    [Fact]
    public void Service_pdf_renders_with_unusual_names_and_zones()
    {
        var (tmp, store, reports, svc) = Setup();
        using var _ = tmp; using var __ = store;
        var pdf = reports.ServicePdf(svc.Id);
        Assert.True(pdf.Length > 5000);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{4} Sunday 11am \(\d+\)\.pdf$", ReportService.FileName(svc));

        // Set RTA_REPORT_PREVIEW to a folder to get PNGs of the pages for a visual check.
        if (Environment.GetEnvironmentVariable("RTA_REPORT_PREVIEW") is { Length: > 0 } dir)
        {
            var i = 0;
            foreach (var png in reports.ServiceImages(svc.Id)) File.WriteAllBytes(Path.Combine(dir, $"report-page{++i}.png"), png);
        }
    }

    [Fact]
    public async Task Csv_has_rows_per_second_and_defuses_formulas()
    {
        var (tmp, store, reports, svc) = Setup();
        using var _ = tmp; using var __ = store;
        using var ms = new MemoryStream();
        await reports.WriteCsvAsync(ms, ["n1:0", "n1:1", "zones:7", "unknown"], svc.StartTs, svc.StartTs + 60, minutes: false, CancellationToken.None);
        var lines = Encoding.UTF8.GetString(ms.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("utc_time,local_time,source,", lines[0]);
        Assert.Equal(1 + 3 * 60, lines.Length);
        Assert.Contains(lines, l => l.Contains(",'=SUM(A1),"));      // formula neutralised
        Assert.Contains(lines, l => l.Contains(",zone,") && l.Contains("Balcony"));
        Assert.Equal("\"a,b\"", ReportService.Csv("a,b"));
        Assert.Equal("'+1", ReportService.Csv("+1"));
    }

    [Fact]
    public void Osc_messages_are_padded_big_endian()
    {
        var m = Osc.Message("/rta/x", 1.5f, 2, "hi");
        Assert.Equal(0, m.Length % 4);
        Assert.Equal("/rta/x\0\0", Encoding.ASCII.GetString(m, 0, 8));
        Assert.Equal(",fis\0\0\0\0", Encoding.ASCII.GetString(m, 8, 8));
        Assert.Equal(new byte[] { 0x3F, 0xC0, 0, 0 }, m[16..20]);   // 1.5f
        Assert.Equal(new byte[] { 0, 0, 0, 2 }, m[20..24]);
        Assert.Equal("hi\0\0", Encoding.ASCII.GetString(m, 24, 4));
    }

    [Theory]
    [InlineData("Front Left (Booth)", "front_left_booth")]
    [InlineData("  ", "x")]
    [InlineData("Ünder-Balcony #2", "under_balcony_2")]
    public void Slugs_are_stable_and_safe(string label, string slug) => Assert.Equal(slug, LevelsView.Slug(label));

    [Fact]
    public void Integration_settings_validation()
    {
        IntegrationSettings.Validate(new IntegrationSettings());
        Assert.Throws<ArgumentException>(() => IntegrationSettings.Validate(new IntegrationSettings { Webhooks = new WebhookSettings { Urls = ["file:///c:/x"] } }));
        Assert.Throws<ArgumentException>(() => IntegrationSettings.Validate(new IntegrationSettings { Osc = new OscSettings { Prefix = "rta" } }));
        Assert.Throws<ArgumentException>(() => IntegrationSettings.Validate(new IntegrationSettings { Midi = new MidiSettings { Mappings = [new("a", "lcs", 200)] } }));
        Assert.Throws<ArgumentException>(() => IntegrationSettings.Validate(new IntegrationSettings { ProPresenter = new ProPresenterSettings { MessageReadout = true } }));
    }
}
