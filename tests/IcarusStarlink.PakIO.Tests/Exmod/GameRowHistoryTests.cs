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
        Assert.Equal(new GameRowSighting("Week 142", "Week 251"), history.Find("Items-D_ItemsStatic.json", "electric_motor"));
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
        Assert.Equal(new GameRowSighting("5 Jan 2099", "5 Jan 2099"), history.Find("Items-D_ItemsStatic.json", "Brand_New_Game_Item_2099"));
        Assert.Equal(new GameRowSighting("5 Jan 2099", "12 Jan 2099"), history.Find("Items-D_ItemsStatic.json", "Kept_Item_2099"));
    }

    [Fact]
    public void Load_ADamagedLocalFile_StillHasTheShippedHistory()
    {
        var local = GameRowHistory.LocalFileFor(DataFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        File.WriteAllText(local, "not json");

        Assert.NotNull(GameRowHistory.Load(local).Find("Items-D_ItemsStatic.json", "Electric_Motor"));
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
