using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.Core.Tests.Ue4ss;

public class Ue4ssSettingsDocumentTests
{
    // Trimmed from a real dev-build UE4SS-settings.ini (2026-10-17).
    private const string RealisticIni =
        "[Overrides]\r\n" +
        "; Path to the 'Mods' folder\r\n" +
        "; Default: <dll_directory>/Mods\r\n" +
        "ModsFolderPath =\r\n" +
        "\r\n" +
        "[General]\r\n" +
        "; Whether to reload all mods when the key defined by HotReloadKey is hit.\r\n" +
        "; Default: 1\r\n" +
        "EnableHotReloadSystem = 0\r\n" +
        "\r\n" +
        "; The key that will trigger a reload of all mods.\r\n" +
        "; Valid values (case-insensitive): Anything from Mods/Keybinds/Scripts/main.lua\r\n" +
        "; Default: R\r\n" +
        "HotReloadKey = R\r\n" +
        "\r\n" +
        "; Whether to create UObject listeners in GUObjectArray.\r\n" +
        "; Default: true\r\n" +
        "bUseUObjectArrayCache = false\r\n" +
        "\r\n" +
        "; The default execution method for ExecuteInGameThread Lua function.\r\n" +
        "; Valid values (case-insensitive): EngineTick, ProcessEvent\r\n" +
        "; Default: EngineTick\r\n" +
        "DefaultExecuteInGameThreadMethod = EngineTick\r\n" +
        "\r\n" +
        "[Debug]\r\n" +
        "; Multiplier for Font Size within the Debug Gui\r\n" +
        "; Default: 1\r\n" +
        "GuiConsoleFontScaling = 1\r\n";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AKeyListedTwiceInASection_ShowsAndEditsTheLastOne_TheValueThatIsRead(bool modConfig)
    {
        // A mod's Lua config reader fills a table line by line, and UE4SS's own ini parser writes
        // each line into the same entry: either way the last line wins. Editing the first changed
        // a line nothing reads.
        var doc = Ue4ssSettingsDocument.Parse("[General]\nSpeed = 1\nOther = x\nSpeed = 2\n[Debug]\nSpeed = 9\n", keepCommentLines: modConfig);

        Assert.Equal("2", doc.GetValue("General", "Speed"));
        var general = doc.Settings.Where(s => s.Section == "General").ToList();
        Assert.Equal(["Other", "Speed"], general.Select(s => s.Key));
        Assert.Equal("2", general.Single(s => s.Key == "Speed").Value);
        Assert.Equal("9", doc.GetValue("Debug", "Speed"));

        Assert.True(doc.SetValue("General", "Speed", "5"));
        Assert.Equal("[General]\nSpeed = 1\nOther = x\nSpeed = 5\n[Debug]\nSpeed = 9\n", doc.ToText());
    }

    [Fact]
    public void Parse_ThenToText_KeepsTheFileExactly() =>
        Assert.Equal(RealisticIni, Ue4ssSettingsDocument.Parse(RealisticIni).ToText());

    [Fact]
    public void Settings_ReadKindsDefaultsAndDescriptions()
    {
        var settings = Ue4ssSettingsDocument.Parse(RealisticIni).Settings.ToDictionary(s => s.Key);

        Assert.Equal(6, settings.Count);
        Assert.Equal(("General", "0", "1", Ue4ssSettingKind.Toggle), (settings["EnableHotReloadSystem"].Section, settings["EnableHotReloadSystem"].Value, settings["EnableHotReloadSystem"].Default, settings["EnableHotReloadSystem"].Kind));
        Assert.Equal("Whether to reload all mods when the key defined by HotReloadKey is hit.", settings["EnableHotReloadSystem"].Description);
        Assert.Equal(Ue4ssSettingKind.Toggle, settings["bUseUObjectArrayCache"].Kind);
        Assert.Equal(Ue4ssSettingKind.Choice, settings["DefaultExecuteInGameThreadMethod"].Kind);
        Assert.Equal(["EngineTick", "ProcessEvent"], settings["DefaultExecuteInGameThreadMethod"].Choices);
        // "Anything from ..." is advice, not a list to pick from.
        Assert.Equal(Ue4ssSettingKind.Text, settings["HotReloadKey"].Kind);
        Assert.Contains("Anything from Mods/Keybinds", settings["HotReloadKey"].Description);
        // 1 here is a number, not on/off.
        Assert.Equal(Ue4ssSettingKind.Text, settings["GuiConsoleFontScaling"].Kind);
        Assert.Equal("", settings["ModsFolderPath"].Value);
    }

