using System.IO;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed class ModConfigEditorViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ModConfigEditor_" + Guid.NewGuid().ToString("N"));

    // Shaped like MkHaters Wildlife's real config.lua.
    private const string Lua =
        "-- MkHaters Wildlife settings.\r\n" +
        "return {\r\n" +
        "    -- Turns the whole mod on or off.\r\n" +
        "    Enabled = true,\r\n" +
        "    DefendRadius = 5000,          -- how far adults come from\r\n" +
        "    Herds = {\r\n" +
        "        { young = \"BearCub\", adults = { \"Bear\" } },\r\n" +
        "    },\r\n" +
        "    Debug = false,\r\n" +
        "}\r\n";

    public ModConfigEditorViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Mod(string where, string file, string text)
    {
        var folder = Path.Combine(_root, where, "MkHatersWildlife");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, file), text);
        return folder;
    }

    private static Ue4ssConfigField Field(ModConfigEditorViewModel vm, string key) =>
        vm.Sections.SelectMany(s => s.Fields).Single(f => f.Key == key);

    [Fact]
    public void Save_ChangesOnlyTheEditedValues_InTheGameAndOnTheServer()
    {
        var game = Mod("game", "config.lua", Lua);
        // The server's copy has its own radius: only what was changed here is written there.
        var server = Mod("server", "config.lua", Lua.Replace("DefendRadius = 5000", "DefendRadius = 9000"));
        var vm = new ModConfigEditorViewModel("MkHatersWildlife",
            [new ModConfigLocation("Your game", game, false), new ModConfigLocation("Server: Test", server, true)], Path.Combine(_root, "Backups"));

        Assert.True(vm.ShowFields);
        Assert.Equal(["Enabled", "DefendRadius", "Debug"], vm.Sections.SelectMany(s => s.Fields).Select(f => f.Key));
        Field(vm, "Debug").IsOn = true;
        vm.AlsoSaveTo.Single().IsChecked = true;
        vm.SaveCommand.Execute(null);

        Assert.Equal(Lua.Replace("Debug = false", "Debug = true"), File.ReadAllText(Path.Combine(game, "config.lua")));
        Assert.Equal(Lua.Replace("Debug = false", "Debug = true").Replace("DefendRadius = 5000", "DefendRadius = 9000"), File.ReadAllText(Path.Combine(server, "config.lua")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "Backups"), "config*.lua", SearchOption.AllDirectories).Length);
        Assert.False(Field(vm, "Debug").IsChanged);
    }

    [Fact]
    public void Save_RefusesABadNumber_AndWritesNothing()
    {
        var game = Mod("game", "config.lua", Lua);
        var vm = new ModConfigEditorViewModel("MkHatersWildlife", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        Field(vm, "DefendRadius").Value = "far";
        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Not saved: DefendRadius", vm.StatusMessage);
        Assert.Equal(Lua, File.ReadAllText(Path.Combine(game, "config.lua")));
    }

    [Fact]
    public void TextMode_CarriesFieldEditsOver_AndSavesTheText()
    {
        var game = Mod("game", "config.lua", Lua);
        var vm = new ModConfigEditorViewModel("MkHatersWildlife", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        Field(vm, "DefendRadius").Value = "7500";
        vm.IsTextMode = true;
        Assert.Contains("DefendRadius = 7500,", vm.RawText);
        vm.RawText = vm.RawText.Replace("\"BearCub\"", "\"BearCub_Juvenile\"");
        vm.SaveCommand.Execute(null);

        var saved = File.ReadAllText(Path.Combine(game, "config.lua"));
        Assert.Contains("DefendRadius = 7500,", saved);
        Assert.Contains("\"BearCub_Juvenile\"", saved);
        // Saving keeps the text view open (it used to flip back to the fields).
        Assert.True(vm.IsTextMode);
    }

    /// <summary>Text edited down to no settings, then "Edit as text" unticked: it stays in the text view (the box used to grey out with the text out of reach).</summary>
    [Fact]
    public void TextMode_TextWithNoSettings_StaysInTheTextView()
    {
        var game = Mod("game", "config.lua", Lua);
        var vm = new ModConfigEditorViewModel("MkHatersWildlife", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        vm.IsTextMode = true;
        vm.RawText = "return {}\r\n";
        vm.IsTextMode = false;

        Assert.True(vm.IsTextMode);
        Assert.True(vm.CanUseFields);
        Assert.Equal("return {}\r\n", vm.RawText);
    }

    [Fact]
    public void Ini_ModeListIsAChoice()
    {
        var game = Mod("game", "config.ini", "; Mode:\r\n;   NoStability  never unstable\r\n;   Off          the game's own\r\nMode = NoStability\r\n");
        var vm = new ModConfigEditorViewModel("MkHatersStability", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        var mode = Field(vm, "Mode");
        Assert.True(mode.IsChoice);
        mode.Value = "Off";
        vm.SaveCommand.Execute(null);

        Assert.Contains("Mode = Off\r\n", File.ReadAllText(Path.Combine(game, "config.ini")));
    }

    /// <summary>
    /// MkHaters Stability's config.ini with a comment after each value: the fields get the bare
    /// value, a drop-down, a switch, a slider and a range, and saving keeps every comment.
    /// </summary>
    [Fact]
    public void IniWithCommentsAfterValues_GivesDropDownsSwitchesAndSliders_AndKeepsTheComments()
    {
        const string ini =
            "[Mode]\r\n" +
            "Mode = Forgiving ; NoStability, Forgiving, Observe or Off\r\n" +
            "[NoStability]\r\n" +
            "SkipOriginal = 1 ; 1 = skip the game's calculation (faster); 0 = run it\r\n" +
            "[Forgiving]\r\n" +
            "ExtraReach = 2 ; extra pieces of reach (0-10)\r\n" +
            "WeightMultiplier = 1 ; how much less weight counts [slider 1 10 0.5]\r\n";
        var game = Mod("game", "config.ini", ini);
        var vm = new ModConfigEditorViewModel("MkHatersStability", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        var mode = Field(vm, "Mode");
        Assert.Equal(("Forgiving", "Forgiving"), (mode.Value, mode.ChoiceValue));
        mode.ChoiceValue = null; // the drop-down clearing itself
        Assert.Equal("Forgiving", mode.Value);
        mode.ChoiceValue = "Observe";
        Assert.True(Field(vm, "SkipOriginal").IsToggle);

        var reach = Field(vm, "ExtraReach");
        Assert.Equal((true, false, "0 to 10"), (reach.IsPlainNumber, reach.HasSlider, reach.RangeText));
        reach.Value = "12";
        Assert.Equal("Outside 0 to 10", reach.ValueProblem);
        reach.Value = "x";
        Assert.Equal("Not a number", reach.ValueProblem);
        reach.Value = "4";
        Assert.False(reach.HasValueProblem);

        var weight = Field(vm, "WeightMultiplier");
        Assert.True(weight.HasSlider);
        weight.NumberValue = 2.4; // the slider snaps to its step
        Assert.Equal("2.5", weight.Value);

        vm.SaveCommand.Execute(null);

        Assert.Equal(
            ini.Replace("Mode = Forgiving ;", "Mode = Observe ;").Replace("ExtraReach = 2 ;", "ExtraReach = 4 ;").Replace("WeightMultiplier = 1 ;", "WeightMultiplier = 2.5 ;"),
            File.ReadAllText(Path.Combine(game, "config.ini")));
    }

    /// <summary>
    /// "1,5" (a comma for decimals) or "ten" in a number field is refused, since the mod can't read
    /// it; a number outside the range in the mod's comment is only warned about and saves.
    /// </summary>
    [Fact]
    public void Save_RefusesANumberTheModCantRead_ButAllowsOneOutsideItsRange()
    {
        const string ini = "[Forgiving]\r\nExtraReach = 2 ; extra pieces of reach (0-10)\r\n";
        var game = Mod("game", "config.ini", ini);
        var vm = new ModConfigEditorViewModel("MkHatersStability", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        Field(vm, "ExtraReach").Value = "1,5";
        vm.SaveCommand.Execute(null);
        Assert.StartsWith("Not saved: ExtraReach isn't a number", vm.StatusMessage);
        Assert.Equal(ini, File.ReadAllText(Path.Combine(game, "config.ini")));

        Field(vm, "ExtraReach").Value = "50";
        vm.SaveCommand.Execute(null);
        Assert.Equal(ini.Replace("= 2 ;", "= 50 ;"), File.ReadAllText(Path.Combine(game, "config.ini")));
    }

    /// <summary>
    /// MkHaters Tools' layout (user 2026-10-27): config.lua opens first; config.default.lua isn't
    /// offered as a file but gives every setting its default, so Reset to default works; a Scripts
    /// module that reads the config isn't a config; the comment tags give a slider, a restart note
    /// and hide a testing value, as MkHaters Mod Settings reads them.
    /// </summary>
    [Fact]
    public void MkHatersLayout_OpensConfigLua_HidesTheDefaultsFile_AndUsesItForReset()
    {
        const string config = "return {\r\n    Enabled = true,               -- the whole mod [restart]\r\n    MaxScale = 2.0,               -- biggest size [slider 1 4 0.5]\r\n    DebugProbe = true,            -- testing [hidden]\r\n}\r\n";
        const string defaults = "return {\r\n    Enabled = true,\r\n    MaxScale = 4.0,\r\n    DebugProbe = false,\r\n}\r\n";
        var game = Mod("game", "config.lua", config);
        File.WriteAllText(Path.Combine(game, "config.default.lua"), defaults);
        Directory.CreateDirectory(Path.Combine(game, "Scripts"));
        File.WriteAllText(Path.Combine(game, "Scripts", "mk_config.lua"), "local M = {}\nlocal function readAll(path) end\nreturn M\n");
        var vm = new ModConfigEditorViewModel("MkHatersTools", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        Assert.Equal(["config.lua"], vm.Files.Select(f => f.RelativePath));
        Assert.Contains("config.default.lua", vm.DefaultsNote);
        Assert.Equal(["Enabled", "MaxScale"], vm.Sections.SelectMany(s => s.Fields).Select(f => f.Key)); // DebugProbe is [hidden]
        var scale = Field(vm, "MaxScale");
        Assert.True(scale.HasSlider);
        Assert.Equal("biggest size", scale.Description);
        Assert.Equal("Default: 4.0", scale.DefaultText);
        Assert.True(scale.CanResetToDefault);
        Assert.True(Field(vm, "Enabled").NeedsRestart);
        Assert.False(Field(vm, "Enabled").CanResetToDefault);

        scale.ResetToDefaultCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        Assert.Equal(config.Replace("MaxScale = 2.0", "MaxScale = 4.0"), File.ReadAllText(Path.Combine(game, "config.lua")));
        Assert.Equal(defaults, File.ReadAllText(Path.Combine(game, "config.default.lua")));
    }

    /// <summary>
    /// After an update (user request 2026-10-27): the settings the update added are marked, the
    /// ones the mod no longer has are listed with the old values, and Done reviewing clears it all.
    /// </summary>
    [Fact]
    public void AfterAnUpdate_NewSettingsAreMarked_GoneOnesListed_DoneReviewingClearsThem()
    {
        var game = Mod("game", "config.lua", Lua);
        var reviews = new List<IcarusStarlink.Core.Ue4ss.Ue4ssConfigReview>
        {
            new() { File = "config.lua", Version = "1.4.0", UpdatedAtUtc = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc), Added = ["Debug"], Removed = ["OldRadius = 5"], Kept = ["DefendRadius"], MergedIntoNewLayout = true },
        };
        var reviewed = false;
        var vm = new ModConfigEditorViewModel("MkHatersWildlife", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"), reviews, () => reviewed = true);

        Assert.True(vm.HasReview);
        Assert.Contains("Updated to 1.4.0", vm.ReviewText);
        Assert.Contains("OldRadius = 5", vm.ReviewText);
        Assert.True(Field(vm, "Debug").IsNewInVersion);
        Assert.False(Field(vm, "Enabled").IsNewInVersion);

        vm.DoneReviewingCommand.Execute(null);

        Assert.True(reviewed);
        Assert.False(vm.HasReview);
        Assert.Null(vm.ReviewText);
        Assert.False(Field(vm, "Debug").IsNewInVersion);
    }

    /// <summary>Done reviewing rebuilt the fields from the file and threw away the edits made so far.</summary>
    [Fact]
    public void DoneReviewing_KeepsTheEditsMadeSoFar()
    {
        var game = Mod("game", "config.lua", Lua);
        var reviews = new List<IcarusStarlink.Core.Ue4ss.Ue4ssConfigReview> { new() { File = "config.lua", Added = ["Debug"], MergedIntoNewLayout = true } };
        var vm = new ModConfigEditorViewModel("MkHatersWildlife", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"), reviews, () => { });

        Field(vm, "DefendRadius").Value = "7000";
        vm.DoneReviewingCommand.Execute(null);

        Assert.Equal("7000", Field(vm, "DefendRadius").Value);
        Assert.True(vm.HasUnsavedChanges);
    }

    /// <summary>
    /// The known open issue (user, 2026-10-28): picking another file in the File drop-down (or
    /// another copy in "Edit the copy in") dropped the unsaved edits without a word. They're kept
    /// now, listed as not saved, and back when the file is picked again.
    /// </summary>
    [Fact]
    public void SwitchingFiles_KeepsTheUnsavedEdits()
    {
        var game = Mod("game", "config.lua", Lua);
        Directory.CreateDirectory(Path.Combine(game, "config"));
        const string ini = "[General]\r\nRadius = 10 ; how far\r\n";
        File.WriteAllText(Path.Combine(game, "config", "Extra.ini"), ini);
        var vm = new ModConfigEditorViewModel("MkHatersWildlife", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));
        var lua = vm.Files.Single(f => f.RelativePath == "config.lua");
        var extra = vm.Files.Single(f => f.RelativePath != "config.lua");

        Assert.False(vm.HasUnsavedChanges);
        Field(vm, "DefendRadius").Value = "7500";
        Assert.Equal("Not saved yet.", vm.UnsavedText);

        vm.SelectedFile = extra;
        Assert.Equal("10", Field(vm, "Radius").Value);
        Assert.True(vm.HasUnsavedChanges);
        Assert.False(vm.HasChangesHere);
        Assert.Contains("config.lua", vm.UnsavedText);

        vm.SelectedFile = lua;
        Assert.Equal("7500", Field(vm, "DefendRadius").Value);
        Assert.True(Field(vm, "DefendRadius").IsChanged);
        vm.SaveCommand.Execute(null);

        Assert.Equal(Lua.Replace("DefendRadius = 5000", "DefendRadius = 7500"), File.ReadAllText(Path.Combine(game, "config.lua")));
        Assert.False(vm.HasUnsavedChanges);
    }

    /// <summary>The same for the text view: text typed there survives a switch to another copy and back.</summary>
    [Fact]
    public void SwitchingCopies_KeepsTextViewEdits()
    {
        var game = Mod("game", "config.lua", Lua);
        var server = Mod("server", "config.lua", Lua);
        var vm = new ModConfigEditorViewModel("MkHatersWildlife",
            [new ModConfigLocation("Your game", game, false), new ModConfigLocation("Server: Test", server, true)], Path.Combine(_root, "Backups"));

        vm.IsTextMode = true;
        vm.RawText = vm.RawText.Replace("\"BearCub\"", "\"Wolf\"");
        vm.SelectedLocation = vm.Locations[1];
        Assert.Contains("\"BearCub\"", vm.RawText);
        Assert.Contains("config.lua (Your game)", vm.UnsavedText);

        vm.SelectedLocation = vm.Locations[0];
        Assert.True(vm.IsTextMode);
        Assert.Contains("\"Wolf\"", vm.RawText);

        // Discard throws away only this copy's edits.
        vm.DiscardChangesCommand.Execute(null);
        Assert.False(vm.HasUnsavedChanges);
        Assert.Equal(Lua, File.ReadAllText(Path.Combine(game, "config.lua")));
    }

    /// <summary>Closing with unsaved edits asks; Save writes every file with edits, Cancel keeps the window, Don't save writes nothing.</summary>
    [Fact]
    public void ConfirmClose_AsksAndSavesEveryEditedFile()
    {
        var game = Mod("game", "config.lua", Lua);
        var server = Mod("server", "config.lua", Lua);
        var vm = new ModConfigEditorViewModel("MkHatersWildlife",
            [new ModConfigLocation("Your game", game, false), new ModConfigLocation("Server: Test", server, true)], Path.Combine(_root, "Backups"));
        Assert.True(vm.ConfirmClose()); // nothing to ask about

        Field(vm, "Debug").IsOn = true;
        vm.SelectedLocation = vm.Locations[1];
        Field(vm, "DefendRadius").Value = "9000";

        string? asked = null;
        vm.AskSaveOnClose = message => { asked = message; return null; };
        Assert.False(vm.ConfirmClose());
        Assert.Contains("aren't saved", asked);
        Assert.Equal(Lua, File.ReadAllText(Path.Combine(game, "config.lua")));

        vm.AskSaveOnClose = _ => true;
        Assert.True(vm.ConfirmClose());
        Assert.Equal(Lua.Replace("Debug = false", "Debug = true"), File.ReadAllText(Path.Combine(game, "config.lua")));
        Assert.Equal(Lua.Replace("DefendRadius = 5000", "DefendRadius = 9000"), File.ReadAllText(Path.Combine(server, "config.lua")));
        Assert.False(vm.HasUnsavedChanges);
    }

    [Fact]
    public void ConfirmClose_DontSave_WritesNothing()
    {
        var game = Mod("game", "config.lua", Lua);
        var vm = new ModConfigEditorViewModel("MkHatersWildlife", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));
        Field(vm, "Debug").IsOn = true;

        vm.AskSaveOnClose = _ => false;
        Assert.True(vm.ConfirmClose());
        Assert.Equal(Lua, File.ReadAllText(Path.Combine(game, "config.lua")));
    }

    /// <summary>
    /// MkHaters Cheats' real config.lua (2026-10-28): the "-- ==== GENERAL ====" headings group
    /// the settings, names read as words with the key beside them, OpenKey = { key = "F2" } is a
    /// key with Ctrl/Shift/Alt (it was a Lua text box), and [server] is a mark, not text.
    /// </summary>
    [Fact]
    public void MkHatersCheats_GroupsByHeading_KeyIsAKey_ServerIsAMark()
    {
        const string cheats =
            "-- MkHaters Cheats settings. [MkHatersSettings] Cheats\r\n" +
            "return {\r\n" +
            "    -- ==== GENERAL ====\r\n" +
            "    -- Turns the whole mod on or off.\r\n" +
            "    Enabled = true,               -- [restart]\r\n" +
            "    -- Opens and closes the MKHATERS CHEATS window.\r\n" +
            "    OpenKey = { key = \"F2\" },\r\n" +
            "\r\n" +
            "    -- ==== GOD MODE ====\r\n" +
            "    -- Whether players may switch god mode on.\r\n" +
            "    AllowGodMode = true,          -- [server]\r\n" +
            "}\r\n";
        var game = Mod("game", "config.lua", cheats);
        var vm = new ModConfigEditorViewModel("MkHatersCheats", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        Assert.Equal(["General", "God mode"], vm.Sections.Select(s => s.Name));
        Assert.Equal("Turns the whole mod on or off.", Field(vm, "Enabled").Description);
        var god = Field(vm, "AllowGodMode");
        Assert.Equal(("Allow god mode", true, true, "Whether players may switch god mode on."), (god.Label, god.ShowKeyName, god.ServerDecides, god.Description));
        Assert.False(Field(vm, "Enabled").ServerDecides);

        var key = Field(vm, "OpenKey");
        Assert.True(key.IsKey);
        Assert.True(key.KeyAllowsModifiers);
        Assert.Equal(("F2", "F2", false), (key.KeyName, key.KeyText, key.KeyCtrl));
        key.KeyName = "F 3"; // not a key name: refused, the value stays the last good one
        Assert.True(key.HasValueProblem);
        Assert.Equal("{ key = \"F2\" }", key.Value);
        vm.SaveCommand.Execute(null);
        Assert.StartsWith("Not saved: OpenKey", vm.StatusMessage);

        key.KeyName = "F3";
        key.KeyCtrl = true;
        Assert.Equal("Ctrl+F3", key.KeyText);
        Assert.False(key.HasValueProblem);
        vm.SaveCommand.Execute(null);

        Assert.Equal(cheats.Replace("OpenKey = { key = \"F2\" },", "OpenKey = { key = \"F3\", ctrl = true },"), File.ReadAllText(Path.Combine(game, "config.lua")));
    }

    /// <summary>A downloaded mod's "config.lua" of many MB isn't read into the editor (no limit before 2026-10-28).</summary>
    [Fact]
    public void AHugeConfigFile_IsNotOpened()
    {
        var game = Mod("game", "config.lua", "return {\n" + new string('-', (int)ModConfigEditorViewModel.MaxConfigBytes) + "\n}\n");
        var vm = new ModConfigEditorViewModel("Huge", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        Assert.Contains("too big for a config", vm.StatusMessage);
        Assert.False(vm.ShowFields);
        Assert.False(vm.IsTextMode);
        Assert.Equal("", vm.RawText);
    }

    /// <summary>Search finds a setting by its plain name or its heading too, and says so when nothing matches.</summary>
    [Fact]
    public void Search_MatchesLabelAndHeading()
    {
        var game = Mod("game", "config.lua", "return {\r\n    -- ==== GUESTS ====\r\n    GuestWeightPercent = 10,\r\n    Debug = false,\r\n}\r\n");
        var vm = new ModConfigEditorViewModel("MkHatersWildlife", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        vm.Filter = "guest weight";
        Assert.Equal(["GuestWeightPercent"], vm.Sections.SelectMany(s => s.Fields).Select(f => f.Key));
        vm.Filter = "guests";
        Assert.Equal(2, vm.Sections.SelectMany(s => s.Fields).Count());
        vm.Filter = "nothing like this";
        Assert.True(vm.ShowNoMatches);
    }

    /// <summary>MkHaters Stability's "; [MkHatersSettings] Stability [server]" marks every setting the server's, but those tagged [local].</summary>
    [Fact]
    public void FileWideServerTag_MarksEverySettingButTheLocalOnes()
    {
        const string ini = "; [MkHatersSettings] Stability [server]\r\n\r\n[Mode]\r\nMode = Off ; Fair or Off\r\n[Hotkey]\r\nHotkey = 1 ; 1 = the hotkey works [local]\r\n";
        var game = Mod("game", "config.ini", ini);
        var vm = new ModConfigEditorViewModel("MkHatersStability", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        Assert.True(Field(vm, "Mode").ServerDecides);
        Assert.False(Field(vm, "Hotkey").ServerDecides);
    }

    [Fact]
    public void ResetAllToDefault_PutsEveryShownSettingBack_HiddenOnesStay()
    {
        const string config = "return {\r\n    Speed = 2,\r\n    Debug = true,\r\n    Probe = true, -- [hidden]\r\n}\r\n";
        const string defaults = "return {\r\n    Speed = 1,\r\n    Debug = false,\r\n    Probe = false,\r\n}\r\n";
        var game = Mod("game", "config.lua", config);
        File.WriteAllText(Path.Combine(game, "config.default.lua"), defaults);
        var vm = new ModConfigEditorViewModel("MkHatersTools", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        Assert.True(vm.CanResetAllToDefault);
        vm.ResetAllToDefaultCommand.Execute(null);
        Assert.False(vm.CanResetAllToDefault);
        Assert.Contains("2 settings", vm.StatusMessage);
        vm.SaveCommand.Execute(null);

        Assert.Equal("return {\r\n    Speed = 1,\r\n    Debug = false,\r\n    Probe = true, -- [hidden]\r\n}\r\n", File.ReadAllText(Path.Combine(game, "config.lua")));
    }

    /// <summary>
    /// The window stays open while the player changes a setting in the mod's own in-game menu
    /// (MkHaters Mod Settings writes config.lua). Saving here used to write the file as it was read
    /// at opening, putting that setting back without a word.
    /// </summary>
    [Fact]
    public void Save_AfterTheFileChangedOnDisk_WritesOnlyTheEditsHere_IntoTheNewFile()
    {
        var game = Mod("game", "config.lua", Lua);
        var vm = new ModConfigEditorViewModel("MkHatersWildlife", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));

        File.WriteAllText(Path.Combine(game, "config.lua"), Lua.Replace("DefendRadius = 5000", "DefendRadius = 8000"));
        Field(vm, "Debug").IsOn = true;
        vm.SaveCommand.Execute(null);

        Assert.Equal(Lua.Replace("DefendRadius = 5000", "DefendRadius = 8000").Replace("Debug = false", "Debug = true"), File.ReadAllText(Path.Combine(game, "config.lua")));
        Assert.Contains("changed outside this window", vm.StatusMessage);
        Assert.Equal("8000", Field(vm, "DefendRadius").Value);
    }

    /// <summary>The text view has no single settings to carry: a file changed on disk meanwhile isn't overwritten, and the text stays to copy.</summary>
    [Fact]
    public void SaveText_AfterTheFileChangedOnDisk_IsRefused_AndTheTextStays()
    {
        var game = Mod("game", "config.lua", Lua);
        var vm = new ModConfigEditorViewModel("MkHatersWildlife", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));
        vm.IsTextMode = true;

        var changed = Lua.Replace("DefendRadius = 5000", "DefendRadius = 8000");
        File.WriteAllText(Path.Combine(game, "config.lua"), changed);
        vm.RawText = Lua.Replace("Debug = false", "Debug = true");
        vm.SaveCommand.Execute(null);

        Assert.Equal(changed, File.ReadAllText(Path.Combine(game, "config.lua")));
        Assert.StartsWith("Not saved: the file was changed outside this window", vm.StatusMessage);
        Assert.Contains("Debug = true", vm.RawText);
    }

    /// <summary>
    /// Final review 2026-10-28: edits kept aside while another file was shown came back on the OLD
    /// file's text. When the file changed on disk meanwhile (the mod's settings menu), Save took
    /// every outside change for an edit made here and wrote the old file back over it.
    /// </summary>
    [Fact]
    public void SwitchingFilesAndBack_AfterTheFileChangedOnDisk_SaveKeepsThatChange()
    {
        var game = Mod("game", "config.lua", Lua);
        Directory.CreateDirectory(Path.Combine(game, "config"));
        File.WriteAllText(Path.Combine(game, "config", "Extra.ini"), "[General]\r\nRadius = 10\r\n");
        var vm = new ModConfigEditorViewModel("MkHatersWildlife", [new ModConfigLocation("Your game", game, false)], Path.Combine(_root, "Backups"));
        var lua = vm.Files.Single(f => f.RelativePath == "config.lua");
        var extra = vm.Files.Single(f => f.RelativePath != "config.lua");

        Field(vm, "Debug").IsOn = true;
        vm.SelectedFile = extra;
        var changed = Lua.Replace("DefendRadius = 5000", "DefendRadius = 8000");
        File.WriteAllText(Path.Combine(game, "config.lua"), changed);
        vm.SelectedFile = lua;
        vm.SaveCommand.Execute(null);

        Assert.Equal(changed.Replace("Debug = false", "Debug = true"), File.ReadAllText(Path.Combine(game, "config.lua")));
    }

    /// <summary>The same in the text view: there is nothing to merge, so it refuses instead of writing the old text back.</summary>
    [Fact]
    public void SwitchingCopiesAndBack_InTheTextView_AfterTheFileChangedOnDisk_IsRefused()
    {
        var game = Mod("game", "config.lua", Lua);
        var server = Mod("server", "config.lua", Lua);
        var vm = new ModConfigEditorViewModel("MkHatersWildlife",
            [new ModConfigLocation("Your game", game, false), new ModConfigLocation("Server: Test", server, true)], Path.Combine(_root, "Backups"));

        vm.IsTextMode = true;
        vm.RawText = vm.RawText.Replace("Debug = false", "Debug = true");
        vm.SelectedLocation = vm.Locations[1];
        var changed = Lua.Replace("DefendRadius = 5000", "DefendRadius = 8000");
        File.WriteAllText(Path.Combine(game, "config.lua"), changed);
        vm.SelectedLocation = vm.Locations[0];
        vm.SaveCommand.Execute(null);

        Assert.Equal(changed, File.ReadAllText(Path.Combine(game, "config.lua")));
        Assert.StartsWith("Not saved: the file was changed outside this window", vm.StatusMessage);
    }
}
