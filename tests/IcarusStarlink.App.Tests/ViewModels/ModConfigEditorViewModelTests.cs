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
}