    [Fact]
    public void SetValue_ChangesOnlyThatLineAndKeepsTheNewlines()
    {
        var document = Ue4ssSettingsDocument.Parse(RealisticIni);

        Assert.True(document.SetValue("General", "EnableHotReloadSystem", "1"));
        Assert.False(document.SetValue("General", "HotReloadKey", "R"));
        Assert.True(document.SetValue("Overrides", "ModsFolderPath", "../Mods"));

        Assert.Equal(
            RealisticIni.Replace("EnableHotReloadSystem = 0", "EnableHotReloadSystem = 1").Replace("ModsFolderPath =\r\n", "ModsFolderPath = ../Mods\r\n"),
            document.ToText());
    }

    [Fact]
    public void SetValue_MissingKeyOrSection_IsAddedWhereItBelongs()
    {
        var document = Ue4ssSettingsDocument.Parse(RealisticIni);

        document.SetValue("Debug", "GuiConsoleVisible", "1");
        document.SetValue("Hooks", "HookLoadMap", "1");

        Assert.Equal("1", document.GetValue("Debug", "GuiConsoleVisible"));
        Assert.Equal("1", document.GetValue("Hooks", "HookLoadMap"));
        Assert.EndsWith("GuiConsoleFontScaling = 1\r\nGuiConsoleVisible = 1\r\n\r\n[Hooks]\r\nHookLoadMap = 1", document.ToText());
    }

    [Fact]
    public void SetValue_SameKeyInAnotherSection_IsNotTouched()
    {
        var document = Ue4ssSettingsDocument.Parse("[A]\nKey = 1\n[B]\nKey = 1\n");

        document.SetValue("B", "Key", "0");

        Assert.Equal("[A]\nKey = 1\n[B]\nKey = 0\n", document.ToText());
    }

    [Fact]
    public void SetValue_RefusesLineBreaks() =>
        Assert.Throws<ArgumentException>(() => Ue4ssSettingsDocument.Parse(RealisticIni).SetValue("General", "HotReloadKey", "R\nEvil = 1"));

    [Theory]
    [InlineData(true, "0", "1")]
    [InlineData(false, "1", "0")]
    [InlineData(true, "false", "true")]
    [InlineData(false, "TRUE", "false")]
    public void ToggleText_KeepsTheFilesStyle(bool on, string existing, string expected) =>
        Assert.Equal(expected, Ue4ssSettingsDocument.ToggleText(on, existing));

    [Fact]
    public void ModConfig_OptionsListedOnePerCommentLine_AreAChoice()
    {
        // MkHaters Stability's real config.ini layout.
        const string ini =
            "; Mode:\n" +
            ";   NoStability  buildings never become unstable\n" +
            ";   Forgiving    more forgiving\n" +
            ";   Off          the game's own stability\n" +
            "Mode = NoStability\n" +
            "\n" +
            "; LogReports    1 = a report every minute\n" +
            "; Debug         1 = one line per failing piece\n" +
            "LogReports = 0\n";

        var settings = Ue4ssSettingsDocument.Parse(ini, keepCommentLines: true).Settings.ToDictionary(s => s.Key);

        Assert.Equal(Ue4ssSettingKind.Choice, settings["Mode"].Kind);
        Assert.Equal(["NoStability", "Forgiving", "Off"], settings["Mode"].Choices);
        Assert.Equal("Mode:\nNoStability  buildings never become unstable\nForgiving    more forgiving\nOff          the game's own stability", settings["Mode"].Description);
        Assert.Equal(Ue4ssSettingKind.Toggle, settings["LogReports"].Kind);
    }

