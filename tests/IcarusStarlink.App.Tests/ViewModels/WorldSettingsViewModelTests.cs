using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.PakIO.Worlds;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed class WorldSettingsViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WorldSettingsVmTests_" + Guid.NewGuid().ToString("N"));
    private readonly ActivityLog _log = new();
    private string? _blocked;

    public WorldSettingsViewModelTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Prospects"));
        Directory.CreateDirectory(Path.Combine(_root, "Data", "Stats"));
        File.WriteAllText(Path.Combine(_root, "Data", "Stats", "D_CustomGameStats.json"), """
            {
              "Defaults": { "StatType": "Bool", "StatChangeability": "Anytime", "DefaultValue": 0 },
              "Rows": [
                { "Name": "Arachnophobia", "DisplayName": "Arachnophobia Mode" },
                { "Name": "GreatHuntEffects", "DefaultValue": 1 },
                { "Name": "StartingLevel", "StatType": "Int", "MinIntValue": 1, "MaxIntValue": 50, "DefaultValue": 1, "StatChangeability": "OnProspectCreation" }
              ]
            }
            """);
        File.WriteAllText(Path.Combine(_root, "Prospects", "World.json"),
            "{\r\n\t\"ProspectInfo\":\r\n\t{\r\n\t\t\"ProspectID\": \"World\",\r\n\t\t\"Difficulty\": \"Hard\",\r\n\t\t\"CustomSettings\": [\r\n\t\t\t{\r\n\t\t\t\t\"SettingRowName\": \"Arachnophobia\",\r\n\t\t\t\t\"SettingValue\": 0\r\n\t\t\t}\r\n\t\t]\r\n\t},\r\n\t\"ProspectBlob\": {}\r\n}");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private WorldSettingsViewModel Create(string? prospects = "use", IDialogService? dialogs = null)
    {
        var vm = new WorldSettingsViewModel(
            () => prospects is null ? null : Path.Combine(_root, "Prospects"),
            () => Path.Combine(_root, "Data"),
            () => _blocked,
            Path.Combine(_root, "Backups"),
            _log,
            dialogs);
        vm.Reload();
        return vm;
    }

    [Fact]
    public void Reload_ListsWorlds_AndRowsWithDefaultsForSettingsTheSaveLacks()
    {
        var vm = Create();

        Assert.Equal("World", Assert.Single(vm.Worlds).Name);
        Assert.Equal(3, vm.Rows.Count);
        var hunt = vm.Rows.Single(r => r.Definition.Name == "GreatHuntEffects");
        Assert.True(hunt.BoolValue);
        Assert.False(hunt.IsListedInSave);
        Assert.False(vm.Rows.Single(r => r.Definition.Name == "StartingLevel").IsEditable);
        Assert.False(vm.HasChanges);
    }

    [Fact]
    public void NoSlot_ShowsNothing()
    {
        var vm = Create(prospects: null);

        Assert.True(vm.HasNoWorlds);
        Assert.Empty(vm.Rows);
        Assert.Contains("Pick a save slot", vm.EmptyText);
    }

    [Fact]
    public void Save_WritesTheChange_AndUndoGoesBack()
    {
        var vm = Create();
        var arachno = vm.Rows.Single(r => r.Definition.Name == "Arachnophobia");
        arachno.BoolValue = true;
        Assert.True(vm.HasChanges);

        vm.SaveCommand.Execute(null);

        Assert.False(vm.HasChanges);
        Assert.Equal(1, WorldSave.Load(Path.Combine(_root, "Prospects", "World.json"))!.Settings["Arachnophobia"]);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "Backups")));
        var logged = Assert.Single(_log.Entries);
        Assert.Equal(ActivityEntryKind.Success, logged.Kind);
        Assert.Contains("Saved 1 setting(s) for 'World'", logged.Message);

        vm.Rows.Single(r => r.Definition.Name == "Arachnophobia").BoolValue = false;
        vm.RevertCommand.Execute(null);
        Assert.True(vm.Rows.Single(r => r.Definition.Name == "Arachnophobia").BoolValue);
    }

    [Fact]
    public void Save_WhileTheGameRuns_IsRefused()
    {
        var vm = Create();
        vm.Rows.Single(r => r.Definition.Name == "Arachnophobia").BoolValue = true;
        _blocked = "Close Icarus first.";

        vm.SaveCommand.Execute(null);

        Assert.Equal("Close Icarus first.", vm.StatusMessage);
        Assert.Equal(0, WorldSave.Load(Path.Combine(_root, "Prospects", "World.json"))!.Settings["Arachnophobia"]);
        Assert.True(vm.HasChanges);
        Assert.False(Directory.Exists(Path.Combine(_root, "Backups")));
        var logged = Assert.Single(_log.Entries);
        Assert.Equal(ActivityEntryKind.Warning, logged.Kind);
        Assert.Contains("Close Icarus first.", logged.Message);
    }

    [Fact]
    public void Save_ThatFails_IsLoggedAsAWarning()
    {
        var vm = Create();
        vm.Rows.Single(r => r.Definition.Name == "Arachnophobia").BoolValue = true;
        // A file where the backup folder should be: the backup (taken before any write) fails.
        File.WriteAllText(Path.Combine(_root, "Backups"), "");

        vm.SaveCommand.Execute(null);

        Assert.StartsWith("Couldn't save the world", vm.StatusMessage);
        var logged = Assert.Single(_log.Entries);
        Assert.Equal(ActivityEntryKind.Warning, logged.Kind);
        Assert.Contains("'World'", logged.Message);
    }

    [Fact]
    public void NumberSetting_IsKeptInTheGamesRange()
    {
        var row = new WorldSettingRowViewModel(
            new WorldSettingDefinition("StartingLevel", "Starting level", "", "Misc", "Int", 1, 1, 50, true, []), null);

        row.ValueText = "99";
        Assert.Equal(50, row.Value);
        row.ValueText = "nonsense";
        Assert.Equal(50, row.Value);
    }

    /// <summary>Refresh game data used to rebuild the rows (InvalidateDefinitions) and drop the unsaved settings.</summary>
    [Fact]
    public void InvalidateDefinitions_KeepsUnsavedSettings()
    {
        var vm = Create();
        vm.Rows.Single(r => r.Definition.Name == "Arachnophobia").BoolValue = true;

        vm.InvalidateDefinitions();

        Assert.True(vm.HasChanges);
        Assert.True(vm.Rows.Single(r => r.Definition.Name == "Arachnophobia").BoolValue);
    }

    /// <summary>Clicking another world used to drop the unsaved settings without a word.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PickingAnotherWorld_WithUnsavedSettings_AsksFirst(bool discard)
    {
        File.Copy(Path.Combine(_root, "Prospects", "World.json"), Path.Combine(_root, "Prospects", "World2.json"));
        var dialogs = new FakeDialogs(discard);
        var vm = Create(dialogs: dialogs);
        var first = vm.SelectedWorld!;
        vm.Rows.Single(r => r.Definition.Name == "Arachnophobia").BoolValue = true;

        vm.SelectedWorld = vm.Worlds.Single(w => w != first);

        Assert.Equal(1, dialogs.Asked);
        Assert.Equal(discard, !ReferenceEquals(first, vm.SelectedWorld));
        Assert.Equal(!discard, vm.HasChanges);
    }

    private sealed class FakeDialogs(bool answer) : IDialogService
    {
        public int Asked { get; private set; }

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            Asked++;
            return answer;
        }

        public RenamePromptResult PromptRename(
            string currentName, string description = "", string? resetValue = null, string resetLabel = "",
            string resetTooltip = "", string title = "", string fieldLabel = "") => new(true, null);
    }
}
