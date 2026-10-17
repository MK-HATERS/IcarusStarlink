using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.Exmod;

/// <summary>Every row the game has ever had: the shipped history plus the user's own refreshes.</summary>
public sealed class GameRowHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "RowHistory_" + Guid.NewGuid().ToString("N"));

    private string DataFolder => Path.Combine(_root, "Data");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void WriteTable(params string[] rows)
    {
        var path = Path.Combine(DataFolder, "Items", "D_ItemsStatic.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, new JsonObject { ["Rows"] = new JsonArray([.. rows.Select(r => (JsonNode)new JsonObject { ["Name"] = r })]) }.ToJsonString());
    }

    [Fact]
    public void Shipped_KnowsRowsTheGameRemovedYearsAgo()
    {
        var history = GameRowHistory.Load(localFile: null);

        Assert.True(history.VersionCount > 100);
        var motor = history.Find("Items-D_ItemsStatic.json", "electric_motor")!;
        Assert.Equal(("Week 142", "Week 251"), (motor.FirstSeen, motor.LastSeen));
        Assert.Null(history.Find("Items-D_ItemsStatic.json", "Some_Mods_Own_New_Item"));
    }

    [Fact]
    public void Record_AddsEachRefresh_SoALaterRemovalIsKnown()
    {
        var local = GameRowHistory.LocalFileFor(DataFolder);
        WriteTable("Brand_New_Game_Item_2099", "Kept_Item_2099");
        GameRowHistory.Record(DataFolder, local, new DateTimeOffset(2099, 1, 5, 0, 0, 0, TimeSpan.Zero));
        WriteTable("Kept_Item_2099");
        GameRowHistory.Record(DataFolder, local, new DateTimeOffset(2099, 1, 12, 0, 0, 0, TimeSpan.Zero));

        var history = GameRowHistory.Load(local);

        Assert.Equal(Path.Combine(_root, "Cache", "game_row_history_local.json"), local);
        var gone = history.Find("Items-D_ItemsStatic.json", "Brand_New_Game_Item_2099")!;
        var kept = history.Find("Items-D_ItemsStatic.json", "Kept_Item_2099")!;
        Assert.Equal(("5 Jan 2099", "5 Jan 2099"), (gone.FirstSeen, gone.LastSeen));
        Assert.Equal(("5 Jan 2099", "12 Jan 2099"), (kept.FirstSeen, kept.LastSeen));
        Assert.Equal(history.Latest, kept.Last);
        Assert.Equal(kept.Last - 1, gone.Last);
    }

    [Fact]
    public void Load_ADamagedLocalFile_StillHasTheShippedHistory()
    {
        var local = GameRowHistory.LocalFileFor(DataFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, "not json");

        Assert.NotNull(GameRowHistory.Load(local).Find("Items-D_ItemsStatic.json", "Electric_Motor"));
    }

    private static ExmodPackage Package(params (string File, string Row, JsonNode? Value)[] rows)
    {
        var package = new ExmodPackage { Name = "M", Author = "A", Version = "1", Description = "", FileName = "M" };
        foreach (var file in rows.GroupBy(r => r.File))
        {
            package.Rows.Add(new ExmodFileRow { CurrentFile = file.Key, FileItems = [.. file.Select(r => new ExmodFileItem { Name = r.Row, Fields = new() { ["Weight"] = r.Value } })] });
        }

        package.Rows.Add(new ExmodFileRow { CurrentFile = "EndOfMod" });
        return package;
    }

    [Fact]
    public void BuiltFor_ReadsTheGameVersionFromTheRowsAModCarries()
    {
        var history = GameRowHistory.Load(localFile: null);
        const string items = "Items-D_ItemsStatic.json";
        var old = Package((items, "Electric_Motor", 1), (items, "Cobalt_Ingot", 1), (items, "Wood_Floor", 1), (items, "Canteen", 1), (items, "Envirosuit", 1), (items, "Its_Own_New_Item", 1));
        var current = Package((items, "Wood_Floor", 1), (items, "Canteen", 1), (items, "Envirosuit", 1), (items, "Waterskin", 1), (items, "Wood_Wall", 1));

        var built = ModGameVersionEstimator.Estimate(old, history)!;

        Assert.True(built.OlderThanGame);
        Assert.Equal(5, built.RowsCompared);
        Assert.Equal("Made for an older game: between Week 222 and 4 Sep 2026", built.OlderText);
        Assert.Null(ModGameVersionEstimator.Estimate(current, history)!.OlderText);
        Assert.Null(ModGameVersionEstimator.Estimate(Package((items, "Electric_Motor", 1)), history));
    }

    /// <summary>Two fake game versions: Old_Pickaxe_Steel in the first, renamed to Pickaxe_Steel in the second.</summary>
    private GameRowHistory TwoVersions(string[] first, string[] second)
    {
        var local = GameRowHistory.LocalFileFor(DataFolder);
        WriteTable(first);
        GameRowHistory.Record(DataFolder, local, new DateTimeOffset(2099, 2, 1, 0, 0, 0, TimeSpan.Zero));
        WriteTable(second);
        GameRowHistory.Record(DataFolder, local, new DateTimeOffset(2099, 2, 8, 0, 0, 0, TimeSpan.Zero));
        return GameRowHistory.Load(local);
    }

    [Fact]
    public void BrokenLink_SaysWhenTheGameRemovedWhatItPointsTo()
    {
        var consumables = Path.Combine(DataFolder, "Traits", "D_Consumable.json");
        Directory.CreateDirectory(Path.GetDirectoryName(consumables)!);
        var local = GameRowHistory.LocalFileFor(DataFolder);
        File.WriteAllText(consumables, """{"Rows":[{"Name":"BasicWater"},{"Name":"Old_Drink_2099"}]}""");
        GameRowHistory.Record(DataFolder, local, new DateTimeOffset(2099, 3, 1, 0, 0, 0, TimeSpan.Zero));
        File.WriteAllText(consumables, """{"Rows":[{"Name":"BasicWater"}]}""");
        var items = Path.Combine(DataFolder, "Items", "D_ItemsStatic.json");
        Directory.CreateDirectory(Path.GetDirectoryName(items)!);
        File.WriteAllText(items, """{"Defaults":{"Consumable":{"RowName":"None","DataTableName":"D_Consumable"}},"Rows":[{"Name":"Canteen","Consumable":{"RowName":"BasicWater"}}]}""");
        GameRowHistory.Record(DataFolder, local, new DateTimeOffset(2099, 3, 8, 0, 0, 0, TimeSpan.Zero));
        var package = new ExmodPackage { Name = "M", Author = "A", Version = "1", Description = "", FileName = "M" };
        package.Rows.Add(new ExmodFileRow { CurrentFile = "Items-D_ItemsStatic.json", FileItems = [new ExmodFileItem { Name = "Canteen", Fields = new() { ["Consumable"] = new JsonObject { ["RowName"] = "Old_Drink_2099" } } }] });
        package.Rows.Add(new ExmodFileRow { CurrentFile = "EndOfMod" });

        var issue = Assert.Single(ModHealthChecker.Check(package, DataFolder, rowHistory: GameRowHistory.Load(local)).Issues);

        Assert.Equal(ModIssueKind.BrokenLink, issue.Kind);
        Assert.Contains("which the game removed after 1 Mar 2099", issue.Why);
    }

    [Fact]
    public void ARenamedRow_RenamedInOneUpdate_IsPreselected_AndAMadeUpRowIsNeverCalledRenamed()
    {
        var history = TwoVersions(["Old_Pickaxe_Steel", "Wood_Floor"], ["Pickaxe_Steel", "Wood_Floor"]);
        var package = Package(("Items-D_ItemsStatic.json", "Old_Pickaxe_Steel", 5), ("Items-D_ItemsStatic.json", "Pickaxe_Steell", 5));

        var issues = ModHealthChecker.Check(package, DataFolder, rowHistory: history).Issues.Where(i => i.Kind == ModIssueKind.MissingRow).ToList();

        // Pickaxe_Steell never was a game row: the mod's own, so no rename is offered for it.
        Assert.DoesNotContain(issues, i => i.ItemName == "Pickaxe_Steell");
        var renamed = Assert.Single(issues, i => i.ItemName == "Old_Pickaxe_Steel");
        if (renamed is not null)
        {
            Assert.Equal(ModIssueFix.RenameRow, renamed.DefaultFix);
            Assert.Contains("renamed this row to 'Pickaxe_Steel' in the update after", renamed.Why);
        }
    }

    [Fact]
    public void Checker_ARowTheHistoryKnows_IsAProvenOldGameRow_EvenWithNoSecondMod()
    {
        WriteTable("Wood_Floor");
        var package = new ExmodPackage { Name = "Old", Author = "A", Version = "1", Description = "", FileName = "Old" };
        package.Rows.Add(new ExmodFileRow
        {
            CurrentFile = "Items-D_ItemsStatic.json",
            FileItems = [new ExmodFileItem { Name = "Electric_Motor", Fields = new() { ["Weight"] = 1 } }, new ExmodFileItem { Name = "My_New_Thing", Fields = new() { ["Weight"] = 1 } }],
        });
        package.Rows.Add(new ExmodFileRow { CurrentFile = "EndOfMod" });

        var report = ModHealthChecker.Check(package, DataFolder, fromPrebuiltPak: true, rowHistory: GameRowHistory.Load(localFile: null));

        var issue = Assert.Single(report.Issues, i => i.Kind == ModIssueKind.OldGameRow);
        Assert.Equal("Electric_Motor", issue.ItemName);
        Assert.Contains("until Week 251", issue.Why);
        Assert.DoesNotContain(report.Issues, i => i.ItemName == "My_New_Thing" && i.Kind == ModIssueKind.OldGameRow);
    }
}
