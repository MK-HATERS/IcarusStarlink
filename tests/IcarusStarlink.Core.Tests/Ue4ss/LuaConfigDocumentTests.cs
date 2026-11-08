using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

public class LuaConfigDocumentTests
{
    // Shaped like MkHaters Wildlife's real config.lua.
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

    /// <summary>
    /// MkHaters Cheats / Wildlife (real): "-- ==== GENERAL ====" headings used to land
    /// in the first setting's description, and nothing was grouped. They name a group now.
    /// </summary>
    [Fact]
    public void CommentHeadings_GroupTheSettings_AndAreNotADescription()
    {
        const string text =
            "-- MkHaters Cheats settings.\r\n" +
            "return {\r\n" +
            "    -- ==== GENERAL ====\r\n" +
            "    -- Turns the whole mod on or off.\r\n" +
            "    Enabled = true,               -- [restart]\r\n" +
            "    OpenKey = { key = \"F2\" },\r\n" +
            "\r\n" +
            "    -- ==== ITEMS ====\r\n" +
            "    DefaultAmount = 1,            -- [slider 1 100 1]\r\n" +
            "    -- ------------------------\r\n" +
            "    MaxAmount = 1000,\r\n" +
            "}\r\n";
        var doc = LuaConfigDocument.Parse(text);
        var settings = doc.Settings.ToDictionary(s => s.Key);

        Assert.Equal(("GENERAL", "Turns the whole mod on or off.\n[restart]"), (settings["Enabled"].Group, settings["Enabled"].Description));
        Assert.Equal("GENERAL", settings["OpenKey"].Group);
        Assert.Equal(("ITEMS", ""), (settings["DefaultAmount"].Group, string.Join("|", settings["DefaultAmount"].Comments)));
        // A bare rule is no heading and no description.
        Assert.Equal(("ITEMS", ""), (settings["MaxAmount"].Group, settings["MaxAmount"].Description));
        Assert.Equal(0, doc.UnknownLineCount);
        Assert.Equal(text, doc.ToText());
    }

    /// <summary>
    /// MkHaters Cheats AllowGodMode (real, seen in the app): a sentence wrapped over two
    /// comment lines showed broken mid-sentence ("the server's" / "setting decides"). It reads as
    /// one line now; a finished sentence or a list item still starts a new line.
    /// </summary>
    [Fact]
    public void ACommentSentenceWrappedOverLines_ReadsAsOneLine()
    {
        const string text =
            "return {\r\n" +
            "    -- Whether players may switch god mode on. Read where the world runs: on a dedicated server, the server's\r\n" +
            "    -- setting decides for everyone who joins it.\r\n" +
            "    AllowGodMode = true,\r\n" +
            "    -- How hard it is.\r\n" +
            "    -- - Easy: fewer\r\n" +
            "    -- - Hard: more\r\n" +
            "    Level = 1,\r\n" +
            "}\r\n";
        var settings = LuaConfigDocument.Parse(text).Settings.ToDictionary(s => s.Key);

        Assert.Equal(
            "Whether players may switch god mode on. Read where the world runs: on a dedicated server, the server's setting decides for everyone who joins it.",
            settings["AllowGodMode"].Description);
        Assert.Equal("How hard it is.\n- Easy: fewer\n- Hard: more", settings["Level"].Description);

        // The config window reads the comment lines through DescribeLuaSetting (the first fix
        // only changed Description, and the window still showed the break).
        var godMode = settings["AllowGodMode"];
        var shown = Ue4ssSettingsDocument.DescribeLuaSetting(godMode.Key, godMode.Value, godMode.Kind, godMode.Comments, godMode.Note);
        Assert.Contains("the server's setting decides", shown.Description);
    }

