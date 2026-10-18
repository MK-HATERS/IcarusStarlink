using System.IO;
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
