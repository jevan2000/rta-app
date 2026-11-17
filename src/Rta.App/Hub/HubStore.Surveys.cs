using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Rta.Core.Calibration;
using Rta.Core.Protocol;
using Rta.Core.Zones;

namespace Rta.App.Hub;

public sealed record SurveyRow(
    long Id, int ZoneId, string Kind, long StartTs, long EndTs, double Seconds, double Confidence,
    string? RoverKey, bool Excluded, string? Note, SurveyMeasurement Measurement,
    /// <summary>Where each reference mic was on the plan when surveyed (to flag moves).</summary>
    Dictionary<string, Pt> RefPositions);

public sealed partial class HubStore
{
    /// <summary>Survey data may contain NaN (e.g. a manual entry with only dBC), so it uses its own options.</summary>
    private static readonly JsonSerializerOptions SurveyJson = new(CalibrationStore.Json)
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        WriteIndented = false,
    };

    private bool _surveyTables;

    private void EnsureSurveyTables()
    {
        if (_surveyTables) return;
        Exec("""
            CREATE TABLE IF NOT EXISTS surveys (
                id INTEGER PRIMARY KEY, zone_id INTEGER NOT NULL, kind TEXT NOT NULL, start_ts INTEGER NOT NULL, end_ts INTEGER NOT NULL,
                seconds REAL NOT NULL, confidence REAL NOT NULL, rover_key TEXT, excluded INTEGER NOT NULL DEFAULT 0, note TEXT,
                data TEXT NOT NULL, ref_positions TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS surveys_zone ON surveys(zone_id, start_ts);
            """);
        _surveyTables = true;
    }

    public SurveyRow AddSurvey(int zoneId, string kind, long startTs, long endTs, string? roverKey, string? note,
        SurveyMeasurement m, Dictionary<string, Pt> refPositions)
    {
        lock (_lock)
        {
            EnsureSurveyTables();
            using var cmd = Cmd("""
                INSERT INTO surveys (zone_id,kind,start_ts,end_ts,seconds,confidence,rover_key,excluded,note,data,ref_positions)
                VALUES ($z,$k,$s,$e,$sec,$c,$r,0,$n,$d,$p); SELECT last_insert_rowid();
                """, ("$z", zoneId), ("$k", kind), ("$s", startTs), ("$e", endTs), ("$sec", m.Seconds), ("$c", m.Confidence),
                ("$r", (object?)roverKey ?? DBNull.Value), ("$n", (object?)note ?? DBNull.Value),
                ("$d", JsonSerializer.Serialize(m, SurveyJson)), ("$p", JsonSerializer.Serialize(refPositions, SurveyJson)));
            var id = (long)cmd.ExecuteScalar()!;
            return new SurveyRow(id, zoneId, kind, startTs, endTs, m.Seconds, m.Confidence, roverKey, false, note, m, refPositions);
        }
    }

    public List<SurveyRow> ListSurveys(int? zoneId = null)
    {
        lock (_lock)
        {
            EnsureSurveyTables();
            using var cmd = zoneId is int z
                ? Cmd("SELECT id,zone_id,kind,start_ts,end_ts,seconds,confidence,rover_key,excluded,note,data,ref_positions FROM surveys WHERE zone_id=$z ORDER BY start_ts", ("$z", z))
                : Cmd("SELECT id,zone_id,kind,start_ts,end_ts,seconds,confidence,rover_key,excluded,note,data,ref_positions FROM surveys ORDER BY start_ts");
            using var r = cmd.ExecuteReader();
            var list = new List<SurveyRow>();
            while (r.Read())
            {
                var m = JsonSerializer.Deserialize<SurveyMeasurement>(r.GetString(10), SurveyJson);
                if (m is null) continue;
                list.Add(new SurveyRow(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetInt64(3), r.GetInt64(4), r.GetDouble(5),
                    r.GetDouble(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetInt32(8) == 1, r.IsDBNull(9) ? null : r.GetString(9), m,
                    JsonSerializer.Deserialize<Dictionary<string, Pt>>(r.GetString(11), SurveyJson) ?? []));
            }
            return list;
        }
    }

    public void UpdateSurvey(long id, bool excluded, string? note)
    {
        lock (_lock)
        {
            EnsureSurveyTables();
            using var cmd = Cmd("UPDATE surveys SET excluded=$x, note=$n WHERE id=$id", ("$x", excluded ? 1 : 0), ("$n", (object?)note ?? DBNull.Value), ("$id", id));
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteSurvey(long id)
    {
        lock (_lock)
        {
            EnsureSurveyTables();
            using var cmd = Cmd("DELETE FROM surveys WHERE id=$id", ("$id", id));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Registers zones as virtual mics ("zones:&lt;id&gt;") so their predictions log like any mic.</summary>
    public void EnsureZoneMics(IEnumerable<PlanZone> zones)
    {
        var now = Now();
        lock (_lock)
            foreach (var z in zones)
                UpsertMicLocked(ZoneNodeId, new MicInfo(z.Id, z.Name, null, "Predicted", null, null, true), now);
    }

    public const string ZoneNodeId = "zones";
    public static string ZoneKey(int zoneId) => MicKey(ZoneNodeId, zoneId);
}