    /// <summary>Untrusted input: long comment lines of dashes and = (a heading-looking line that never closes) stay quick to read.</summary>
    [Fact]
    public void LongRuleLikeComments_ParseQuickly()
    {
        var line = "-- " + new string('-', 5000) + " x " + new string('=', 5000) + "y\n";
        var text = "return {\n" + string.Concat(Enumerable.Repeat(line, 200)) + "    A = 1,\n}\n";
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var doc = LuaConfigDocument.Parse(text);

        Assert.Equal("A", Assert.Single(doc.Settings).Key);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
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

    /// <summary>A downloaded config with a long run of digits ending in a letter: the number check tried every split of the digits (18 s for 20,000 on the UI thread).</summary>
    [Fact]
    public void ALongRunOfDigitsThatIsntANumber_ParsesQuickly()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var doc = LuaConfigDocument.Parse("return {\n    A = " + new string('1', 50_000) + "x,\n    B = 1.5e3,\n    C = .5,\n    D = 2.,\n}\n");

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        Assert.Equal(["B", "C", "D"], doc.Settings.Select(s => s.Key));
        Assert.All(doc.Settings, s => Assert.Equal(LuaConfigValueKind.Number, s.Kind));
    }

    /// <summary>A key written twice: Lua keeps the last value, so the editor shows and edits that line (it edited the first, which nothing reads).</summary>
    [Fact]
    public void AKeyWrittenTwice_IsShownOnce_AndTheLastLineIsEdited()
    {
        var doc = LuaConfigDocument.Parse("return {\n    Speed = 1,\n    Debug = false,\n    Speed = 3, -- pasted from a forum\n}\n");

        Assert.Equal(["Debug", "Speed"], doc.Settings.Select(s => s.Key));
        Assert.Equal("3", doc.GetValue("Speed"));
        Assert.Null(doc.SetValue("Speed", "5"));
        Assert.Equal("return {\n    Speed = 1,\n    Debug = false,\n    Speed = 5, -- pasted from a forum\n}\n", doc.ToText());
    }

    /// <summary>An empty list inside the settings ("NoGuests = {}" in MkHaters Wildlife) is a setting the player fills in, not something that can't be read.</summary>
    [Fact]
    public void AnEmptyListInsideTheSettings_IsASetting()
    {
        var doc = LuaConfigDocument.Parse("return {\n    NoGuests = {},                -- e.g. { \"Yeti\" }\n    Debug = false,\n}\n");

        Assert.Equal(0, doc.UnknownLineCount);
        var noGuests = doc.Settings.First();
        Assert.Equal(("NoGuests", "{}", LuaConfigValueKind.Table), (noGuests.Key, noGuests.Value, noGuests.Kind));
        Assert.Null(doc.SetValue("NoGuests", "{ \"Yeti\" }"));
        Assert.StartsWith("return {\n    NoGuests = { \"Yeti\" },", doc.ToText());
    }

    /// <summary>"local Config = {}" … "return Config" is the frame of a Config.Key file, not something the update merge can't carry.</summary>
    [Fact]
    public void TheFrameOfAConfigDotKeyFile_IsNotUnknown()
    {
        var doc = LuaConfigDocument.Parse("local Config = {}\nConfig.Speed = 2\nConfig.Debug = false\nreturn Config\n");

        Assert.Equal(0, doc.UnknownLineCount);
        Assert.Equal(["Config.Speed", "Config.Debug"], doc.Settings.Select(s => s.Key));
    }

