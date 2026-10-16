using System.IO;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Server;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>The Server page's Local server tab against a fake install on disk — nothing is ever launched.</summary>
public sealed class LocalServerViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _install;

    public LocalServerViewModelTests()
    {
        _install = Path.Combine(_root, "Icarus Dedicated Server");
        Directory.CreateDirectory(Path.Combine(_install, "Icarus", "Binaries", "Win64"));
        File.WriteAllText(Path.Combine(_install, "IcarusServer.exe"), "");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string IniPath => DedicatedServerLayout.SettingsIniPath(_install, null);

    private (LocalServerViewModel Vm, FakeStore Store, FakeProcesses Processes, FakeDialogs Dialogs) Build(string? pick = null, bool confirm = true)
    {
        var store = new FakeStore();
        var processes = new FakeProcesses();
        var dialogs = new FakeDialogs(confirm);
        var vm = new LocalServerViewModel(store, processes, dialogs, new ActivityLog(), () => pick, startTimer: false);
        return (vm, store, processes, dialogs);
    }

    /// <summary>Picking a subfolder finds the install root, and an existing ServerSettings.ini is read into the form.</summary>
    [Fact]
    public void AddExistingServer_FromASubfolder_FindsTheRootAndReadsItsIni()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IniPath)!);
        File.WriteAllText(IniPath, "[/Script/Icarus.DedicatedServerSettings]\r\nSessionName=My World\r\nMaxPlayers=6\r\nAdminPassword=adm\r\nResumeProspect=True\r\nLastProspectName=Olympus_Base\r\n");
        var (vm, store, _, _) = Build(pick: Path.Combine(_install, "Icarus", "Binaries", "Win64"));

        vm.AddExistingServerCommand.Execute(null);

        var profile = Assert.Single(store.Profiles);
        Assert.Equal(_install, profile.InstallFolder);
        Assert.Same(profile, vm.SelectedProfile);
        Assert.Equal("My World", vm.SteamServerName);
        Assert.Equal("6", vm.MaxPlayersText);
        Assert.Equal("adm", vm.AdminPassword);
        Assert.False(vm.AdminPasswordIsEmpty);
        Assert.True(vm.ResumeProspect);
        Assert.Equal("Olympus_Base", vm.LastProspectName);
        Assert.Equal("17777", vm.PortText);
    }

    [Fact]
    public void AddExistingServer_NoServerThere_SaysSoAndAddsNothing()
    {
        var (vm, store, _, _) = Build(pick: _root + "_nothing");

        vm.AddExistingServerCommand.Execute(null);

        Assert.Empty(store.Profiles);
        Assert.Contains("No IcarusServer.exe", vm.StatusMessage);
    }

    /// <summary>The documented first-launch bug (no ini until the first lobby): Save writes one, with exactly the form's values and the game's defaults left blank.</summary>
    [Fact]
    public void Save_WithNoIniYet_WritesOneTheGameCanRead()
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.AdminPassword = "s3cret";
        vm.JoinPassword = "friends";
        vm.MaxPlayersText = "4";
        vm.ShutdownIfEmptyForText = "-1";

        vm.SaveSettingsCommand.Execute(null);

        Assert.Null(vm.ValidationMessage);
        var written = ServerSettingsIni.Load(IniPath).Read();
        Assert.Equal("s3cret", written.AdminPassword);
        Assert.Equal("friends", written.JoinPassword);
        Assert.Equal(4, written.MaxPlayers);
        Assert.Equal(-1, written.ShutdownIfEmptyFor);
        Assert.Null(written.ResumeProspect);
        Assert.Equal("Saved.", vm.StatusMessage);
    }

    [Theory]
    [InlineData("9", "17777", "27015", "Max players")]
    [InlineData("4", "0", "27015", "Game port")]
    [InlineData("4", "27015", "27015", "must differ")]
    [InlineData("4", "17777", "abc", "Query port")]
    public void Save_OutOfRangeValues_AreRefusedWithTheReasonAndNothingIsWritten(string maxPlayers, string port, string queryPort, string expected)
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.MaxPlayersText = maxPlayers;
        vm.PortText = port;
        vm.QueryPortText = queryPort;

        vm.SaveSettingsCommand.Execute(null);

        Assert.Contains(expected, vm.ValidationMessage);
        Assert.False(File.Exists(IniPath));
    }

    /// <summary>Start saves first (so the ini exists before the server boots), then launches with the form's launch options.</summary>
    [Fact]
    public async Task Start_SavesThenLaunchesWithTheFormsOptions()
    {
        var (vm, store, processes, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.SteamServerName = "Friends server";
        vm.PortText = "17778";
        vm.QueryPortText = "27016";
        processes.RunAfterStart = true;

        await vm.StartCommand.ExecuteAsync(null);

        var started = Assert.Single(processes.Started);
        Assert.Equal("Friends server", started.SteamServerName);
        Assert.Equal(17778, started.Port);
        Assert.True(File.Exists(IniPath));
        Assert.Equal(17778, store.Profiles.Single().Port);
        Assert.True(vm.IsRunning);
        Assert.StartsWith("RUNNING", vm.RunStateText);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(vm.StopCommand.CanExecute(null));
    }

    [Fact]
    public async Task Stop_AsksFirstAndReportsAForcedEnd()
    {
        var (vm, _, processes, dialogs) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        processes.Running = new RunningServer(1234, DateTime.Now.AddMinutes(-5));
        vm.Tick();
        processes.StopIsClean = false;

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Messages);
        Assert.Equal([1234], processes.Stopped);
        Assert.Contains("was ended", vm.StatusMessage);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Stop_Declined_LeavesTheServerAlone()
    {
        var (vm, _, processes, _) = Build(pick: _install, confirm: false);
        vm.AddExistingServerCommand.Execute(null);
        processes.Running = new RunningServer(1234, DateTime.Now);
        vm.Tick();

        await vm.StopCommand.ExecuteAsync(null);

        Assert.Empty(processes.Stopped);
        Assert.True(vm.IsRunning);
    }

    /// <summary>The log panel follows the server's own log file, hides engine chatter, and filters by text.</summary>
    [Fact]
    public void Tick_TailsTheLogAndAppliesTheFilters()
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        var log = DedicatedServerLayout.LogPath(_install, null, null);
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        File.WriteAllText(log, "LogStreaming: Display: noise\r\nLogNet: Join succeeded: James\r\nLogIcarus: Display: Loading prospect Olympus_Base\r\n");

        vm.Tick();

        Assert.Equal(["LogNet: Join succeeded: James", "LogIcarus: Display: Loading prospect Olympus_Base"], vm.LogLines);

        vm.HideNoise = false;
        Assert.Equal(3, vm.LogLines.Count);

        vm.LogFilter = "join";
        Assert.Equal(["LogNet: Join succeeded: James"], vm.LogLines);
    }

    [Fact]
    public void RemoveProfile_RemovesOnlyTheListEntry()
    {
        var (vm, store, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);

        vm.RemoveProfileCommand.Execute(null);

        Assert.Empty(store.Profiles);
        Assert.Null(vm.SelectedProfile);
        Assert.True(File.Exists(Path.Combine(_install, "IcarusServer.exe")));
    }

    private sealed class FakeStore : IDedicatedServerProfileStore
    {
        public List<DedicatedServerProfile> Profiles { get; } = [];
        public IReadOnlyList<DedicatedServerProfile> GetAll() => Profiles;

        public void Save(DedicatedServerProfile profile)
        {
            Profiles.RemoveAll(p => p.Id == profile.Id);
            Profiles.Add(profile);
        }

        public void Delete(Guid id) => Profiles.RemoveAll(p => p.Id == id);
    }

    private sealed class FakeProcesses : IDedicatedServerProcessService
    {
        public RunningServer? Running { get; set; }
        public bool RunAfterStart { get; set; }
        public bool StopIsClean { get; set; } = true;
        public List<DedicatedServerProfile> Started { get; } = [];
        public List<int> Stopped { get; } = [];

        public RunningServer? FindRunning(DedicatedServerProfile profile) => Running;

        public void Start(DedicatedServerProfile profile)
        {
            Started.Add(profile);
            if (RunAfterStart)
            {
                Running = new RunningServer(42, DateTime.Now);
            }
        }

        public Task<bool> StopAsync(RunningServer server, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Stopped.Add(server.ProcessId);
            Running = null;
            return Task.FromResult(StopIsClean);
        }
    }

    private sealed class FakeDialogs(bool confirmResult) : IDialogService
    {
        public List<string> Messages { get; } = [];

        public bool Confirm(string message, string title, ThemedConfirmSeverity severity)
        {
            Messages.Add(message);
            return confirmResult;
        }

        public RenamePromptResult PromptRename(string currentName, string description = "", string? resetValue = null, string resetLabel = "", string resetTooltip = "", string title = "", string fieldLabel = "") =>
            throw new NotSupportedException();
    }
}
