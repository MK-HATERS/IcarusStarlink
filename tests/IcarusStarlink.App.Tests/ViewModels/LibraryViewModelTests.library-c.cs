using System.IO;
using System.Net.Http;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class LibraryViewModelTests
{
    // ---- Update and "Your build" pills while another update runs ----

    /// <summary>
    /// Holds every Nexus files request open until the test lets them go. Each test lets them go
    /// before it ends: a command's task that never finishes keeps xUnit waiting forever.
    /// </summary>
    private sealed class HeldFileRequests
    {
        private readonly List<TaskCompletionSource<NexusModFileSet>> _held = [];

        public Task<NexusModFileSet> Hold(int modId)
        {
            var request = new TaskCompletionSource<NexusModFileSet>(TaskCreationOptions.RunContinuationsAsynchronously);
            _held.Add(request);
            return request.Task;
        }

        /// <summary>Answers every held request with "no files", which ends each update quietly.</summary>
        public void ReleaseAll()
        {
            foreach (var request in _held)
            {
                request.TrySetResult(new NexusModFileSet([], []));
            }
        }
    }

    /// <summary>Two Nexus rows (mods 1 and 2), signed in; every files request is held open.</summary>
    private static (LibraryViewModel Vm, TestHarness Harness, HeldFileRequests Held, LibraryItemViewModel A, LibraryItemViewModel B) BuildTwoNexusRowsWithHeldFetches()
    {
        var harness = new TestHarness();
        var held = new HeldFileRequests();
        harness.CredentialStore.ApiKey = "key";
        harness.NexusApiClient.FileSetHandler = held.Hold;
        harness.AddMod("ModA", source: "Nexus", nexusModId: 1);
        harness.AddMod("ModB", source: "Nexus", nexusModId: 2);
        var vm = harness.Build();
        var a = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        var b = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModB");
        return (vm, harness, held, a, b);
    }

    /// <summary>Lets every held request go and waits for the updates to end (bounded, so a wrong build fails instead of hanging).</summary>
    private static async Task ReleaseAndWaitAsync(HeldFileRequests held, params Task[] updates)
    {
        held.ReleaseAll();
        await Task.WhenAny(Task.WhenAll(updates), Task.Delay(TimeSpan.FromSeconds(10)));
    }

    /// <summary>Regression: while one Nexus update waited on its download, every other row's Update pill did nothing.</summary>
    [Fact]
    public async Task GetUpdateCommand_WhileAnotherUpdateIsFetching_StaysClickableForOtherRows()
    {
        var (vm, _, held, a, b) = BuildTwoNexusRowsWithHeldFetches();

        var first = vm.GetUpdateCommand.ExecuteAsync(a);
        try
        {
            Assert.True(vm.GetUpdateCommand.CanExecute(b));
        }
        finally
        {
            await ReleaseAndWaitAsync(held, first);
        }
    }

    /// <summary>The same for the UE4SS panel's Update pill.</summary>
    [Fact]
    public async Task GetUe4ssUpdateCommand_WhileAnotherUpdateIsFetching_StaysClickableForOtherRows()
    {
        var harness = new TestHarness();
        var held = new HeldFileRequests();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.CredentialStore.ApiKey = "key";
        harness.NexusApiClient.FileSetHandler = held.Hold;
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("LuaA", IsEnabled: true));
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("LuaB", IsEnabled: true));
        harness.Ue4ssModMetaStore.Metas["LuaA"] = new Ue4ssModMeta { NexusModId = 11, NexusVersion = "1.0" };
        harness.Ue4ssModMetaStore.Metas["LuaB"] = new Ue4ssModMeta { NexusModId = 12, NexusVersion = "1.0" };
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        var luaA = vm.Ue4ssMods.Single(r => r.Name == "LuaA");
        var luaB = vm.Ue4ssMods.Single(r => r.Name == "LuaB");

        var first = vm.GetUe4ssUpdateCommand.ExecuteAsync(luaA);
        try
        {
            Assert.True(vm.GetUe4ssUpdateCommand.CanExecute(luaB));
        }
        finally
        {
            await ReleaseAndWaitAsync(held, first);
        }
    }

    /// <summary>A second click on the row that is already updating asks Nexus nothing more.</summary>
    [Fact]
    public async Task GetUpdateCommand_SecondClickOnTheSameRow_IsRefused()
    {
        var (vm, harness, held, a, _) = BuildTwoNexusRowsWithHeldFetches();

        var first = vm.GetUpdateCommand.ExecuteAsync(a);
        var second = vm.GetUpdateCommand.ExecuteAsync(a);
        try
        {
            Assert.True(second.IsCompleted, "the second click should be refused at once, not wait on Nexus");
            Assert.Contains("already updating", vm.StatusMessage);
            Assert.Equal([1], harness.NexusApiClient.FileSetCalls);
        }
        finally
        {
            await ReleaseAndWaitAsync(held, first, second);
        }
    }

    /// <summary>
    /// Regression: "Your build" asked its question while another update was still choosing or
    /// fetching a file, and that update's file choice could then open over the question.
    /// </summary>
    [Fact]
    public async Task ReplaceOwnBuild_WhileAnUpdateIsRunning_AsksNothing()
    {
        var (vm, harness, held, a, b) = BuildTwoNexusRowsWithHeldFetches();
        b.Version = "2.0";
        b.NewestOnNexus = "1.0";
        Assert.True(b.IsAheadOfNexus);

        var update = vm.GetUpdateCommand.ExecuteAsync(a);
        var replace = vm.ReplaceOwnBuildCommand.ExecuteAsync(b);
        try
        {
            Assert.Empty(harness.DialogService.ConfirmWithWordsMessages);
            Assert.Contains("Wait for the update", vm.StatusMessage);
        }
        finally
        {
            await ReleaseAndWaitAsync(held, update, replace);
        }
    }

    // ---- Re-linking to another Nexus mod ----

    /// <summary>Regression: re-linking to another Nexus ID kept the old mod's Update pill and its cached picture.</summary>
    [Fact]
    public async Task LinkFolderToNexus_ToADifferentId_ClearsTheOldUpdateAndPicture()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA", source: "Nexus", nexusModId: 1);
        var vm = harness.Build();
        var row = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        row.LatestVersion = "2.0";
        row.LatestNexusFileId = 5;
        row.NewestOnNexus = "2.0";
        Directory.CreateDirectory(harness.ThumbnailCacheDirectory);
        var picture = Path.Combine(harness.ThumbnailCacheDirectory, "ModA.img");
        File.WriteAllBytes(picture, [1, 2, 3]);

        await vm.LinkFolderToNexusAsync("ModA", "ModA", 2);

        var after = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        Assert.Null(after.LatestVersion);
        Assert.Null(after.LatestNexusFileId);
        Assert.Null(after.NewestOnNexus);
        Assert.False(File.Exists(picture), "the old mod's cached picture should be gone");
    }

    /// <summary>Linking again to the same ID is not a new mod: what the last check found stays.</summary>
    [Fact]
    public async Task LinkFolderToNexus_ToTheSameId_KeepsWhatTheLastCheckFound()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA", source: "Nexus", nexusModId: 1);
        var vm = harness.Build();
        var row = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        row.LatestVersion = "2.0";
        Directory.CreateDirectory(harness.ThumbnailCacheDirectory);
        var picture = Path.Combine(harness.ThumbnailCacheDirectory, "ModA.img");
        File.WriteAllBytes(picture, [1, 2, 3]);

        await vm.LinkFolderToNexusAsync("ModA", "ModA", 1);

        Assert.Equal("2.0", vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA").LatestVersion);
        Assert.True(File.Exists(picture));
    }

    // ---- Update all ----

    /// <summary>Regression: with only a UE4SS mod out of date, Update all said "Nothing to update".</summary>
    [Fact]
    public async Task UpdateAll_OnlyAUe4ssModIsOutdated_NamesIt()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("CoolLua", IsEnabled: true));
        harness.Ue4ssModMetaStore.Metas["CoolLua"] = new Ue4ssModMeta { NexusModId = 11, NexusVersion = "1.0" };
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        var row = vm.Ue4ssMods.Single(r => r.Name == "CoolLua");
        row.LatestVersion = "2.0";
        Assert.True(row.HasUpdateAvailable);

        await vm.UpdateAllCommand.ExecuteAsync(null);

        Assert.DoesNotContain("Nothing to update", vm.StatusMessage);
        Assert.Contains("CoolLua", vm.StatusMessage);
    }

    /// <summary>Two Nexus rows the last check found out of date.</summary>
    private static (LibraryViewModel Vm, TestHarness Harness) BuildTwoOutdatedNexusRows()
    {
        var harness = new TestHarness();
        harness.CredentialStore.ApiKey = "key";
        harness.AddMod("ModA", source: "Nexus", nexusModId: 1);
        harness.AddMod("ModB", source: "Nexus", nexusModId: 2);
        var vm = harness.Build();
        foreach (var row in vm.RootItems.OfType<LibraryItemViewModel>())
        {
            row.LatestVersion = "2.0";
            Assert.True(row.HasUpdateAvailable);
        }

        return (vm, harness);
    }

    /// <summary>
    /// Signed in, Update all runs each outdated Nexus mod's own Update one after another (the same
    /// file choice and download as its pill) instead of only naming them. The harness's Nexus page
    /// factory throws, so the page isn't opened either.
    /// </summary>
    [Fact]
    public async Task UpdateAll_SignedIn_RunsEachNexusModsOwnUpdateInTurn()
    {
        var (vm, harness) = BuildTwoOutdatedNexusRows();

        await vm.UpdateAllCommand.ExecuteAsync(null);

        Assert.Equal([1, 2], harness.NexusApiClient.FileSetCalls);
        Assert.Contains("ModA", vm.StatusMessage);
        Assert.Contains("ModB", vm.StatusMessage);
    }

    /// <summary>Cancelling one mod's file choice stops Update all there; the rest are named, not run.</summary>
    [Fact]
    public async Task UpdateAll_FileChoiceCancelled_StopsThere()
    {
        var (vm, harness) = BuildTwoOutdatedNexusRows();
        harness.NexusApiClient.FileSets[1] = AllInOneFiles();
        harness.NexusApiClient.FileSets[2] = AllInOneFiles();

        await vm.UpdateAllCommand.ExecuteAsync(null);

        Assert.Single(harness.DialogService.NexusFilePicks);
        Assert.Equal([1], harness.NexusApiClient.FileSetCalls);
        Assert.Contains("Stopped at 'ModA'", vm.StatusMessage);
        Assert.Contains("ModB", vm.StatusMessage);
    }

    /// <summary>A download's two halves (the pak and its Lua half) share one Nexus file: Update all gets it once.</summary>
    [Fact]
    public async Task UpdateAll_TwoHalvesOfOneDownload_UpdatesThemOnce()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.CredentialStore.ApiKey = "key";
        harness.AddMod("Wireless", source: "Nexus", nexusModId: 7).NexusFileId = 70;
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("WirelessLua", IsEnabled: true));
        harness.Ue4ssModMetaStore.Metas["WirelessLua"] = new Ue4ssModMeta { NexusModId = 7, NexusVersion = "1.0", NexusFileId = 70 };
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        var pak = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Wireless");
        var lua = vm.Ue4ssMods.Single(r => r.Name == "WirelessLua");
        pak.LatestNexusFileId = 71;
        lua.LatestNexusFileId = 71;
        Assert.True(pak.HasUpdateAvailable && lua.HasUpdateAvailable);
        // The silent update check after a reload asks for the tracked files too: count only Update all's requests.
        harness.NexusApiClient.FileSetCalls.Clear();

        await vm.UpdateAllCommand.ExecuteAsync(null);

        Assert.Equal([7], harness.NexusApiClient.FileSetCalls);
    }

    /// <summary>
    /// Regression: a second outdated download of the same Nexus mod, with no recorded file, was
    /// skipped as "the other half" without a word: neither run nor named in "Not updated".
    /// </summary>
    [Fact]
    public async Task UpdateAll_SecondDownloadOfTheSameModWithNoKnownFile_IsNamedNotSkippedSilently()
    {
        var harness = new TestHarness();
        harness.CredentialStore.ApiKey = "key";
        harness.AddMod("FirstCopy", source: "Nexus", nexusModId: 1);
        harness.AddMod("SecondCopy", source: "Nexus", nexusModId: 1);
        var vm = harness.Build();
        foreach (var row in vm.RootItems.OfType<LibraryItemViewModel>())
        {
            row.LatestVersion = "2.0";
        }

        await vm.UpdateAllCommand.ExecuteAsync(null);

        Assert.Contains("FirstCopy", vm.StatusMessage);
        Assert.Contains("SecondCopy", vm.StatusMessage);
    }

    /// <summary>An account Nexus says isn't Premium can't download through the API: Update all names the mods, as before.</summary>
    [Fact]
    public async Task UpdateAll_AccountNotPremium_OnlyNamesTheMods()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.CredentialStore.ApiKey = "key";
        harness.NexusApiClient.User = new NexusUserInfo(5, "me", IsPremium: false, IsSupporter: false, "", "");
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("CoolLua", IsEnabled: true));
        harness.Ue4ssModMetaStore.Metas["CoolLua"] = new Ue4ssModMeta { NexusModId = 11, NexusVersion = "1.0" };
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        vm.Ue4ssMods.Single(r => r.Name == "CoolLua").LatestVersion = "2.0";

        await vm.UpdateAllCommand.ExecuteAsync(null);

        Assert.Empty(harness.NexusApiClient.FileSetCalls);
        Assert.Contains("CoolLua", vm.StatusMessage);
        Assert.Contains("UE4SS panel", vm.StatusMessage);
    }

    // ---- Check for updates: how many Nexus requests ----

    /// <summary>
    /// Nexus requests held open until the test answers them. Thread-safe: once one is answered,
    /// the next waiting request can start on another thread.
    /// </summary>
    private sealed class HeldRequests<T>
    {
        private readonly object _gate = new();
        private readonly List<TaskCompletionSource<T>> _held = [];

        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _held.Count;
                }
            }
        }

        public Task<T> Hold()
        {
            var request = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _held.Add(request);
            }

            return request.Task;
        }

        /// <summary>Answers every request, including the ones that start meanwhile, until the work ends (bounded, so a wrong build fails instead of hanging).</summary>
        public async Task AnswerUntilDoneAsync(T answer, Task work)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!work.IsCompleted && DateTime.UtcNow < deadline)
            {
                TaskCompletionSource<T>[] pending;
                lock (_gate)
                {
                    pending = [.. _held];
                }

                foreach (var request in pending)
                {
                    request.TrySetResult(answer);
                }

                await Task.WhenAny(work, Task.Delay(10));
            }
        }
    }

    private static Ue4ssModRowViewModel AddUe4ssRow(TestHarness harness, LibraryViewModel vm, string name, Ue4ssModMeta meta)
    {
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState(name, IsEnabled: true));
        harness.Ue4ssModMetaStore.Metas[name] = meta;
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        return vm.Ue4ssMods.Single(r => r.Name == name);
    }

    /// <summary>Regression: a mod's data half and its Lua half asked Nexus for the same mod twice.</summary>
    [Fact]
    public async Task CheckForUpdates_DataAndLuaHalvesOfOneMod_AskNexusOnce()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        harness.CredentialStore.ApiKey = "key";
        harness.AddMod("Bags", source: "Nexus", nexusModId: 11);
        harness.NexusApiClient.ModInfoById[11] = new NexusModInfo(11, "Bags", "someone", "", "2.0", null);
        var vm = harness.Build();
        AddUe4ssRow(harness, vm, "BagsLua", new Ue4ssModMeta { NexusModId = 11, NexusVersion = "1.0" });
        harness.NexusApiClient.GetModInfoCallCount = 0;

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal(1, harness.NexusApiClient.GetModInfoCallCount);
        Assert.Equal("2.0", vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "Bags").LatestVersion);
        Assert.Equal("2.0", vm.Ue4ssMods.Single(r => r.Name == "BagsLua").LatestVersion);
        Assert.Contains("2 mod(s) have an update", vm.UpdateCheckStatusMessage);
    }

    /// <summary>Regression: every mod without a recorded file asked Nexus at the same moment, so the Nexus limit failed most of them.</summary>
    [Fact]
    public async Task CheckForUpdates_ManyModsWithoutAFile_AskNexusAtMostFourAtOnce()
    {
        var harness = new TestHarness();
        harness.CredentialStore.ApiKey = "key";
        for (var modId = 1; modId <= 6; modId++)
        {
            harness.AddMod($"Mod{modId}", source: "Nexus", nexusModId: modId);
        }

        var vm = harness.Build();
        var held = new HeldRequests<NexusModInfo?>();
        harness.NexusApiClient.ModInfoHandler = _ => held.Hold();

        var check = vm.CheckForUpdatesCommand.ExecuteAsync(null);
        try
        {
            Assert.Equal(4, held.Count);
        }
        finally
        {
            await held.AnswerUntilDoneAsync(null, check);
        }

        Assert.True(check.IsCompleted, "the check should end once every request is answered");
    }

    /// <summary>The same cap for the per-file check of mods that know their Nexus file.</summary>
    [Fact]
    public async Task CheckForUpdates_ManyModsWithAFile_AskNexusAtMostFourAtOnce()
    {
        var harness = new TestHarness();
        harness.CredentialStore.ApiKey = "key";
        for (var modId = 1; modId <= 6; modId++)
        {
            harness.AddMod($"Mod{modId}", source: "Nexus", nexusModId: modId).NexusFileId = modId * 10;
        }

        var vm = harness.Build();
        var held = new HeldRequests<NexusModFileSet>();
        harness.NexusApiClient.FileSetHandler = _ => held.Hold();

        var check = vm.CheckForUpdatesCommand.ExecuteAsync(null);
        try
        {
            Assert.Equal(4, held.Count);
        }
        finally
        {
            await held.AnswerUntilDoneAsync(new NexusModFileSet([], []), check);
        }

        Assert.True(check.IsCompleted, "the check should end once every request is answered");
    }

    // ---- Update texts ----

    /// <summary>Regression: with only Nexus mods and no key, Check for updates said "Everything checked is up to date." though nothing was checked.</summary>
    [Fact]
    public async Task CheckForUpdates_NexusModsWithNoKey_DoesNotSayUpToDate()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA", source: "Nexus", nexusModId: 1);
        var vm = harness.Build();

        await vm.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Contains("Sign in", vm.UpdateCheckStatusMessage);
        Assert.DoesNotContain("up to date", vm.UpdateCheckStatusMessage);
    }

    /// <summary>Regression: a Nexus version written "v1.0" read "(vv1.0)" in the "Your build" question.</summary>
    [Fact]
    public async Task ReplaceOwnBuild_NexusVersionWrittenWithAV_AsksWithoutADoubleV()
    {
        var harness = new TestHarness();
        harness.AddMod("ModA", source: "Nexus", nexusModId: 1, version: "2.0");
        var vm = harness.Build();
        var row = vm.RootItems.OfType<LibraryItemViewModel>().Single(i => i.FolderName == "ModA");
        row.NewestOnNexus = "v1.0";
        Assert.True(row.IsAheadOfNexus);

        await vm.ReplaceOwnBuildCommand.ExecuteAsync(row);

        var question = Assert.Single(harness.DialogService.ConfirmWithWordsMessages);
        Assert.Contains("(v2.0)", question);
        Assert.Contains("(v1.0)", question);
        Assert.DoesNotContain("vv", question);
    }

    /// <summary>Regression: a UE4SS mod whose Nexus version is written "v1.2" showed "Update vv1.2" and "Nexus: … · vv1.2".</summary>
    [Fact]
    public void Ue4ssRow_NexusVersionWrittenWithAV_ShowsOneV()
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "FakeContent");
        var vm = harness.Build();
        var row = AddUe4ssRow(harness, vm, "CoolLua", new Ue4ssModMeta { NexusModId = 11, NexusVersion = "v1.2", NexusName = "Cool" });

        row.LatestVersion = "v1.3";

        Assert.Equal("Nexus: Cool · v1.2", row.NexusSummary);
        Assert.Equal("v1.3", row.LatestVersionText);
        Assert.Equal("v1.3", row.NewestOnNexusText);
    }
}
