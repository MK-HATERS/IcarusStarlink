using IcarusStarlink.PakIO.Worlds;

namespace IcarusStarlink.PakIO.Tests.Worlds;

public sealed class WorldSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WorldSettingsTests_" + Guid.NewGuid().ToString("N"));

    public WorldSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // Laid out the way the game writes a prospect: tabs, CRLF, ProspectBlob after ProspectInfo.
    private const string SaveWithSettings =
        "{\r\n\t\"ProspectInfo\":\r\n\t{\r\n\t\t\"ProspectID\": \"My World\",\r\n\t\t\"ProspectDTKey\": \"OpenWorld_Elysium\",\r\n\t\t\"Difficulty\": \"Hard\",\r\n\t\t\"NoRespawns\": false,\r\n" +
        "\t\t\"CustomSettings\": [\r\n\t\t\t{\r\n\t\t\t\t\"SettingRowName\": \"SettingsVersion\",\r\n\t\t\t\t\"SettingValue\": 1\r\n\t\t\t},\r\n\t\t\t{\r\n\t\t\t\t\"SettingRowName\": \"Arachnophobia\",\r\n\t\t\t\t\"SettingValue\": 0\r\n\t\t\t}\r\n\t\t],\r\n" +
        "\t\t\"ProspectState\": \"Active\"\r\n\t},\r\n\t\"ProspectBlob\":\r\n\t{\r\n\t\t\"BinaryBlob\": \"AAAA]}[{\\\"CustomSettings\\\": []\"\r\n\t}\r\n}";

    private const string SaveWithoutSettings =
        "{\r\n\t\"ProspectInfo\":\r\n\t{\r\n\t\t\"ProspectID\": \"Old World\",\r\n\t\t\"Difficulty\": \"Easy\",\r\n\t\t\"NoRespawns\": true\r\n\t},\r\n\t\"ProspectBlob\":\r\n\t{\r\n\t\t\"BinaryBlob\": \"BBBB\"\r\n\t}\r\n}";

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Picking a world reads the game's settings table: a damaged one means no settings to show, never a crash.</summary>
    [Theory]
    [InlineData("not json")]
    [InlineData("""{"Defaults":{},"Rows":[{"Name":"Odd","bHidden":1,"StatType":"Bool"},{"Name":"Fine","StatType":"Bool"}]}""")]
    public void Catalog_ADamagedTable_DoesntThrow(string json)
    {
        var data = Path.Combine(_root, "Data");
        Directory.CreateDirectory(Path.Combine(data, "Stats"));
        File.WriteAllText(Path.Combine(data, "Stats", "D_CustomGameStats.json"), json);

        var settings = WorldSettingsCatalog.Load(data);

        Assert.All(settings, s => Assert.Contains(s.Name, new[] { "Odd", "Fine" }));
    }

    [Fact]
    public void Load_ReadsWhatTheWorldIs()
    {
        var world = WorldSave.Load(Write("a.json", SaveWithSettings))!;

        Assert.Equal("My World", world.Name);
        Assert.Equal("OpenWorld_Elysium", world.ProspectType);
        Assert.Equal("Hard", world.Difficulty);
        Assert.False(world.Hardcore);
        Assert.Equal(0, world.Settings["arachnophobia"]);
        Assert.Equal(1, world.Settings["SettingsVersion"]);
    }

    [Fact]
    public void Load_NotAWorldSave_IsNull()
    {
        Assert.Null(WorldSave.Load(Write("x.json", """{"Something": 1}""")));
        Assert.Null(WorldSave.Load(Write("y.json", "not json")));
    }

    [Fact]
    public void Save_NoChanges_ReproducesTheFileExactly()
    {
        var path = Write("a.json", SaveWithSettings);
        WorldSave.Load(path)!.Save(new Dictionary<string, int>(), Path.Combine(_root, "bk"));

        Assert.Equal(SaveWithSettings, File.ReadAllText(path));
    }

    [Fact]
    public void Save_ChangesOnlyTheSettings_KeepsTheBlobAndBacksUpFirst()
    {
        var path = Write("a.json", SaveWithSettings);
        var backup = WorldSave.Load(path)!.Save(new Dictionary<string, int> { ["Arachnophobia"] = 1, ["GreatHuntEffects"] = 0 }, Path.Combine(_root, "bk"));

        var after = File.ReadAllText(path);
        Assert.Equal(SaveWithSettings, File.ReadAllText(backup));
        Assert.Equal(SaveWithSettings[SaveWithSettings.IndexOf("\"ProspectBlob\"")..], after[after.IndexOf("\"ProspectBlob\"")..]);
        Assert.Contains("\t\t\t\t\"SettingRowName\": \"GreatHuntEffects\",\r\n\t\t\t\t\"SettingValue\": 0\r\n", after);

        var reread = WorldSave.Load(path)!;
        Assert.Equal(1, reread.Settings["Arachnophobia"]);
        Assert.Equal(0, reread.Settings["GreatHuntEffects"]);
        Assert.Equal(1, reread.Settings["SettingsVersion"]);
        Assert.Equal("Active", reread.State);
    }

    [Fact]
    public void Save_OlderSaveWithoutTheList_AddsIt()
    {
        var path = Write("old.json", SaveWithoutSettings);
        WorldSave.Load(path)!.Save(new Dictionary<string, int> { ["Arachnophobia"] = 1 }, Path.Combine(_root, "bk"));

        var reread = WorldSave.Load(path)!;
        Assert.Equal(1, reread.Settings["Arachnophobia"]);
        Assert.True(reread.Hardcore);
        Assert.Contains("\"BinaryBlob\": \"BBBB\"", File.ReadAllText(path));
    }

    /// <summary>A CustomSettings that isn't a list: the next '[' anywhere was taken, in the world blob, and that text was cut out. Now nothing is written.</summary>
    [Fact]
    public void Save_CustomSettingsNotAList_ThrowsAndLeavesTheWorldExactlyAsItWas()
    {
        var text = SaveWithSettings.Replace(
            SaveWithSettings[SaveWithSettings.IndexOf("[\r\n\t\t\t{")..(SaveWithSettings.IndexOf("\t\t],") + 3)],
            "null");
        Assert.Contains("\"CustomSettings\": null,", text);
        var path = Write("a.json", text);

        Assert.Throws<FormatException>(() => WorldSave.Load(path)!.Save(new Dictionary<string, int> { ["Arachnophobia"] = 1 }, Path.Combine(_root, "bk")));

        Assert.Equal(text, File.ReadAllText(path));
    }

    /// <summary>The game rewrote the world after it was loaded: settings the user didn't touch keep the file's newer value.</summary>
    [Fact]
    public void Save_FileChangedSinceLoad_KeepsTheNewerValuesOfUntouchedSettings()
    {
        var path = Write("a.json", SaveWithSettings);
        var world = WorldSave.Load(path)!;
        File.WriteAllText(path, SaveWithSettings.Replace("\"SettingValue\": 1", "\"SettingValue\": 2"));

        world.Save(new Dictionary<string, int> { ["Arachnophobia"] = 1 }, Path.Combine(_root, "bk"));

        var reread = WorldSave.Load(path)!;
        Assert.Equal(2, reread.Settings["SettingsVersion"]);
        Assert.Equal(1, reread.Settings["Arachnophobia"]);
        Assert.Equal(2, world.Settings["SettingsVersion"]);
    }

    [Fact]
    public void Save_TwiceInARow_KeepsBothBackups()
    {
        var path = Write("a.json", SaveWithSettings);
        var world = WorldSave.Load(path)!;
        var first = world.Save(new Dictionary<string, int> { ["Arachnophobia"] = 1 }, Path.Combine(_root, "bk"));
        var second = world.Save(new Dictionary<string, int> { ["Arachnophobia"] = 0 }, Path.Combine(_root, "bk"));

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "bk")).Length);
    }

    [Fact]
    public void LoadFolder_SkipsFilesThatArentWorlds()
    {
        var folder = Path.Combine(_root, "Prospects");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.json"), SaveWithSettings);
        File.WriteAllText(Path.Combine(folder, "b.json"), "{}");
        File.WriteAllText(Path.Combine(folder, "a.json.backup"), SaveWithSettings);

        Assert.Single(WorldSave.LoadFolder(folder));
        Assert.Empty(WorldSave.LoadFolder(Path.Combine(_root, "missing")));
    }

    [Fact]
    public void Catalog_ReadsVisibleSettings_WithDefaultsAndPlainText()
    {
        var stats = Path.Combine(_root, "Data", "Stats");
        Directory.CreateDirectory(stats);
        File.WriteAllText(Path.Combine(stats, "D_CustomGameStats.json"), """
            {
              "Defaults": { "StatType": "Bool", "StatChangeability": "OnProspectCreation", "DefaultValue": 0, "bHidden": false },
              "Rows": [
                { "Name": "SettingsVersion", "bHidden": true },
                { "Name": "Arachnophobia", "DisplayName": "NSLOCTEXT(\"D_CustomGameStats\", \"A\", \"Arachnophobia Mode\")", "StatChangeability": "Anytime" },
                { "Name": "LootLevel", "StatType": "DropDown", "DefaultValue": 1, "DropDownOptions": ["Low", "Normal", "High"] }
              ]
            }
            """);

        var defs = WorldSettingsCatalog.Load(Path.Combine(_root, "Data"));

        Assert.Equal(["Arachnophobia", "LootLevel"], defs.Select(d => d.Name));
        Assert.Equal("Arachnophobia Mode", defs[0].DisplayName);
        Assert.True(defs[0].ChangeableAnytime);
        Assert.False(defs[1].ChangeableAnytime);
        Assert.Equal(["Low", "Normal", "High"], defs[1].Options);
        Assert.Equal(1, defs[1].DefaultValue);
        Assert.Empty(WorldSettingsCatalog.Load(Path.Combine(_root, "nowhere")));
    }
}
