using System.IO;
using System.IO.Compression;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Patches;

namespace IcarusStarlink.App.Tests.ViewModels;

/// <summary>Restore, admin list, patch UE4SS mods, failures in the activity log, and the Connect card's Bind IP.</summary>
public sealed partial class LocalServerViewModelTests
{
    /// <summary>Like Build, with an activity log the test can read.</summary>
    private (LocalServerViewModel Vm, FakeDialogs Dialogs) BuildLogged(ActivityLog log)
    {
        var dialogs = new FakeDialogs(confirmResult: true);
        var vm = new LocalServerViewModel(new FakeStore(), new FakeProcesses(), dialogs, log, SteamCmd, Settings, MergedPakPath, BackupsRoot, _ => _install, () => null, startTimer: false, firewall: Firewall,
            network: Network)
        {
            StartPollDelay = TimeSpan.Zero,
            RunWorkInline = true,
            CopyToClipboard = Copied.Add,
        };
        SteamCmd.Chosen = () => Settings.Current.SteamCmdExePath;
        return (vm, dialogs);
    }

    // ---- Restore ----

    /// <summary>
    /// Regression: a backup broken part way was extracted straight into Saved and the message said
    /// only "Restore failed". Now Saved is untouched, the message says so, and the bell has it.
    /// </summary>
    [Fact]
    public async Task Restore_ABrokenBackup_SaysNothingWasChanged_AndLogsAWarning()
    {
        WriteProspect("A", content: "current");
        var log = new ActivityLog();
        var (vm, _) = BuildLogged(log);
        vm.AddExistingServerCommand.Execute(null);
        var folder = vm.BackupsFolderPath;
        Directory.CreateDirectory(folder);
        using (var archive = ZipFile.Open(Path.Combine(folder, "2026-01-01_000000_manual.zip"), ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("PlayerData/DedicatedServer/Prospects/A.json").Open()))
            {
                w.Write("backup");
            }

            // A file where the Prospects folder is: it can't be extracted.
            using (var w = new StreamWriter(archive.CreateEntry("PlayerData/DedicatedServer/Prospects").Open()))
            {
                w.Write("x");
            }
        }

        vm.SelectedProfile = null;
        vm.SelectedProfile = vm.Profiles.Single();
        vm.SelectedBackup = vm.Backups.Single(b => b.Name == "2026-01-01_000000_manual");

        await vm.RestoreSelectedBackupCommand.ExecuteAsync(null);

