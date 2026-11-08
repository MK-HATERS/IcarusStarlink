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

    private WorldSettingsViewModel Create(string? prospects = "use", IDialogService? dialogs = null, string? owner = null, int keep = 10)
    {
        var vm = new WorldSettingsViewModel(
            () => prospects is null ? null : Path.Combine(_root, "Prospects"),
            () => Path.Combine(_root, "Data"),
            () => _blocked,
            Path.Combine(_root, "Backups"),
            _log,
            dialogs,
            owner is null ? null : () => owner,
            () => keep);
        vm.Reload();
        return vm;
    }

    private const string Slot = "76561198000000001";

    private string WorldPath => Path.Combine(_root, "Prospects", "World.json");

    private void SaveArachnophobia(WorldSettingsViewModel vm, bool on)
    {
        vm.Rows.Single(r => r.Definition.Name == "Arachnophobia").BoolValue = on;
        vm.SaveCommand.Execute(null);
    }

    [Fact]
    public async Task Backups_TheSelectedWorldsBackupsAreListed_AndRestorePutsTheChosenOneBack()
    {
        var vm = Create(owner: Slot);
        Assert.True(vm.ShowsBackups);
        Assert.True(vm.HasNoBackups);

        SaveArachnophobia(vm, true);
        var backup = Assert.Single(vm.Backups);
        Assert.Equal("before world settings save", backup.What);
        Assert.StartsWith(Slot + "_", backup.FileName);
        Assert.False(vm.RestoreBackupCommand.CanExecute(null));

        vm.SelectedBackup = backup;
        Assert.True(vm.RestoreBackupCommand.CanExecute(null));
        var started = DateTime.Now.AddSeconds(-2);
        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal(0, WorldSave.Load(WorldPath)!.Settings["Arachnophobia"]);
        Assert.False(vm.Rows.Single(r => r.Definition.Name == "Arachnophobia").BoolValue);
        Assert.True(File.GetLastWriteTime(WorldPath) >= started);
        // The world as it was just before is backed up first, and listed.
        Assert.Equal(2, vm.Backups.Count);
        Assert.Contains(vm.Backups, b => b.What == "before a restore");
        Assert.StartsWith("Restored 'World'", vm.StatusMessage);
        Assert.Contains(_log.Entries, e => e.Kind == ActivityEntryKind.Success && e.Message.StartsWith("Restored 'World'"));
    }

    /// <summary>
    /// Regression: Restore copies the world in the background, and Save world settings stayed on
    /// meanwhile, so a click on it wrote the same world file while the restore was replacing it.
    /// </summary>
    [Fact]
    public async Task Save_IsOff_WhileARestoreRuns()
    {
        var vm = Create(owner: Slot);
        SaveArachnophobia(vm, true);
        vm.SelectedBackup = vm.Backups.Single();
        vm.Rows.Single(r => r.Definition.Name == "GreatHuntEffects").BoolValue = false;
        Assert.True(vm.SaveCommand.CanExecute(null));
        var saveTold = 0;
        vm.SaveCommand.CanExecuteChanged += (_, _) => saveTold++;
        using var gate = new ManualResetEventSlim();
        vm.WhileRestoring = gate.Wait;

        Task? restore = null;
        try
        {
            // Held on the background thread before the backup is copied back.
            restore = vm.RestoreBackupCommand.ExecuteAsync(null);

            Assert.False(restore.IsCompleted);
            Assert.False(vm.SaveCommand.CanExecute(null));
            Assert.True(saveTold > 0);
            vm.SaveCommand.Execute(null);
            Assert.Single(_log.Entries, e => e.Message.StartsWith("Saved", StringComparison.Ordinal));
        }
        finally
        {
            gate.Set();
            if (restore is not null)
            {
                await restore;
            }
        }

        Assert.StartsWith("Restored 'World'", vm.StatusMessage);
        Assert.True(vm.SaveCommand.CanExecute(null));
    }

    [Fact]
    public async Task Restore_WhileTheGameRuns_IsRefused_AndNothingIsWritten()
    {
        var vm = Create(owner: Slot);
        SaveArachnophobia(vm, true);
        vm.SelectedBackup = vm.Backups.Single();
        var worldBefore = File.ReadAllText(WorldPath);
        _blocked = "Close Icarus first.";

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal("Close Icarus first.", vm.StatusMessage);
        Assert.Equal(worldBefore, File.ReadAllText(WorldPath));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "Backups")));
        Assert.Contains(_log.Entries, e => e.Kind == ActivityEntryKind.Warning && e.Message.Contains("not restored"));
    }

    [Fact]
    public async Task Restore_AsksFirst_AndNoChangesNothing()
    {
        var dialogs = new FakeDialogs(false);
        var vm = Create(dialogs: dialogs, owner: Slot);
        SaveArachnophobia(vm, true);
        vm.SelectedBackup = vm.Backups.Single();

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.Asked);
        Assert.Equal(1, WorldSave.Load(WorldPath)!.Settings["Arachnophobia"]);
        Assert.Single(vm.Backups);
    }

    [Fact]
    public void Save_PrunesTheWorldsBackups_ToTheSetting()
    {
        var vm = Create(owner: Slot, keep: 3);
        for (var i = 0; i < 6; i++)
        {
            SaveArachnophobia(vm, i % 2 == 0);
        }

        Assert.Equal(3, vm.Backups.Count);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(_root, "Backups")).Length);
    }

    /// <summary>Another slot's (or server's) backups of a world with the same name never show.</summary>
    [Fact]
    public void Backups_OfAnotherOwner_AreNotListed()
    {
        SaveArachnophobia(Create(owner: "76561198000000002"), true);

        var vm = Create(owner: Slot);

        Assert.Empty(vm.Backups);
    }

    [Fact]
    public void NoOwner_NoBackupsList()
    {
        var vm = Create();

        Assert.False(vm.ShowsBackups);
        Assert.Empty(vm.Backups);
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

    /// <summary>Letters, or 99 when the value is already 50 (its top): nothing changed, so the box kept the typed text while 50 was what Save wrote.</summary>
    [Fact]
    public void NumberSetting_TextItCantUse_TellsTheBoxToShowTheRealValueAgain()
    {
        var row = new WorldSettingRowViewModel(
            new WorldSettingDefinition("StartingLevel", "Starting level", "", "Misc", "Int", 1, 1, 50, true, []), 50);
        var told = 0;
        row.PropertyChanged += (_, e) => told += e.PropertyName == nameof(WorldSettingRowViewModel.ValueText) ? 1 : 0;

        row.ValueText = "nonsense";
        row.ValueText = "99";
        row.EndTyping();

        Assert.Equal(1, told);
        Assert.Equal("50", row.ValueText);
    }

    /// <summary>
    /// Regression: a typed number reached the settings only when the box lost focus, so Save world
    /// settings (on only with a change) stayed greyed out and a click on it did nothing. The box
    /// keeps what's typed (a first digit isn't pushed up to the minimum mid-typing) and shows the
    /// in-range value once typing ends.
    /// </summary>
    [Fact]
    public void NumberSetting_TypingCountsAtOnce_ButTheBoxIsTidiedOnlyWhenTypingEnds()
    {
        var row = new WorldSettingRowViewModel(
            new WorldSettingDefinition("Count", "Count", "", "Misc", "Int", 10, 10, 50, true, []), 12);

        row.ValueText = "1";
        Assert.Equal(10, row.Value);
        Assert.True(row.IsChanged);
        Assert.Equal("1", row.ValueText);

        row.ValueText = "15";
        Assert.Equal(15, row.Value);

        row.EndTyping();
        Assert.Equal("15", row.ValueText);
    }

    /// <summary>
    /// Regression: during a slot switch the tab still showed the old slot's world while the backup
    /// name already came from the new slot, so Save backed it up (and pruned) under the other slot.
    /// </summary>
    [Fact]
    public void SaveDuringASlotSwitch_BacksUpUnderTheSlotTheWorldWasLoadedFor()
    {
        var owner = "76561198000000001";
        var vm = new WorldSettingsViewModel(
            () => Path.Combine(_root, "Prospects"),
            () => Path.Combine(_root, "Data"),
            () => _blocked,
            Path.Combine(_root, "Backups"),
            _log,
            null,
            () => owner,
            () => 10);
        vm.Reload();
        vm.Rows.Single(r => r.Definition.Name == "Arachnophobia").BoolValue = true;

        // The slot changed; the list hasn't been read again yet.
        owner = "76561198000000002";
        vm.SaveCommand.Execute(null);

        var backup = Assert.Single(Directory.GetFiles(Path.Combine(_root, "Backups")));
        Assert.StartsWith("76561198000000001_", Path.GetFileName(backup));
        Assert.StartsWith("76561198000000001_", Assert.Single(vm.Backups).FileName);
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

    /// <summary>A mission world is saved under a 32-character id: it's titled by its prospect, with the id kept faint.</summary>
    [Fact]
    public void AWorldSavedUnderAnId_IsTitledByItsProspect()
    {
        File.WriteAllText(Path.Combine(_root, "Prospects", "B6E8A7604E8C4D29041549B1AE40E6DE.json"),
            "{\"ProspectInfo\": {\"ProspectID\": \"B6E8A7604E8C4D29041549B1AE40E6DE\", \"ProspectDTKey\": \"ELY_Story_1\", \"Difficulty\": \"Medium\", \"CustomSettings\": []}, \"ProspectBlob\": {}}");
        var vm = Create();

        var mission = vm.Worlds.Single(w => w.Name == "B6E8A7604E8C4D29041549B1AE40E6DE");
        Assert.Equal("ELY Story 1", mission.Title);
        Assert.Equal("B6E8A7604E8C4D29041549B1AE40E6DE", mission.RawId);

        var named = vm.Worlds.Single(w => w.Name == "World");
        Assert.Equal("World", named.Title);
        Assert.Null(named.RawId);
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
