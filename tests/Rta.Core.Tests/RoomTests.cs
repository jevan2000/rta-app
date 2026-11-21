using Rta.App.Hub;
using Rta.Core.Zones;

namespace Rta.Core.Tests;

public class RoomTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 1, 2, 3];

    private static Plan Room(string floorName, string? imageId = null) => new()
    {
        Floors = [new Floor
        {
            Id = "main", Name = floorName, Width = 20, Height = 15,
            Underlay = imageId is null ? null : new Underlay(imageId, 0, 0, 20, 15),
            Shapes = [new PlanShape { Id = "s1", Kind = "stage", Points = [new(1, 1), new(5, 1), new(5, 3)] }],
            Zones = [new PlanZone { Id = 0, Name = "Front", Points = [new(1, 5), new(9, 5), new(9, 9), new(1, 9)] }],
            Mics = [new MicPlacement("n1:0", 10, 14)],
        }],
    };

    private static SurveyMeasurement Survey(double lc) =>
        new(null, lc - 3, lc, [new SurveyRef("n1:0", null, lc - 8, lc - 5, 120, null, 0.5, 6)], 60, 0.9);

    [Fact]
    public void Every_save_keeps_the_previous_plan_and_it_can_be_put_back()
    {
        using var tmp = new TempData();
        var plans = new PlanStore(tmp.Paths);
        plans.Save(Room("First"));
        Assert.Empty(plans.Versions());                      // nothing replaced yet
        plans.Save(Room("Second"));
        plans.Save(Room("Third"));
        var versions = plans.Versions();
        Assert.Equal(2, versions.Count);
        Assert.Equal("Second", plans.ReadVersion(versions[0].Name)!.Floors[0].Name);   // newest first
        Assert.Equal("First", plans.ReadVersion(versions[1].Name)!.Floors[0].Name);
        Assert.Contains("1 zones", versions[0].Summary);

        plans.Save(plans.ReadVersion(versions[1].Name)!);
        Assert.Equal("First", new PlanStore(tmp.Paths).Current.Floors[0].Name);       // survives a restart
        Assert.Null(plans.ReadVersion("..\\plan.json"));
    }

    [Fact]
    public void A_room_exports_with_images_and_surveys_and_imports_on_another_hub()
    {
        using var a = new TempData();
        using var b = new TempData();
        var plansA = new PlanStore(a.Paths);
        var image = plansA.SaveImage(Png);
        var savedA = plansA.Save(Room("Sanctuary", image));
        var zoneId = savedA.Floors[0].Zones[0].Id;
        using var storeA = new HubStore(a.Paths);
        storeA.AddSurvey(zoneId, "Rover", 1000, 1060, "n2:0", "row 5", Survey(95), new() { ["n1:0"] = new Pt(10, 14) });
        storeA.AddSurvey(zoneId + 50, "Rover", 1000, 1060, null, "other zone", Survey(90), []);   // not in this room
        var room = Rooms.Export(plansA, storeA, new HubSettingsStore(a.Paths));
        Assert.Single(room.Images);
        Assert.Single(room.Surveys);

        // Through JSON, as the file would be.
        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(room, HubStore.SurveyJson);
        var read = Rooms.Read(new MemoryStream(json));

        var plansB = new PlanStore(b.Paths);
        plansB.Save(Room("Old room"));
        using var storeB = new HubStore(b.Paths);
        storeB.AddSurvey(zoneId, "Rover", 500, 560, null, "stale survey of the old room", Survey(80), []);
        var state = new HubState(storeB, new HubSettingsStore(b.Paths));
        var r = Rooms.Import(read, plansB, storeB, b.Paths, state);

        Assert.Equal("Sanctuary", r.Plan.Floors[0].Name);
        Assert.Equal(zoneId, r.Plan.Floors[0].Zones[0].Id);                           // ids kept, so surveys line up
        Assert.Equal(1, r.Surveys);
        Assert.Equal(1, r.MissingMics);                                                // n1:0 isn't on this Hub
        var survey = Assert.Single(storeB.ListSurveys(zoneId));
        Assert.Equal("row 5", survey.Note);                                            // the old room's survey was replaced
        Assert.Equal(new Pt(10, 14), survey.RefPositions["n1:0"]);
        var newImage = r.Plan.Floors[0].Underlay!.ImageId;
        Assert.NotEqual(image, newImage);
        Assert.Equal(Png, plansB.ReadImage(newImage));
        Assert.Equal("Old room", plansB.ReadVersion(plansB.Versions()[0].Name)!.Floors[0].Name);  // previous plan kept
        Assert.NotNull(Rta.App.Backup.BackupArchive.FindBackup(b.Dir, r.SafetyBackup!));
    }

    [Fact]
    public void Files_that_arent_rooms_are_refused()
    {
        Assert.Throws<ArgumentException>(() => Rooms.Read(new MemoryStream("{\"format\":\"something\"}"u8.ToArray())));
        Assert.Throws<ArgumentException>(() => Rooms.Read(new MemoryStream("not json"u8.ToArray())));
        Assert.Throws<ArgumentException>(() => Rooms.Read(new MemoryStream("{\"format\":\"rta-room\",\"version\":9}"u8.ToArray())));
    }
}
