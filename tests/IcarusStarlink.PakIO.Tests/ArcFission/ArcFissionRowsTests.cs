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

    /// <summary>Review findings: whatever a downloaded card or rows file holds, it's one mod's problem, never the scan's.</summary>
    [Fact]
    public void HostileCards_AreOneModsProblem_AndTheOthersStillCount()
    {
        Mod("WirelessPower", "WirelessPower", Rows(("Crafting/D_ProcessorRecipes.json", "Wireless_Power_Node", 10)));
        // A 20-digit version number (it used to overflow and break the conflict check).
        Mod("HugeOld", "HugeOld", Rows(("Crafting/D_ProcessorRecipes.json", "Huge_Old", 1)), library: "1.0.99999999999999999999");
        Mod("HugeNew", "HugeNew", Rows(("Crafting/D_ProcessorRecipes.json", "Huge_New", 1)), library: "99999999999999999999.0");
        // A rows file name with a NUL in it (it used to drop every ArcFission row without a word).
        Mod("NulName", "NulName", Rows(("Crafting/D_ProcessorRecipes.json", "Nul_Row", 1)));
        File.WriteAllText(Path.Combine(ModsFolder, "NulName", "ArcFission", "mod.lua"),
            """return { arcfission = 1, author = "ArcFission", id = "NulName", rows = "a\0b" }""");
        // A rows file over the size limit.
        Mod("Huge", "Huge", "return { tables = {} }" + new string(' ', 5 * 1024 * 1024));

        var scan = ArcFissionRows.Scan(Content)!;

        Assert.Equal(["HugeOld", "WirelessPower"], scan.Mods.Select(m => m.Id));
        Assert.Contains(scan.Problems, p => p.StartsWith("HugeNew:") && p.Contains("needs ArcFission Library"));
        Assert.Contains(scan.Problems, p => p.StartsWith("NulName:"));
        Assert.Contains(scan.Problems, p => p.StartsWith("Huge:") && p.Contains("larger than"));
    }

    /// <summary>QuickSave (rows = false) only registers with the library: adding or updating it changes no data, so no "Changes waiting".</summary>
    [Fact]
    public void AModWithNoRows_DoesntChangeTheFingerprint()
    {
        Mod("WirelessPower", "WirelessPower", Rows(("Crafting/D_ProcessorRecipes.json", "Wireless_Power_Node", 10)));
        var before = ArcFissionRows.Scan(Content)!.ManifestName;
        var folder = Path.Combine(ModsFolder, "QuickSave");
        Directory.CreateDirectory(Path.Combine(folder, "ArcFission"));
        File.WriteAllText(Path.Combine(folder, "enabled.txt"), "");
        File.WriteAllText(Path.Combine(folder, "ArcFission", "mod.lua"),
            """return { arcfission = 1, author = "ArcFission", id = "QuickSave", version = "0.2.3", library = "1.2.1", rows = false }""");

        var scan = ArcFissionRows.Scan(Content)!;

        Assert.Equal(2, scan.Mods.Count);
        Assert.Equal(before, scan.ManifestName);
    }

    /// <summary>Lists as the library's ipairs reads them: numbered ([1] = …, how the library writes lists) count, and a nil ends the list.</summary>
    [Fact]
    public void RowsAsANumberedList_AndANilEndingIt_ReadAsTheLibraryReadsThem()
    {
        Mod("WirelessPower", "WirelessPower", """
            return { tables = { ["Crafting/D_ProcessorRecipes.json"] = {
                [1] = { name = "First", json = [[{ "Name": "First" }]] },
                [2] = { name = "Second", json = [[{ "Name": "Second" }]] },
            }, ["Traits/D_Energy.json"] = { { name = "Kept", json = [[{ "Name": "Kept" }]] }, nil, { name = "AfterNil", json = "{}" } } } }
            """);

        var rows = Assert.Single(ArcFissionRows.Scan(Content)!.Mods).Rows.Select(r => r.Name);

        Assert.Equal(["First", "Second", "Kept"], rows);
    }

    [Fact]
    public void LuaData_ReadsTheEscapesLuaHas()
    {
        Assert.Equal("A\u00e9H-ok\nend", LuaData.Parse("return \"\\x41\\u{E9}\\72-\\z\r\n      ok\\\r\nend\""));
        Assert.Throws<FormatException>(() => LuaData.Parse("return \"\\300\""));
        Assert.Throws<FormatException>(() => LuaData.Parse("return \"\\xZZ\""));
    }

    /// <summary>
    /// A Lua string is bytes: "\xC3\xA9" and "\195\169" are the two UTF-8 bytes of é (how Lua's own
    /// %q and many serializers write text). They used to come out as two Latin-1 characters ("Ã©"),
    /// so a row's text was silently wrong in the merge (review 2026-10-26).
    /// </summary>
    [Fact]
    public void LuaData_EscapedBytesAreUtf8_LikeTheGameReadsThem()
    {
        Assert.Equal("Caf\u00e9 \u00e9", LuaData.Parse("return \"Caf\\xC3\\xA9 \\195\\169\""));
        Assert.Equal("\u00e9\u00e9", LuaData.Parse("return \"\\xC3\\xA9\u00e9\""));
        Assert.Equal("a\uFFFDb", LuaData.Parse("return \"a\\xFFb\""));
    }

    /// <summary>Lua's own rules for line breaks (review 2026-10-26): every \r\n, \n\r or lone \r in a [[long string]] is one \n, and a lone \r ends a -- comment, so the next field isn't swallowed by it.</summary>
    [Fact]
    public void LuaData_LineBreaks_AsLuaReadsThem()
    {
        Assert.Equal("a\nb\nc\nd", LuaData.Parse("return [[a\r\nb\rc\n\rd]]"));
        Assert.Equal("x", LuaData.Parse("return [[\n\rx]]"));
        Assert.Equal("a\nb", LuaData.Parse("return \"a\\\n\rb\""));

        var table = Assert.IsType<LuaTable>(LuaData.Parse("return { a = 1, -- note\rb = 2 }"));
        Assert.Equal(2d, table.Fields["b"]);
    }

    /// <summary>Words aren't numbers: "-Infinity" or "-nan" is an expression Lua refuses in a data file, not a value (review 2026-10-26).</summary>
    [Fact]
    public void LuaData_InfinityAndNanWordsAreRefused()
    {
        Assert.Throws<FormatException>(() => LuaData.Parse("return -Infinity"));
        Assert.Throws<FormatException>(() => LuaData.Parse("return { arcfission = -NaN }"));
        Assert.Equal(-0.5, LuaData.Parse("return -.5"));
        Assert.Equal(1e10, LuaData.Parse("return 1E+10"));
        Assert.Equal(-255d, LuaData.Parse("return -0xFF"));
    }

    /// <summary>
    /// The library checks a card's "library" with its vercmp whatever its type, so library = 2 (a
    /// number) needs library 2 and the mod is left out of the game. It used to be read only as text,
    /// so a number skipped the check and the merge carried rows the game doesn't have (review 2026-10-26).
    /// </summary>
    [Fact]
    public void ACardNeedingANewerLibrary_AsANumber_IsLeftOutToo()
    {
        Mod("WirelessPower", "WirelessPower", Rows(("Crafting/D_ProcessorRecipes.json", "Wireless_Power_Node", 10)));
        Mod("NumberNeed", "NumberNeed", Rows(("Crafting/D_ProcessorRecipes.json", "Number_Row", 1)));
        File.WriteAllText(Path.Combine(ModsFolder, "NumberNeed", "ArcFission", "mod.lua"),
            """return { arcfission = 1, author = "ArcFission", id = "NumberNeed", library = 2 }""");

        var scan = ArcFissionRows.Scan(Content)!;

        Assert.Equal("WirelessPower", Assert.Single(scan.Mods).Id);
        Assert.Contains(scan.Problems, p => p.StartsWith("NumberNeed:") && p.Contains("needs ArcFission Library 2"));
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
