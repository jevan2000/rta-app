namespace Rta.App.Hub.Integrations;

public sealed record NetTarget(string Host, int Port);

public sealed record OscSettings
{
    public bool Enabled { get; init; }
    public List<NetTarget> Targets { get; init; } = [new("127.0.0.1", 9000)];
    /// <summary>Address prefix, e.g. "/rta" → /rta/mic/front_left/lceq1m.</summary>
    public string Prefix { get; init; } = "/rta";
    /// <summary>Level messages per second (alerts are sent immediately).</summary>
    public double RateHz { get; init; } = 2;
}

public sealed record MidiCcMapping(string Source, string Metric, int Cc, double MinDb = 60, double MaxDb = 110);

public sealed record MidiSettings
{
    public bool Enabled { get; init; }
    public string? Device { get; init; }
    /// <summary>1–16.</summary>
    public int Channel { get; init; } = 1;
    public List<MidiCcMapping> Mappings { get; init; } = [];
    /// <summary>Note sent (on/off) while any warn / alarm is active; -1 = none.</summary>
    public int WarnNote { get; init; } = 60;
    public int AlarmNote { get; init; } = 61;
}

public sealed record WebhookSettings
{
    public bool Enabled { get; init; }
    public List<string> Urls { get; init; } = [];
    public bool OnAlertClear { get; init; } = true;
    public bool OnService { get; init; } = true;
}

public sealed record ProPresenterSettings
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 1025;
    /// <summary>Show alarms (and warnings, optionally) as a stage-display message.</summary>
    public bool StageAlarms { get; init; } = true;
    public bool StageWarnings { get; init; }
    /// <summary>Keep a live level line on the stage display (alarms take over while active).</summary>
    public bool StageReadout { get; init; }
    public List<string> ReadoutSources { get; init; } = [];
    public string ReadoutMetric { get; init; } = "LCeq1m";
    /// <summary>Opt-in: also update a ProPresenter Message (which can appear on the audience output).</summary>
    public bool MessageReadout { get; init; }
    public string? MessageId { get; init; }
    public string TokenName { get; init; } = "level";
}

public sealed record ReportSettings
{
    public bool AutoGenerate { get; init; } = true;
    /// <summary>Folder for automatic PDF reports; empty = &lt;data folder&gt;\reports.</summary>
    public string? Folder { get; init; }
}

public sealed record IntegrationSettings
{
    public OscSettings Osc { get; init; } = new();
    public MidiSettings Midi { get; init; } = new();
    public WebhookSettings Webhooks { get; init; } = new();
    public ProPresenterSettings ProPresenter { get; init; } = new();
    public ReportSettings Reports { get; init; } = new();

    public static void Validate(IntegrationSettings s)
    {
        foreach (var t in s.Osc.Targets)
        {
            if (string.IsNullOrWhiteSpace(t.Host) || t.Host.Length > 253) throw new ArgumentException("OSC target host is required.");
            if (t.Port is < 1 or > 65535) throw new ArgumentException("OSC port must be 1–65535.");
        }
        if (s.Osc.Targets.Count > 16) throw new ArgumentException("At most 16 OSC targets.");
        if (!s.Osc.Prefix.StartsWith('/') || s.Osc.Prefix.Contains(' ')) throw new ArgumentException("OSC prefix must start with / and contain no spaces.");
        if (s.Osc.RateHz is < 0.2 or > 20) throw new ArgumentException("OSC rate must be 0.2–20 per second.");
        if (s.Midi.Channel is < 1 or > 16) throw new ArgumentException("MIDI channel must be 1–16.");
        foreach (var m in s.Midi.Mappings)
        {
            if (m.Cc is < 0 or > 127) throw new ArgumentException("MIDI CC numbers are 0–127.");
            if (m.MaxDb <= m.MinDb) throw new ArgumentException("MIDI mapping: the top of the dB range must be above the bottom.");
        }
        if (s.Midi.WarnNote is < -1 or > 127 || s.Midi.AlarmNote is < -1 or > 127) throw new ArgumentException("MIDI notes are 0–127 (or -1 for none).");
        if (s.Webhooks.Urls.Count > 10) throw new ArgumentException("At most 10 webhook URLs.");
        foreach (var u in s.Webhooks.Urls)
            if (!Uri.TryCreate(u, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException($"Webhook URL '{u}' must be an http(s) address.");
        if (string.IsNullOrWhiteSpace(s.ProPresenter.Host)) throw new ArgumentException("ProPresenter host is required.");
        if (s.ProPresenter.Port is < 1 or > 65535) throw new ArgumentException("ProPresenter port must be 1–65535.");
        if (s.ProPresenter.MessageReadout && string.IsNullOrWhiteSpace(s.ProPresenter.MessageId))
            throw new ArgumentException("Choose a ProPresenter message for the readout.");
    }
}
