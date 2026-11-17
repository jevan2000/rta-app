using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rta.App.Hub.Integrations;

public sealed record ProPresenterMessage(string Id, string Name, IReadOnlyList<string> Tokens);

/// <summary>
/// ProPresenter network API (7.9+, including 17–22): Settings → Network → Enable Network in
/// ProPresenter, then use its IP and port here. Uses the stage message for alarms and an optional
/// Message (with a text token) for a live readout.
/// </summary>
public sealed class ProPresenterClient(HttpClient http)
{
    private static Uri Base(ProPresenterSettings s) => new($"http://{s.Host}:{s.Port}/");

    /// <summary>Returns ProPresenter's description of itself (name, version) as text.</summary>
    public async Task<string> VersionAsync(ProPresenterSettings s, CancellationToken ct)
    {
        using var res = await http.GetAsync(new Uri(Base(s), "version"), ct);
        res.EnsureSuccessStatusCode();
        var json = await res.Content.ReadFromJsonAsync<JsonObject>(ct);
        var parts = new[] { "name", "platform", "os_version", "host_description", "api_version" }
            .Select(k => json?[k]?.ToString()).Where(v => !string.IsNullOrWhiteSpace(v));
        return string.Join(" · ", parts);
    }

    public async Task SetStageMessageAsync(ProPresenterSettings s, string text, CancellationToken ct)
    {
        using var res = await http.PutAsync(new Uri(Base(s), "v1/stage/message"), JsonBody(JsonSerializer.Serialize(text)), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task ClearStageMessageAsync(ProPresenterSettings s, CancellationToken ct)
    {
        using var res = await http.DeleteAsync(new Uri(Base(s), "v1/stage/message"), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ProPresenterMessage>> MessagesAsync(ProPresenterSettings s, CancellationToken ct)
    {
        using var res = await http.GetAsync(new Uri(Base(s), "v1/messages"), ct);
        res.EnsureSuccessStatusCode();
        var arr = await res.Content.ReadFromJsonAsync<JsonArray>(ct) ?? [];
        var list = new List<ProPresenterMessage>();
        foreach (var m in arr)
        {
            var id = m?["id"]?["uuid"]?.ToString() ?? m?["id"]?.ToString();
            var name = m?["id"]?["name"]?.ToString() ?? m?["name"]?.ToString() ?? "(unnamed)";
            if (id is null) continue;
            var tokens = (m?["tokens"] as JsonArray)?.Select(t => t?["name"]?.ToString()).OfType<string>().ToList() ?? [];
            list.Add(new ProPresenterMessage(id, name, tokens));
        }
        return list;
    }

    /// <summary>Shows a Message with one text token filled in. Token format per the v1 API's message trigger.</summary>
    public async Task TriggerMessageAsync(ProPresenterSettings s, string messageId, string token, string text, CancellationToken ct)
    {
        var body = new JsonArray(new JsonObject { ["name"] = token, ["text"] = new JsonObject { ["text"] = text } });
        using var res = await http.PostAsync(new Uri(Base(s), $"v1/message/{Uri.EscapeDataString(messageId)}/trigger"), JsonBody(body.ToJsonString()), ct);
        res.EnsureSuccessStatusCode();
    }

    public async Task ClearMessageAsync(ProPresenterSettings s, string messageId, CancellationToken ct)
    {
        using var res = await http.GetAsync(new Uri(Base(s), $"v1/message/{Uri.EscapeDataString(messageId)}/clear"), ct);
        res.EnsureSuccessStatusCode();
    }

    public static string ReadoutLine(LevelsSnapshot snap, ProPresenterSettings s)
    {
        var metric = LevelsView.MetricName(s.ReadoutMetric);
        var unit = metric.StartsWith("la") ? "dBA" : "dBC";
        var parts = s.ReadoutSources
            .Select(k => LevelsView.Find(snap, k))
            .OfType<LevelSource>()
            .Select(src => $"{src.Label} {(src.Values.GetValueOrDefault(metric) is double v ? v.ToString("0") : "—")}");
        var line = string.Join("  ·  ", parts);
        return line.Length == 0 ? "" : $"{line} {unit}";
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>JSON with a Content-Length (not chunked): some embedded HTTP servers can't read chunked uploads.</summary>
    public static StringContent JsonBody(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
