using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

public class LuaConfigDocumentTests
{
    // Shaped like MkHaters Wildlife's real config.lua (2026-10-18).
    private const string Realistic =
        "-- MkHaters Wildlife settings.\r\n" +
        "return {\r\n" +
        "    -- Turns the whole mod on or off.\r\n" +
        "    Enabled = true,\r\n" +
        "\r\n" +
        "    DefendRadius = 5000,          -- how far adults come from\r\n" +
        "    ProtectDamageMultiplier = 0.5, -- 0.5 = half damage\r\n" +
        "    BagItemRow = \"MkHaters_Ammo_Bag\",\r\n" +
        "    Herds = {\r\n" +
        "        { young = \"Moa_Juvenile\",     adults = { \"Moa\" } },\r\n" +
        "        { young = \"BearCub\", adults = { \"Bear\" } },        -- also BearCub_Juvenile\r\n" +
        "    },\r\n" +
        "    Predators = { \"Wolf\", \"Bear\" },\r\n" +
        "    Debug = false,                -- extra detail\r\n" +
        "}\r\n";

    [Fact]
    public void Parse_ThenToText_KeepsTheFileExactly() =>
        Assert.Equal(Realistic, LuaConfigDocument.Parse(Realistic).ToText());

    [Fact]
    public void Settings_AreTheTopLevelOneLineValues_WithTheirComments()
    {
        var settings = LuaConfigDocument.Parse(Realistic).Settings.ToDictionary(s => s.Key);

        Assert.Equal(["Enabled", "DefendRadius", "ProtectDamageMultiplier", "BagItemRow", "Predators", "Debug"], settings.Keys);
        Assert.Equal((LuaConfigValueKind.Boolean, "true", "Turns the whole mod on or off."), (settings["Enabled"].Kind, settings["Enabled"].Value, settings["Enabled"].Description));
        Assert.Equal((LuaConfigValueKind.Number, "5000", "how far adults come from"), (settings["DefendRadius"].Kind, settings["DefendRadius"].Value, settings["DefendRadius"].Description));
        Assert.Equal((LuaConfigValueKind.String, "MkHaters_Ammo_Bag"), (settings["BagItemRow"].Kind, settings["BagItemRow"].Value));
        Assert.Equal((LuaConfigValueKind.Table, "{ \"Wolf\", \"Bear\" }"), (settings["Predators"].Kind, settings["Predators"].Value));
    }

    [Fact]
    public void SetValue_ChangesOnlyTheValue()
    {
        var doc = LuaConfigDocument.Parse(Realistic);

        Assert.Null(doc.SetValue("DefendRadius", "7500"));
        Assert.Null(doc.SetValue("Debug", "true"));
        Assert.Null(doc.SetValue("BagItemRow", "My \"Bag\""));
        Assert.Null(doc.SetValue("Predators", "{ \"Wolf\" }"));

        var text = doc.ToText();
        Assert.Contains("    DefendRadius = 7500,          -- how far adults come from\r\n", text);
        Assert.Contains("    Debug = true,                -- extra detail\r\n", text);
        Assert.Contains("    BagItemRow = \"My \\\"Bag\\\"\",\r\n", text);
        Assert.Contains("    Predators = { \"Wolf\" },\r\n", text);
        Assert.Equal("My \"Bag\"", LuaConfigDocument.Parse(text).GetValue("BagItemRow"));
        // A second change to the same line still lands on the value.
        Assert.Null(doc.SetValue("DefendRadius", "1"));
        Assert.Contains("    DefendRadius = 1,          -- how far", doc.ToText());
    }

    /// <summary>
    /// The mod config editor sends every field back on Save. An untouched string holding \r, \065
    /// or \x41 used to come back without its backslashes, and one holding \n came back refused
    /// ("can't span lines"), so saving any other field failed.
    /// </summary>
    [Fact]
    public void SetValue_EveryFieldSentBackUnchanged_LeavesEscapedStringsExactlyAsWritten()
    {
        const string text = "return {\n    Sep = \"a\\r\\nb\",\n    Code = \"\\065\\x41\",\n    Tab = \"x\\ty\",\n    Speed = 1,\n}\n";
        var doc = LuaConfigDocument.Parse(text);

        foreach (var setting in doc.Settings)
        {
            Assert.Null(doc.SetValue(setting.Key, setting.Value));
        }

        Assert.Equal(text, doc.ToText());
        Assert.Equal("a\r\nb", doc.GetValue("Sep"));
        Assert.Equal("\\065\\x41", doc.GetValue("Code"));
    }

    [Fact]
    public void SetValue_AStringWithALineBreak_IsWrittenAsAnEscape()
    {
        var doc = LuaConfigDocument.Parse("return {\n    Sep = \"a\",\n}\n");

        Assert.Null(doc.SetValue("Sep", "one\ntwo\tthree"));

        Assert.Equal("return {\n    Sep = \"one\\ntwo\\tthree\",\n}\n", doc.ToText());
        Assert.Equal("one\ntwo\tthree", LuaConfigDocument.Parse(doc.ToText()).GetValue("Sep"));
    }

    [Theory]
    [InlineData("DefendRadius", "far")]
    [InlineData("Enabled", "yes")]
    [InlineData("Predators", "{ \"Wolf\"")]
    [InlineData("Herds", "{}")]
    public void SetValue_RefusesWhatCantBeWritten(string key, string value)
    {
        var doc = LuaConfigDocument.Parse(Realistic);

        Assert.NotNull(doc.SetValue(key, value));
        Assert.Equal(Realistic, doc.ToText());
    }

    [Fact]
    public void GlobalAssignments_AreSettingsToo()
    {
        var doc = LuaConfigDocument.Parse("local Config = {}\nConfig.Speed = 2 -- times faster\nConfig.Key = Key.F5\nreturn Config\n");

        var setting = Assert.Single(doc.Settings);
        Assert.Equal(("Config.Speed", "2", "times faster"), (setting.Key, setting.Value, setting.Description));
    }

    [Fact]
    public void MixedLineEndings_StayAsTheyWere()
    {
        // NearbyCrafting.ini really mixes them, as does any file edited in two different editors.
        const string mixed = "return {\r\n    A = 1,\n    B = false,\r\n}\n";
        var lua = LuaConfigDocument.Parse(mixed);
        Assert.Null(lua.SetValue("B", "true"));
        Assert.Equal("return {\r\n    A = 1,\n    B = true,\r\n}\n", lua.ToText());

        var ini = Ue4ssSettingsDocument.Parse("[A]\r\nX = 1\nY = 2\r\n", keepCommentLines: true);
        ini.SetValue("A", "Z", "3");
        Assert.Equal("[A]\r\nX = 1\nY = 2\r\nZ = 3\r\n", ini.ToText());
    }
}
