using System.IO;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Tests.ViewModels;

public class Ue4ssConfigViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _contentPath;
    private readonly string _serverFolder;

    private const string GameIni = "[General]\n; Whether to reload all mods.\n; Default: 1\nEnableHotReloadSystem = 0\n\n; Whether to automatically reload mods.\n; Default: 0\nEnableAutoReloadingLuaMods = 1\n";

    // The server has its own choices: hot reload on, auto reload off.
    private const string ServerIni = "[General]\nEnableHotReloadSystem = 1\nEnableAutoReloadingLuaMods = 0\n";

    public Ue4ssConfigViewModelTests()
    {
        _contentPath = Path.Combine(_root, "Game", "Icarus", "Content");
        Directory.CreateDirectory(_contentPath);
        var gameUe4ss = Ue4ssGamePaths.ResolveLoaderFolder(_contentPath);
        Directory.CreateDirectory(gameUe4ss);
        File.WriteAllText(Path.Combine(gameUe4ss, "UE4SS-settings.ini"), GameIni);

        _serverFolder = Path.Combine(_root, "Server");
        var serverUe4ss = Path.Combine(DedicatedServerLayout.Win64Folder(_serverFolder), "ue4ss");
        Directory.CreateDirectory(serverUe4ss);
        File.WriteAllText(Path.Combine(serverUe4ss, "UE4SS-settings.ini"), ServerIni);
    }

    private string GameIniPath => Path.Combine(Ue4ssGamePaths.ResolveLoaderFolder(_contentPath), "UE4SS-settings.ini");
    private string ServerIniPath => Path.Combine(DedicatedServerLayout.Win64Folder(_serverFolder), "ue4ss", "UE4SS-settings.ini");

    private Ue4ssConfigViewModel Build() => new(
        new FakeSettings { Current = { IcarusContentPath = _contentPath } },
        new FakeStore([new DedicatedServerProfile { Name = "Server #1", InstallFolder = _serverFolder }]),
        Path.Combine(_root, "Backups"));

    [Fact]
    public void Targets_AreTheGameAndEveryServerWithUe4ss_GameFirst()
    {
        var vm = Build();

        Assert.Equal(["Game", "Server: Server #1"], vm.Targets.Select(t => t.Name));
        Assert.Equal("Game", vm.SelectedTarget?.Name);
        var fields = vm.Sections.Single().Fields;
        Assert.Equal(["EnableHotReloadSystem", "EnableAutoReloadingLuaMods"], fields.Select(f => f.Key));
        Assert.True(fields.All(f => f.IsToggle));
        Assert.Equal(["Server: Server #1"], vm.AlsoSaveTo.Select(a => a.Target.Name));
    }

    /// <summary>Regression: two profiles on one install (a second one via a Saved folder suffix) listed the same UE4SS-settings.ini twice.</summary>
    [Fact]
    public void TwoProfilesOnOneInstall_ShareOneTarget()
    {
        var vm = new Ue4ssConfigViewModel(
            new FakeSettings { Current = { IcarusContentPath = _contentPath } },
            new FakeStore([
                new DedicatedServerProfile { Name = "Server #1", InstallFolder = _serverFolder },
                new DedicatedServerProfile { Name = "Server #2", InstallFolder = _serverFolder, SavedDirSuffix = "_2" },
            ]),
            Path.Combine(_root, "Backups"));

        Assert.Equal(["Game", "Server: Server #1"], vm.Targets.Select(t => t.Name));
    }

    /// <summary>Review 2026-10-23: the targets were read once at start, so a UE4SS installed afterwards never showed up on the tab.</summary>
    [Fact]
    public void Ue4ssInstalledLater_ShowsUpWithoutARestart()
    {
        File.Delete(GameIniPath);
        var vm = new Ue4ssConfigViewModel(
            new FakeSettings { Current = { IcarusContentPath = _contentPath } }, new FakeStore([]), Path.Combine(_root, "Backups"));
        Assert.True(vm.HasNoTargets);

        File.WriteAllText(GameIniPath, GameIni);
        WeakReferenceMessenger.Default.Send(new Ue4ssLoaderChangedMessage("installed"));

        Assert.Equal(["Game"], vm.Targets.Select(t => t.Name));
        Assert.Equal("Game", vm.SelectedTarget?.Name);
    }

    [Fact]
    public void SettingsSaved_WithUnsavedEdits_KeepsTheEdits()
    {
        var vm = Build();
        var hotReload = vm.Sections.Single().Fields.Single(f => f.Key == "EnableHotReloadSystem");
        hotReload.IsOn = true;

        WeakReferenceMessenger.Default.Send(new SettingsSavedMessage());

        Assert.Same(hotReload, vm.Sections.Single().Fields.Single(f => f.Key == "EnableHotReloadSystem"));
        Assert.True(hotReload.IsChanged);
    }

    [Fact]
    public void Save_WritesTheGame_AndOnlyTheChangedSettingToATickedServer_WithBackups()
    {
        var vm = Build();
        var hotReload = vm.Sections.Single().Fields.Single(f => f.Key == "EnableHotReloadSystem");
        hotReload.IsOn = true;
        Assert.True(hotReload.IsChanged);
        vm.AlsoSaveTo.Single().IsChecked = true;

        vm.SaveCommand.Execute(null);

        Assert.Equal(GameIni.Replace("EnableHotReloadSystem = 0", "EnableHotReloadSystem = 1"), File.ReadAllText(GameIniPath));
        // The server got the one changed setting; its own auto-reload choice (0) stays.
        Assert.Equal("1", Ue4ssSettingsDocument.Load(ServerIniPath).GetValue("General", "EnableHotReloadSystem"));
        Assert.Equal("0", Ue4ssSettingsDocument.Load(ServerIniPath).GetValue("General", "EnableAutoReloadingLuaMods"));
        Assert.False(hotReload.IsChanged);
        Assert.Contains("Saved EnableHotReloadSystem to Game and Server: Server #1", vm.StatusMessage);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "Backups", "UE4SS-Settings"), "*.ini", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Save_NothingTicked_LeavesTheServerAlone()
    {
        var vm = Build();
        vm.Sections.Single().Fields.Single(f => f.Key == "EnableAutoReloadingLuaMods").IsOn = false;

        vm.SaveCommand.Execute(null);

        Assert.Contains("EnableAutoReloadingLuaMods = 0", File.ReadAllText(GameIniPath));
        Assert.Equal(ServerIni, File.ReadAllText(ServerIniPath));
    }

    [Fact]
    public void EditingTheServer_SavesThere_AndResetToDefaultUsesTheFilesDefault()
    {
        var vm = Build();
        vm.SelectedTarget = vm.Targets.Single(t => t.IsServer);
        var field = vm.Sections.Single().Fields.Single(f => f.Key == "EnableHotReloadSystem");
        Assert.True(field.IsOn);

        field.IsOn = false;
        vm.SaveCommand.Execute(null);

        Assert.Equal("0", Ue4ssSettingsDocument.Load(ServerIniPath).GetValue("General", "EnableHotReloadSystem"));
        Assert.Equal(GameIni, File.ReadAllText(GameIniPath));

        vm.SelectedTarget = vm.Targets.Single(t => !t.IsServer);
        var gameField = vm.Sections.Single().Fields.Single(f => f.Key == "EnableHotReloadSystem");
        Assert.True(gameField.CanResetToDefault);
        gameField.ResetToDefaultCommand.Execute(null);
        Assert.Equal("1", gameField.Value);
    }

    [Fact]
    public void Filter_ShowsOnlyMatchingSettings()
    {
        var vm = Build();

        vm.Filter = "automatically";

        Assert.Equal(["EnableAutoReloadingLuaMods"], vm.Sections.Single().Fields.Select(f => f.Key));
    }

    /// <summary>
    /// A slider in small steps wrote "2E-05" (C#'s short form) into the mod's config, which a mod
    /// reading plain numbers can't use; and a slider with a step of 0 wrote "NaN".
    /// </summary>
    /// <summary>
    /// UE4SS-settings.ini (real, seen in the app 2026-10-28): "Default: &lt;dll_directory&gt;/Mods"
    /// describes what blank does. A blank value showed Reset to default, and Reset wrote the
    /// description itself into the file.
    /// </summary>
    [Fact]
    public void ADefaultThatDescribesBlank_CountsBlankAsDefault_AndResetBlanksIt()
    {
        const string text =
            "[Overrides]\n; Path to the 'Mods' folder\n; Default: <dll_directory>/Mods\nModsFolderPath = \n\n"
            + "; Default: Empty (parse mods.txt from all mod directories)\nControllingModsTxt = ../x/mods.txt\n";
        var fields = Ue4ssSettingsDocument.Parse(text).Settings.Select(s => new Ue4ssConfigField(s)).ToDictionary(f => f.Key);

        Assert.False(fields["ModsFolderPath"].CanResetToDefault);

        var controlling = fields["ControllingModsTxt"];
        Assert.True(controlling.CanResetToDefault);
        controlling.ResetToDefaultCommand.Execute(null);
        Assert.Equal("", controlling.Value);
        Assert.True(controlling.IsDefault);
    }

    [Fact]
    public void NumberValue_WritesPlainDecimals()
    {
        var small = new Ue4ssConfigField(Ue4ssSettingsDocument.Parse("[A]\nX = 0.00001 ; tiny [slider 0 0.001 0.00001]\n", keepCommentLines: true).Settings.Single());
        small.NumberValue = 0.00002;
        Assert.Equal("0.00002", small.Value);

        var zeroStep = new Ue4ssConfigField(Ue4ssSettingsDocument.Parse("[A]\nX = 2 ; how far [slider 0 10 0]\n", keepCommentLines: true).Settings.Single());
        zeroStep.NumberValue = 5;
        Assert.Equal("5", zeroStep.Value);
    }

    /// <summary>Picking the server in Edit and back dropped the game's unsaved edits (same bug as the mod config editor's file switch).</summary>
    [Fact]
    public void SwitchingTargets_KeepsTheUnsavedEdits()
    {
        var vm = Build();
        vm.Sections.SelectMany(s => s.Fields).Single(f => f.Key == "EnableHotReloadSystem").IsOn = true;

        vm.SelectedTarget = vm.Targets[1];
        Assert.True(vm.HasUnsavedChanges);
        vm.SelectedTarget = vm.Targets[0];

        var field = vm.Sections.SelectMany(s => s.Fields).Single(f => f.Key == "EnableHotReloadSystem");
        Assert.True(field.IsOn);
        Assert.True(field.IsChanged);
        vm.SaveCommand.Execute(null);
        Assert.Contains("EnableHotReloadSystem = 1", File.ReadAllText(GameIniPath));
        Assert.False(vm.HasUnsavedChanges);
    }

    /// <summary>
    /// Final review 2026-10-28: Save wrote the copy read when the target was picked, so a change
    /// made to the file since (by hand, or a new setting a UE4SS update added) was put back.
    /// </summary>
    [Fact]
    public void Save_AfterTheFileChangedOnDisk_KeepsThatChange()
    {
        var vm = Build();
        File.WriteAllText(GameIniPath, GameIni.Replace("EnableAutoReloadingLuaMods = 1", "EnableAutoReloadingLuaMods = 0") + "\n[Debug]\nConsoleEnabled = 1\n");
        vm.Sections.SelectMany(s => s.Fields).Single(f => f.Key == "EnableHotReloadSystem").IsOn = true;

        vm.SaveCommand.Execute(null);

        var saved = Ue4ssSettingsDocument.Load(GameIniPath);
        Assert.Equal("1", saved.GetValue("General", "EnableHotReloadSystem"));
        Assert.Equal("0", saved.GetValue("General", "EnableAutoReloadingLuaMods"));
        Assert.Equal("1", saved.GetValue("Debug", "ConsoleEnabled"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public bool Save() => true;
    }

    private sealed class FakeStore(List<DedicatedServerProfile> profiles) : IDedicatedServerProfileStore
    {
        public IReadOnlyList<DedicatedServerProfile> GetAll() => profiles;

        public string? LoadProblem => null;

        public bool Save(DedicatedServerProfile profile) => true;

        public bool Delete(Guid id) => true;
    }
}