    /// <summary>
    /// MkHaters Stability's config.ini as it is now: a comment after each value. The editor showed
    /// "Forgiving ; NoStability, Forgiving, Observe or Off" as the value, made no choices or switches
    /// of them, and gave ExtraReach the paragraph about ForgivingBoost.
    /// </summary>
    [Fact]
    public void ModConfig_CommentsAfterTheValue_GiveTheValueChoicesRangesAndMarkers()
    {
        const string ini =
            "[Mode]\n" +
            "; NoStability: everything is stable.\n" +
            "Mode = Forgiving ; NoStability, Forgiving, Observe or Off\n" +
            "\n" +
            "[NoStability]\n" +
            "SkipOriginal = 1 ; 1 = skip the game's calculation (faster); 0 = run it, then replace the answer\n" +
            "\n" +
            "[Forgiving]\n" +
            "; Reach: stability spreads ExtraReach more pieces out from the ground.\n" +
            "ForgivingBoost = Reach ; Reach, Strength or Both\n" +
            "ExtraReach = 2 ; extra pieces of reach, for Reach and Both (0-10)\n" +
            "MinimumStability = 0 ; the lowest stability any piece can have; 0 = no floor\n" +
            "WeightMultiplier = 1 ; how much less weight counts; at 1 it is the game's own [slider 1 10 0.5]\n" +
            "ReportEverySeconds = 60 ; how often the report runs, in seconds (at least 10)\n" +
            "HotkeyKey = F3 ; F1-F12 or NumPad0-NumPad9; restart needed [restart]\n";

        var document = Ue4ssSettingsDocument.Parse(ini, keepCommentLines: true);
        var settings = document.Settings.ToDictionary(s => s.Key);

        Assert.Equal(("Forgiving", Ue4ssSettingKind.Choice), (settings["Mode"].Value, settings["Mode"].Kind));
        Assert.Equal(["NoStability", "Forgiving", "Observe", "Off"], settings["Mode"].Choices);
        Assert.Equal("NoStability: everything is stable.", settings["Mode"].Description);
        Assert.Equal(Ue4ssSettingKind.Toggle, settings["SkipOriginal"].Kind);
        Assert.Equal(["Reach", "Strength", "Both"], settings["ForgivingBoost"].Choices);
        Assert.Equal("extra pieces of reach, for Reach and Both", settings["ExtraReach"].Description);
        Assert.Equal("how often the report runs, in seconds", settings["ReportEverySeconds"].Description);
        Assert.Equal((Ue4ssSettingKind.Number, 0d, 10d), (settings["ExtraReach"].Kind, settings["ExtraReach"].Minimum, settings["ExtraReach"].Maximum));
        Assert.Equal(Ue4ssSettingKind.Number, settings["MinimumStability"].Kind);
        Assert.Equal((1d, 10d, 0.5d), (settings["WeightMultiplier"].Minimum, settings["WeightMultiplier"].Maximum, settings["WeightMultiplier"].Step));
        Assert.DoesNotContain("[slider", settings["WeightMultiplier"].Description);
        Assert.Equal((10d, (double?)null), (settings["ReportEverySeconds"].Minimum, settings["ReportEverySeconds"].Maximum));
        Assert.True(settings["HotkeyKey"].NeedsRestart);
        Assert.Equal("F1-F12 or NumPad0-NumPad9", settings["HotkeyKey"].Description);

        // Saving a value keeps the comment after it, spacing and all.
        Assert.Equal("Forgiving", document.GetValue("Mode", "Mode"));
        document.SetValue("Mode", "Mode", "Observe");
        document.SetValue("Forgiving", "ExtraReach", "4");
        Assert.Contains("Mode = Observe ; NoStability, Forgiving, Observe or Off\n", document.ToText());
        Assert.Contains("ExtraReach = 4 ; extra pieces of reach, for Reach and Both (0-10)\n", document.ToText());
        Assert.Throws<ArgumentException>(() => document.SetValue("Mode", "Mode", "A ; B"));
    }

    /// <summary>
    /// A quoted value with " ; " inside it ("Prefix = "[Stability] ; " ; shown before each report")
    /// was cut at the first ';' in the quotes: the value read as "[Stability]" with the rest as its
    /// note, and changing it wrote the new value in front of half the old quoted text.
    /// </summary>
    [Fact]
    public void ModConfig_SemicolonInsideQuotes_IsPartOfTheValue()
    {
        const string ini = "[Report]\nPrefix = \"[Stability] ; \" ; shown before each report\n";
        var document = Ue4ssSettingsDocument.Parse(ini, keepCommentLines: true);

        Assert.Equal("\"[Stability] ; \"", document.GetValue("Report", "Prefix"));
        Assert.Equal("shown before each report", document.Settings.Single().Description);

        document.SetValue("Report", "Prefix", "\"[S] ; \"");
        Assert.Equal("[Report]\nPrefix = \"[S] ; \" ; shown before each report\n", document.ToText());
    }

    /// <summary>"[slider 0 10 0]" gave the slider a step of 0, and moving it wrote "NaN" into the mod's config.</summary>
    [Fact]
    public void ModConfig_SliderWithAZeroStep_StepsByOne()
    {
        var setting = Ue4ssSettingsDocument.Parse("[A]\nX = 2 ; how far [slider 0 10 0]\n", keepCommentLines: true).Settings.Single();
        Assert.Equal(1d, setting.Step);
    }

    /// <summary>UE4SS's own settings file keeps reading a ';' in a value as part of it (it has no comments after values).</summary>
    [Fact]
    public void Ue4ssSettings_KeepASemicolonInAValue() =>
        Assert.Equal("a ; b", Ue4ssSettingsDocument.Parse("[General]\nX = a ; b\n").GetValue("General", "X"));
}