    /// <summary>
    /// Regression: in a Config.Key file, the lines inside "Config.Wolf = { … }" over several lines
    /// were read as top-level settings, so Wolf's and Bear's Count collapsed into one and an update
    /// rebuilt the file with only the last one's value.
    /// </summary>
    [Fact]
    public void AConfigDotKeyFileWithTablesOverSeveralLines_KeepsThemOutOfTheSettings()
    {
        var doc = LuaConfigDocument.Parse("local Config = {}\nConfig.Wolf = {\n    Enabled = true,\n    Count = 3,\n}\nConfig.Bear = {\n    Enabled = true,\n    Count = 2,\n}\nreturn Config\n");
        Assert.True(doc.UnknownLineCount > 0);
        Assert.DoesNotContain(doc.Settings, s => s.Key is "Count" or "Enabled");

        var mixed = LuaConfigDocument.Parse("local Config = {}\nConfig.Speed = 5\nConfig.Wolf = {\n    Count = 3,\n}\nreturn Config\n");
        Assert.Equal(["Config.Speed"], mixed.Settings.Select(s => s.Key));
        Assert.True(mixed.UnknownLineCount > 0);

        // Two top-level tables: the first is the settings, the second can't be told apart from code.
        var two = LuaConfigDocument.Parse("Wolf = {\n    Count = 3,\n}\nBear = {\n    Count = 2,\n}\n");
        Assert.Equal("3", two.GetValue("Count"));
        Assert.True(two.UnknownLineCount > 0);
    }

    /// <summary>Regression: a --[==[ long comment ]==] wasn't seen as one, so a commented-out "Speed = 1" inside it won the key and Save edited it.</summary>
    [Fact]
    public void LinesInsideALevelLongComment_AreNotSettings()
    {
        var doc = LuaConfigDocument.Parse("return {\n    Speed = 5,\n    --[==[ old values:\n    Speed = 1,\n    ]==]\n    Debug = false,\n}\n");

        Assert.Equal("5", doc.GetValue("Speed"));
        Assert.Null(doc.SetValue("Speed", "7"));
        var text = doc.ToText();
        Assert.Contains("    Speed = 7,\n    --[==[", text);
        Assert.Contains("    Speed = 1,", text);
    }

    /// <summary>Regression: "Speed = 5, --[[ old values:" opens a long comment, but the lines after it were read as settings.</summary>
    [Fact]
    public void ALongCommentOpenedAfterAValue_HidesTheLinesAfterIt()
    {
        var doc = LuaConfigDocument.Parse("return {\n    Speed = 5, --[[ old values:\n    Speed = 1,\n    ]]\n    Debug = false,\n}\n");

        Assert.Equal(["Speed", "Debug"], doc.Settings.Select(s => s.Key));
        Assert.Equal("5", doc.GetValue("Speed"));
    }

    /// <summary>A long comment on one line ("--[==[ note ]==]") is a comment block, not the description of the setting under it, the same as "--[[ note ]]".</summary>
    [Fact]
    public void AOneLineLevelLongComment_IsNotADescription()
    {
        var doc = LuaConfigDocument.Parse("return {\n    --[==[ note ]==]\n    A = 1,\n}\n");

        Assert.Equal("", Assert.Single(doc.Settings).Description);
    }

    /// <summary>Untrusted input: long odd lines (spaces, words, an unclosed string or table, a long key) stay quick to read and describe.</summary>
    [Theory]
    [InlineData("    ", " ")]
    [InlineData("    ", "a ")]
    [InlineData("    X = \"", "a ")]
    [InlineData("    X = ", "{\"a\"}")]
    [InlineData("    local ", " ")]
    [InlineData("    -- valid ", " ")]
    [InlineData("    -- requires ", "a ")]
    [InlineData("    Key", "a.b")]
    [InlineData("    --[", "=")]
    [InlineData("    A = 1, --[", "=")]
    public void ALongOddLine_ParsesQuickly(string start, string repeated)
    {
        var text = "return {\n" + start + string.Concat(Enumerable.Repeat(repeated, 50_000)) + "\n    A = 1,\n}\n";

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var doc = LuaConfigDocument.Parse(text);
        var described = doc.Settings.Select(s => Ue4ssSettingsDocument.DescribeLuaSetting(s.Key, s.Value, s.Kind, s.Comments, s.Note)).ToList();
        _ = described.Select(s => Ue4ssSettingLabel.FromKey(s.Key)).ToList();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
        Assert.Contains(described, s => s.Key == "A");
    }

