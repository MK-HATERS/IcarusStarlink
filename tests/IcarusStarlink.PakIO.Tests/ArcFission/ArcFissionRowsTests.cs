using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.ArcFission;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.PakIO.Tests.ArcFission;

/// <summary>
/// The ArcFission mods' rows read straight from their rows.lua (as ArcFission Library 1.3.0 reads
/// them), so the merge has them before the game ever ran, and never the library's pak with its
/// copies of the game's tables. Files shaped like WirelessPower 0.9.5's on the user's setup.
/// </summary>
public sealed class ArcFissionRowsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "ArcFission_" + Guid.NewGuid().ToString("N"));

    private string Content => Path.Combine(_root, "Icarus", "Content");

    private string ModsFolder => Path.Combine(_root, "Icarus", "Binaries", "Win64", "ue4ss", "Mods");

    private string DataFolder => Path.Combine(_root, "Data");

    public ArcFissionRowsTests()
    {
        Directory.CreateDirectory(Content);
        Library(enabled: true);
        Directory.CreateDirectory(Path.Combine(DataFolder, "Crafting"));
        File.WriteAllText(Path.Combine(DataFolder, "Crafting", "D_ProcessorRecipes.json"),
            """{"RowStruct":"S","Defaults":{},"Rows":[{"Name":"Stone_Pickaxe","CraftTime":5}]}""");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private void Library(bool enabled)
    {
        var folder = Path.Combine(ModsFolder, "ArcFissionLibrary");
        Directory.CreateDirectory(Path.Combine(folder, "Scripts"));
        File.WriteAllText(Path.Combine(folder, "Scripts", "main.lua"), "local LIBRARY_VERSION = \"1.3.0\"\nlocal FORMAT = 1\n");
        var flag = Path.Combine(folder, "enabled.txt");
        if (enabled)
        {
            File.WriteAllText(flag, "");
        }
        else
        {
            File.Delete(flag);
        }
    }

    private void Mod(string folderName, string id, string rows, bool enabled = true, string library = "1.0.0", string version = "0.9.5")
    {
        var folder = Path.Combine(ModsFolder, folderName);
        Directory.CreateDirectory(Path.Combine(folder, "ArcFission"));
        File.WriteAllText(Path.Combine(folder, "ArcFission", "mod.lua"), $$"""
            -- ArcFission identity card (mod format 1). Read by the ArcFission Library.
            return {
                arcfission = 1,
                author     = "ArcFission",
                id         = "{{id}}",
                name       = "{{id}} mod",
                version    = "{{version}}",
                library    = "{{library}}",
                rows       = "rows.lua",
                paks       = { "ZZ_{{id}}_Model_P.pak" },
            }
            """);
        File.WriteAllText(Path.Combine(folder, "ArcFission", "rows.lua"), rows);
        if (enabled)
        {
            File.WriteAllText(Path.Combine(folder, "enabled.txt"), "");
        }
    }

    private static string Rows(params (string Table, string Name, int Count)[] rows) =>
        "-- ArcFission rows (format 1): rows this mod ADDS to the game's data tables.\nreturn {\n    format = 1,\n    tables = {\n"
        + string.Join("", rows.GroupBy(r => r.Table).Select(t =>
            $"        [\"{t.Key}\"] = {{\n"
            + string.Join("", t.Select(r => $$"""
                { name = "{{r.Name}}", json = [[        {
                            "Name": "{{r.Name}}",
                            "Inputs": [ { "Element": { "RowName": "Glass", "DataTableName": "D_ItemsStatic" }, "Count": {{r.Count}} } ]
                        }]] },

                """))
            + "        },\n"))
        + "    },\n}\n";

    [Fact]
    public void Scan_ReadsTheRowsOfTheModsTurnedOn_AsTheLibraryWould()
    {
        Mod("WirelessPower", "WirelessPower", Rows(("Crafting/D_ProcessorRecipes.json", "Wireless_Power_Node", 10)));
        Mod("Off", "Off", Rows(("Crafting/D_ProcessorRecipes.json", "Off_Row", 1)), enabled: false);
        Mod("TooNew", "TooNew", Rows(("Crafting/D_ProcessorRecipes.json", "New_Row", 1)), library: "9.0");
        Directory.CreateDirectory(Path.Combine(ModsFolder, "Keybinds", "Scripts"));

        var scan = ArcFissionRows.Scan(Content)!;

        var mod = Assert.Single(scan.Mods);
        Assert.Equal(("WirelessPower", "0.9.5"), (mod.Id, mod.Version));
        var row = Assert.Single(mod.Rows);
        Assert.Equal(("Crafting-D_ProcessorRecipes.json", "Wireless_Power_Node"), (row.CurrentFile, row.Name));
        Assert.Equal(10, row.Row["Inputs"]![0]!["Count"]!.GetValue<int>());
        Assert.Contains(scan.Problems, p => p.Contains("TooNew") && p.Contains("needs ArcFission Library 9.0"));
        Assert.True(scan.HasRows);
        Assert.StartsWith("ArcFission rows ", scan.ManifestName);
    }

    [Fact]
    public void Scan_WithoutTheLibraryTurnedOn_IsNothing()
    {
        Mod("WirelessPower", "WirelessPower", Rows(("Crafting/D_ProcessorRecipes.json", "Wireless_Power_Node", 10)));
        Library(enabled: false);

        Assert.Null(ArcFissionRows.Scan(Content));
    }

    [Fact]
    public void TheFingerprint_ChangesWithTheRows_SoTheInstalledMergeIsKnownToBeOld()
    {
        Mod("WirelessPower", "WirelessPower", Rows(("Crafting/D_ProcessorRecipes.json", "Wireless_Power_Node", 10)));
        var before = ArcFissionRows.Scan(Content)!.ManifestName;
        Assert.Equal(before, ArcFissionRows.Scan(Content)!.ManifestName);

        Mod("WirelessPower", "WirelessPower", Rows(("Crafting/D_ProcessorRecipes.json", "Wireless_Power_Node", 20)));

        Assert.NotEqual(before, ArcFissionRows.Scan(Content)!.ManifestName);
    }

    [Fact]
    public void ToPackage_SkipsRowsTheGameHas_AndARowTwoModsAddGoesToTheFirst()
    {
        Mod("WirelessPower", "WirelessPower", Rows(("Crafting/D_ProcessorRecipes.json", "Wireless_Power_Node", 10), ("Crafting/D_ProcessorRecipes.json", "stone_pickaxe", 1)));
        Mod("Another", "Another", Rows(("Crafting/D_ProcessorRecipes.json", "Wireless_Power_Node", 99)));
        var problems = new List<string>();

        var package = ArcFissionRows.ToPackage(ArcFissionRows.Scan(Content)!, new GameTableIndex(DataFolder), problems);

        // "Another" sorts first by id, as the library sorts: it keeps the row.
        var item = Assert.Single(Assert.Single(package.Rows).FileItems);
        Assert.Equal("Wireless_Power_Node", item.Name);
        Assert.Equal(99, item.Fields["Inputs"]![0]!["Count"]!.GetValue<int>());
        Assert.False(item.Fields.ContainsKey("Name"));
        Assert.Contains(problems, p => p.Contains("already has row 'stone_pickaxe'"));
        Assert.Contains(problems, p => p.Contains("also added by Another mod"));

        var changes = ExmodFieldChangeMapper.ToFieldChanges(package, new DefaultSemanticClassifier());
        Assert.All(changes, c => Assert.True(c.IsNewItem));
    }

    [Fact]
    public void ABrokenModIsSaid_AndTheOthersStillCount()
    {
        Mod("WirelessPower", "WirelessPower", Rows(("Crafting/D_ProcessorRecipes.json", "Wireless_Power_Node", 10)));
        Mod("Broken", "Broken", "return { tables = { [\"Crafting/D_ProcessorRecipes.json\"] = { { name = \"X\", json = os.execute(\"calc\") } } } }");

        var scan = ArcFissionRows.Scan(Content)!;

        Assert.Equal("WirelessPower", Assert.Single(scan.Mods).Id);
        Assert.Contains(scan.Problems, p => p.StartsWith("Broken:"));
    }

    [Fact]
    public void LuaData_ReadsDataOnly()
    {
        var table = Assert.IsType<LuaTable>(LuaData.Parse("""
            --[[ a block
                 comment ]]
            return {
                a = 1, b = -2.5e1; c = 0x10,
                ["d e"] = 'it\'s', [3] = true, nested = { "x", [==[long ]] still]==], nil },
            }
            """));
        Assert.Equal(1d, table.Fields["a"]);
        Assert.Equal(-25d, table.Fields["b"]);
        Assert.Equal(16d, table.Fields["c"]);
        Assert.Equal("it's", table.Fields["d e"]);
        Assert.Equal(true, table.Fields["3"]);
        Assert.Equal(["x", "long ]] still", null], table.Table("nested")!.Items);

        Assert.Throws<FormatException>(() => LuaData.Parse("return os.execute('calc')"));
        Assert.Throws<FormatException>(() => LuaData.Parse("return { a = f() }"));
        Assert.Throws<FormatException>(() => LuaData.Parse("x = 1"));
        Assert.Throws<FormatException>(() => LuaData.Parse("return { a = 1 } extra"));
    }
}