        Assert.Contains("Nothing was changed", vm.StatusMessage);
        Assert.Equal("current", File.ReadAllText(Path.Combine(DedicatedServerLayout.ProspectsFolder(_install, null), "A.json")));
        Assert.Contains(log.Entries, e => e.Kind == ActivityEntryKind.Warning && e.Message.StartsWith("Restore failed", StringComparison.Ordinal));
    }

    // ---- Admin list ----

    /// <summary>Regression: Save took 17 digits that don't start 7656, which the hint under the box calls not a Steam64 id.</summary>
    [Fact]
    public void SaveAccessLists_SeventeenDigitsNotStarting7656_IsRefusedLikeTheHintSays()
    {
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);
        vm.AdminWhitelistText = "12345678901234567\n";
        Assert.NotNull(vm.AdminListHint);

        vm.SaveAccessListsCommand.Execute(null);

        Assert.Contains("12345678901234567", vm.StatusMessage);
        Assert.False(File.Exists(DedicatedServerLayout.AdminWhitelistPath(_install, null)));
    }

    // ---- Apply a patch: its own UE4SS mods ----

    /// <summary>A bundled UE4SS mod folder with a Lua script and a native DLL, as a patch carries it.</summary>
    private string MakeBundledMod(string name)
    {
        var folder = Path.Combine(_root, "PatchExtract", "BundledUe4ss", name);
        Directory.CreateDirectory(Path.Combine(folder, "Scripts"));
        Directory.CreateDirectory(Path.Combine(folder, "dlls"));
        File.WriteAllText(Path.Combine(folder, "Scripts", "main.lua"), "-- " + name);
        File.WriteAllText(Path.Combine(folder, "dlls", "main.dll"), "native");
        return folder;
    }

    private (LocalServerViewModel Vm, FakeDialogs Dialogs) BuildForBundledPatch(string modName, bool enabledInPatch = true)
    {
        var gameWin64 = MakeGameWithUe4ss();
        Settings.Current.IcarusContentPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameWin64)!)!, "Content");
        var patches = new FakePatchService
        {
            ImportResult = new PatchImportContents(
                new PatchManifest { FormatVersion = 2, ProfileName = "P", Ue4ssMods = [new PatchUe4ssEntry { Name = modName, Bundled = true, Enabled = enabledInPatch }] },
                new Dictionary<string, IcarusStarlink.PakIO.Container.ExmodPackageContents>(),
                bundledUe4ssFolders: new Dictionary<string, string> { [modName] = MakeBundledMod(modName) }),
        };
        var importer = new FakePatchImporter { Result = "Imported 'P'." };
        var (vm, _, _, dialogs) = Build(pick: _install, patches: patches, importer: importer);
        vm.AddExistingServerCommand.Execute(null);
        vm.InstallUe4ssOnServerCommand.Execute(null);
        dialogs.OpenFilePath = Path.Combine(_root, "patch.zip");
        return (vm, dialogs);
    }

    /// <summary>
    /// Regression: a UE4SS mod the patch carries (Lua and a DLL that run with the host's rights)
    /// went onto the server and was switched on with no question, even after the host said No to
    /// it on the game side. Now it asks, names the DLL, and No puts nothing on the server.
    /// </summary>
    [Fact]
    public async Task ApplyPatchToServer_BundledUe4ssMod_AskedFirst_NoLeavesServerUntouched()
    {
        var (vm, dialogs) = BuildForBundledPatch("Evil");
        dialogs.Answers.Enqueue(false);

        await vm.ApplyPatchToServerCommand.ExecuteAsync(null);

        Assert.False(Directory.Exists(Path.Combine(DedicatedServerLayout.Ue4ssModsFolder(_install), "Evil")));
        Assert.Contains(dialogs.Messages, m => m.Contains("Evil", StringComparison.Ordinal) && m.Contains("DLL", StringComparison.Ordinal));
        Assert.Contains("you said no", vm.ShareStatusMessage);
    }

    /// <summary>Regression: a server copy the owner had switched off was switched back on by the patch with no question.</summary>
    [Fact]
    public async Task ApplyPatchToServer_BundledModOffOnTheServer_NoKeepsItOff()
    {
        var (vm, dialogs) = BuildForBundledPatch("Evil");
        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(_install);
        Directory.CreateDirectory(Path.Combine(serverMods, "Evil", "Scripts"));
        File.WriteAllText(Path.Combine(serverMods, "Evil", "Scripts", "main.lua"), "-- server copy");
        var list = Ue4ssModStart.ReadList(serverMods);
        list.Set("Evil", false);
        list.Save(Path.Combine(serverMods, Ue4ssModStart.ListFileName));
        dialogs.Answers.Enqueue(false);

        await vm.ApplyPatchToServerCommand.ExecuteAsync(null);

        Assert.False(Ue4ssModStart.Starts(serverMods, "Evil", Ue4ssModStart.ReadList(serverMods)));
        Assert.Contains(dialogs.Messages, m => m.Contains("on the server, now off", StringComparison.Ordinal));
    }

    /// <summary>Yes puts the patch's copy on the server, but a mod the patch marks off isn't switched on (it used to be, whatever the patch said).</summary>
    [Fact]
    public async Task ApplyPatchToServer_BundledModMarkedOff_YesCopiesItButLeavesItOff()
    {
        var (vm, dialogs) = BuildForBundledPatch("Quiet", enabledInPatch: false);
        dialogs.Answers.Enqueue(true);

        await vm.ApplyPatchToServerCommand.ExecuteAsync(null);

        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(_install);
        Assert.True(File.Exists(Path.Combine(serverMods, "Quiet", "Scripts", "main.lua")));
        Assert.False(Ue4ssModStart.Starts(serverMods, "Quiet", Ue4ssModStart.ReadList(serverMods)));
    }

    /// <summary>Yes on a mod the patch has on: copied and switched on, as before.</summary>
    [Fact]
    public async Task ApplyPatchToServer_BundledMod_YesCopiesItAndTurnsItOn()
    {
        var (vm, dialogs) = BuildForBundledPatch("Wanted");
        dialogs.Answers.Enqueue(true);

        await vm.ApplyPatchToServerCommand.ExecuteAsync(null);

        var serverMods = DedicatedServerLayout.Ue4ssModsFolder(_install);
        Assert.True(Ue4ssModStart.Starts(serverMods, "Wanted", Ue4ssModStart.ReadList(serverMods)));
        Assert.Contains("1 UE4SS mod(s) put on the server (Wanted)", vm.ShareStatusMessage);
    }

    /// <summary>Regression: the server side wrote a patch "config" that is really code (Scripts\main.lua) into the server's copy; the game side refuses it.</summary>
    [Fact]
    public async Task ApplyPatchToServer_AConfigThatIsCode_IsNotWritten()
    {
        var gameWin64 = MakeGameWithUe4ss(("MkHatersTools", true));
        Settings.Current.IcarusContentPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameWin64)!)!, "Content");
        var patches = new FakePatchService
        {
            ImportResult = new PatchImportContents(
                new PatchManifest
                {
                    FormatVersion = 2, ProfileName = "P",
                    Ue4ssMods = [new PatchUe4ssEntry { Name = "MkHatersTools", Configs = [new PatchConfigFile { Path = "Scripts/main.lua", Text = "os.execute('calc')\n" }] }],
                },
                new Dictionary<string, IcarusStarlink.PakIO.Container.ExmodPackageContents>()),
        };
        var (vm, _, _, dialogs) = Build(pick: _install, patches: patches, importer: new FakePatchImporter { Result = "Imported 'P'." });
        vm.AddExistingServerCommand.Execute(null);
        vm.InstallUe4ssOnServerCommand.Execute(null);
        dialogs.OpenFilePath = Path.Combine(_root, "patch.zip");

        await vm.ApplyPatchToServerCommand.ExecuteAsync(null);

        Assert.Equal("-- MkHatersTools", File.ReadAllText(Path.Combine(DedicatedServerLayout.Ue4ssModsFolder(_install), "MkHatersTools", "Scripts", "main.lua")));
    }

    // ---- Failures reach the activity log ----

    /// <summary>Regression: a failed merged-pak copy only set the page's status line, so a user on another page never saw it.</summary>
    [Fact]
    public async Task InstallMergedPak_CopyFails_IsLoggedAsAWarning()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MergedPakPath)!);
        File.WriteAllBytes(MergedPakPath, [1, 2, 3]);
        var log = new ActivityLog();
        var (vm, _) = BuildLogged(log);
        vm.AddExistingServerCommand.Execute(null);

        using (new FileStream(MergedPakPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await vm.InstallMergedPakCommand.ExecuteAsync(null);
        }

        Assert.StartsWith("Couldn't copy the pak", vm.StatusMessage);
        Assert.Contains(log.Entries, e => e.Kind == ActivityEntryKind.Warning && e.Message.StartsWith("Couldn't copy the pak", StringComparison.Ordinal));
    }

    // ---- Connect card ----

    /// <summary>Regression: with Settings › Advanced › Bind IP set, the card still showed the adapter it picked, an address the server doesn't answer on.</summary>
    [Fact]
    public async Task Connect_BindIpSet_ShowsThatAddress()
    {
        Network.Adapters = [new NetworkAdapterInfo("Ethernet", "Intel(R) Ethernet Connection", true, NetworkAdapterKind.Ethernet, true, ["192.168.1.20"])];
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);

        vm.Multihome = "192.168.1.50";
        vm.SaveSettingsCommand.Execute(null);
        await vm.RefreshLanAddressAsync();

        Assert.Equal("192.168.1.50:17777", vm.LanJoinAddress);
        Assert.Contains("Bind IP", vm.LanAddressText);
        Assert.Contains("this PC (192.168.1.50)", vm.PortForwardHint);
    }

    /// <summary>A Bind IP of 0.0.0.0 (every adapter) or a host name keeps the address the card picked.</summary>
    [Fact]
    public async Task Connect_BindIpEveryAdapter_KeepsThePickedAddress()
    {
        Network.Adapters = [new NetworkAdapterInfo("Ethernet", "Intel(R) Ethernet Connection", true, NetworkAdapterKind.Ethernet, true, ["192.168.1.20"])];
        var (vm, _, _, _) = Build(pick: _install);
        vm.AddExistingServerCommand.Execute(null);

        vm.Multihome = "0.0.0.0";
        vm.SaveSettingsCommand.Execute(null);
        await vm.RefreshLanAddressAsync();

        Assert.Equal("192.168.1.20:17777", vm.LanJoinAddress);
        Assert.Equal("192.168.1.20:17777", vm.LanAddressText);
    }

    // ---- Share tab: patch work off the UI thread ----

    /// <summary>
    /// Stands in for the window's dispatcher: code that sees it as the current context ran on the
    /// UI thread. Like a dispatcher, it runs posted continuations with itself as the context.
    /// </summary>
    private sealed class UiThreadMarker : SynchronizationContext
    {
        public static bool OnUiThread => Current is UiThreadMarker;

        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    d(state);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            });

        public override SynchronizationContext CreateCopy() => this;
    }

    /// <summary>Starts a command the way a click does: its synchronous part runs with the UI context current.</summary>
    private static Task StartOnUiThread(Func<Task> start)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new UiThreadMarker());
        try
        {
            return start();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>Notes whether each patch export or import ran on the UI thread.</summary>
    private sealed class ThreadNotingPatchService(PatchImportContents? import = null) : IPatchService
    {
        public bool? ExportOnUiThread { get; private set; }
        public bool? ImportOnUiThread { get; private set; }

        public Task ExportAsync(PatchManifest manifest, PatchBundle bundle, string outputPath, CancellationToken cancellationToken = default)
        {
            ExportOnUiThread = UiThreadMarker.OnUiThread;
            return Task.CompletedTask;
        }

        public Task<PatchImportContents> ImportAsync(string patchFilePath, CancellationToken cancellationToken = default)
        {
            ImportOnUiThread = UiThreadMarker.OnUiThread;
            return Task.FromResult(import ?? throw new NotSupportedException("No import set."));
        }
    }

    /// <summary>Notes whether building the patch (it reads each UE4SS mod's Nexus link here) ran on the UI thread.</summary>
    private sealed class ThreadNotingMetaStore : IUe4ssModMetaStore
    {
        public bool? LoadOnUiThread { get; private set; }

        public Ue4ssModMeta Load(string folderName)
        {
            LoadOnUiThread = UiThreadMarker.OnUiThread;
            return new Ue4ssModMeta();
        }

        public void Save(string folderName, Ue4ssModMeta meta) => throw new NotSupportedException();
        public void Delete(string folderName) => throw new NotSupportedException();
        public IReadOnlyList<string> ListFolderNames() => [];
    }

    /// <summary>A server added and set up inline; the patch command under test then runs its work as the app does.</summary>
    private (LocalServerViewModel Vm, FakeDialogs Dialogs) BuildForShare(IPatchService patches, IUe4ssModMetaStore? meta = null)
    {
        var dialogs = new FakeDialogs(confirmResult: true);
        var vm = new LocalServerViewModel(new FakeStore(), new FakeProcesses(), dialogs, new ActivityLog(), SteamCmd, Settings, MergedPakPath, BackupsRoot, _ => _install, () => null, startTimer: false, firewall: Firewall,
            library: new FakeLibrary(), patchService: patches, ue4ssMetaStore: meta, network: Network)
        {
            StartPollDelay = TimeSpan.Zero,
            RunWorkInline = true,
            CopyToClipboard = Copied.Add,
        };
        SteamCmd.Chosen = () => Settings.Current.SteamCmdExePath;
        vm.AddExistingServerCommand.Execute(null);
        return (vm, dialogs);
    }

    private static PatchImportContents EmptyPatch() =>
        new(new PatchManifest { FormatVersion = 2, ProfileName = "P" }, new Dictionary<string, IcarusStarlink.PakIO.Container.ExmodPackageContents>());

    /// <summary>
    /// Regression: Export for players built the patch and wrote it (bundled UE4SS folders, paks of
    /// hundreds of MB) on the UI thread, so the window froze until the file was written.
    /// </summary>
    [Fact]
    public async Task ExportServerPatch_BuildsAndWritesThePatchOffTheUiThread()
    {
        var gameWin64 = MakeGameWithUe4ss(("MkHatersTools", true));
        Settings.Current.IcarusContentPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(gameWin64)!)!, "Content");
        var patches = new ThreadNotingPatchService();
        var meta = new ThreadNotingMetaStore();
        var (vm, dialogs) = BuildForShare(patches, meta);
        vm.InstallUe4ssOnServerCommand.Execute(null);
        vm.MatchServerToGameCommand.Execute(null);
        dialogs.SaveFilePath = Path.Combine(_root, "ISL-Patch-server.zip");
        vm.RunWorkInline = false;

        await StartOnUiThread(() => vm.ExportServerPatchCommand.ExecuteAsync(null));

        Assert.StartsWith("Exported", vm.ShareStatusMessage);
        Assert.Equal(false, meta.LoadOnUiThread);
        Assert.Equal(false, patches.ExportOnUiThread);
    }

    /// <summary>Regression: Apply a patch unpacked it (every bundled pak and UE4SS folder) on the UI thread.</summary>
    [Fact]
    public async Task ApplyPatchToServer_UnpacksThePatchOffTheUiThread()
    {
        var patches = new ThreadNotingPatchService(EmptyPatch());
        var (vm, dialogs) = BuildForShare(patches);
        dialogs.OpenFilePath = Path.Combine(_root, "patch.zip");
        vm.RunWorkInline = false;

        await StartOnUiThread(() => vm.ApplyPatchToServerCommand.ExecuteAsync(null));

        Assert.Equal(false, patches.ImportOnUiThread);
    }

    /// <summary>Regression: Compare a patch unpacked the whole patch on the UI thread just to read its list.</summary>
    [Fact]
    public async Task ComparePatch_UnpacksThePatchOffTheUiThread()
    {
        var patches = new ThreadNotingPatchService(EmptyPatch());
        var (vm, dialogs) = BuildForShare(patches);
        dialogs.OpenFilePath = Path.Combine(_root, "patch.zip");
        vm.RunWorkInline = false;

        await StartOnUiThread(() => vm.ComparePatchCommand.ExecuteAsync(null));

        Assert.True(vm.HasPatchCompare);
        Assert.Equal(false, patches.ImportOnUiThread);
    }
}