    /// <summary>
    /// MkHaters Mod Settings 1.6.0's layout (real lines from Bags and Cheats): "-- [section]"
    /// headings, [tip] / [warning] notes, [name], [pair], [context]. Every setting landed in one group
    /// with "[section] Ammo Bag [tip] Keep the..." and "[name Arrows]" in its description.
    /// </summary>
    [Fact]
    public void MkHatersSettingsTags_GiveSectionsNotesNamesAndPairs_AndLeaveTheDescriptionClean()
    {
        const string text =
            "-- [MkHatersSettings] Bags [icon Bag] [order 4]\n" +
            "return {\n" +
            "    -- [section] Ammo Bag\n" +
            "    -- [tip] Keep the Ammo Bag in your backpack or quickbar, and guns and bows reload from it.\n" +
            "    SearchBackpack = true, -- The Ammo Bag is used while it's in your backpack. [server]\n" +
            "    WheelKey = \"R\", -- The key you hold for both wheels. [key] [shared Reload] [context Weapons]\n" +
            "\n" +
            "    -- [section] Ammo Rules\n" +
            "    Ammo_Arrows_Auto = true, -- Arrows in the Ammo Bag. [name Arrows] [restart] [server] [pair Ammo_Arrows_Allowed By itself|Allowed]\n" +
            "    Ammo_Arrows_Allowed = true, -- May be put in the Ammo Bag at all. [name Arrows] [restart] [server]\n" +
            "\n" +
            "    -- [section] Items\n" +
            "    -- [warning] A server can't switch off giving items. It is the game's own pickup call, which the server can't\n" +
            "    -- tell from a real pickup.\n" +
            "    DefaultAmount = 1, -- The amount in the box when the window opens. [slider 1 100 1] [important]\n" +
            "}\n";
        var doc = LuaConfigDocument.Parse(text);
        var settings = doc.Settings.ToDictionary(s => s.Key, s => (Raw: s, Shown: Ue4ssSettingsDocument.DescribeLuaSetting(s.Key, s.Value, s.Kind, s.Comments, s.Note)));

        Assert.Equal(["Ammo Bag", "Ammo Bag", "Ammo Rules", "Ammo Rules", "Items"], doc.Settings.Select(s => s.Group));
        var backpack = settings["SearchBackpack"].Shown;
        Assert.Equal(("Keep the Ammo Bag in your backpack or quickbar, and guns and bows reload from it.", Ue4ssNoticeKind.Tip), (backpack.Notice, backpack.NoticeKind));
        Assert.Equal("The Ammo Bag is used while it's in your backpack.", backpack.Description);
        Assert.Equal("The key you hold for both wheels.", settings["WheelKey"].Shown.Description);
        var arrows = settings["Ammo_Arrows_Auto"].Shown;
        Assert.Equal(("Arrows", "Arrows in the Ammo Bag.", true), (arrows.DisplayName, arrows.Description, arrows.NeedsRestart));
        Assert.Equal(("Ammo_Arrows_Allowed", "By itself", "Allowed"), (arrows.PairKey, arrows.PairOwnLabel, arrows.PairLabel));
        var amount = settings["DefaultAmount"].Shown;
        Assert.Equal(Ue4ssNoticeKind.Warning, amount.NoticeKind);
        Assert.Equal("A server can't switch off giving items. It is the game's own pickup call, which the server can't tell from a real pickup.", amount.Notice);
        Assert.Equal(("The amount in the box when the window opens.", true, 100d), (amount.Description, amount.Important, amount.Maximum));
        Assert.Equal(0, doc.UnknownLineCount);
    }

    [Theory]
    [InlineData("all", "All")]
    [InlineData("on foot", "On Foot")]
    [InlineData("NoStability", "NoStability")]
    public void AChoiceIsShownCapitalised(string choice, string shown) => Assert.Equal(shown, Ue4ssSettingLabel.ChoiceCaption(choice));
}
