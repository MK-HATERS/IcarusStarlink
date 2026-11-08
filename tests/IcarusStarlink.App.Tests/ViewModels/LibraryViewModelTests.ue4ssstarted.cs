using System.IO;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Ue4ss;

namespace IcarusStarlink.App.Tests.ViewModels;

public sealed partial class LibraryViewModelTests
{
    // ---- Each UE4SS row says whether UE4SS started the mod last time (UE4SS.log) ----

    /// <summary>One test's own game folder (Icarus\Content, Binaries\Win64\ue4ss\Mods). Deleted on Dispose.</summary>
    private sealed class Ue4ssLogScratch : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "IcarusStarlink.Tests", "Ue4ssLog_" + Guid.NewGuid().ToString("N"));

        public string ContentPath => Path.Combine(Root, "Icarus", "Content");

        public string ModsFolder => Ue4ssGamePaths.ResolveModsFolder(ContentPath);

        public string LogPath => Ue4ssGamePaths.ResolveLoaderLogPath(ContentPath);

        /// <summary>A mod folder in the game with an enabled.txt, all dated `at`.</summary>
        public void AddMod(string name, DateTime at)
        {
            var folder = Path.Combine(ModsFolder, name);
            Directory.CreateDirectory(folder);
            var enabled = Path.Combine(folder, "enabled.txt");
            File.WriteAllText(enabled, "");
            File.SetCreationTime(enabled, at);
            File.SetLastWriteTime(enabled, at);
            Directory.SetCreationTime(folder, at);
            Directory.SetLastWriteTime(folder, at);
        }

        /// <summary>A log whose two start passes ran at `runAt` and started `started`.</summary>
        public void WriteLog(DateTime runAt, params string[] started)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var stamp = runAt.ToString("yyyy-MM-dd HH:mm:ss.fffffff", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllLines(LogPath,
            [
                $"[{stamp}] Starting mods (from mods.txt (x) load order)...",
                $"[{stamp}] Starting mods (from mods.txt (x) load order)...",
                .. started.Select(name => $"[{stamp}] Mod '{name}' has enabled.txt, starting mod."),
            ]);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static readonly DateTime LastGameRun = new(2026, 11, 1, 20, 0, 0, DateTimeKind.Local);

    private static (TestHarness Harness, LibraryViewModel Vm) BuildWithUe4ssMods(Ue4ssLogScratch scratch, params string[] names)
    {
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = scratch.ContentPath;
        foreach (var name in names)
        {
            harness.Ue4ssModStateService.States.Add(new Ue4ssModState(name, IsEnabled: true, IsInGame: true));
        }

        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);
        return (harness, vm);
    }

    private static Ue4ssModRowViewModel Row(LibraryViewModel vm, string name) =>
        vm.Ue4ssRootItems.OfType<Ue4ssModRowViewModel>().Single(r => r.Name == name);

    [Fact]
    public void Ue4ssRow_OnAndInTheLog_SaysStarted_OnButNotInTheLog_SaysDidntStart()
    {
        using var scratch = new Ue4ssLogScratch();
        scratch.AddMod("Keybinds", LastGameRun.AddDays(-1));
        scratch.AddMod("MiniMapa", LastGameRun.AddDays(-1));
        scratch.WriteLog(LastGameRun, "Keybinds");

        var (_, vm) = BuildWithUe4ssMods(scratch, "Keybinds", "MiniMapa");

        var started = Row(vm, "Keybinds");
        Assert.Equal(Ue4ssLastRun.Started, started.LastRun);
        Assert.True(started.ShowLastRun);
        Assert.False(started.LastRunFailed);
        Assert.Equal("Started", started.LastRunText);

        var notStarted = Row(vm, "MiniMapa");
        Assert.Equal(Ue4ssLastRun.NotStarted, notStarted.LastRun);
        Assert.True(notStarted.ShowLastRun);
        Assert.True(notStarted.LastRunFailed);
        Assert.Contains("didn't start", notStarted.LastRunTooltip);
    }

    [Fact]
    public void Ue4ssRow_TurnedOnAfterTheLastRun_SaysNotRunSince_NotDidntStart()
    {
        using var scratch = new Ue4ssLogScratch();
        scratch.AddMod("MiniMapa", LastGameRun.AddHours(1));
        scratch.WriteLog(LastGameRun, "Keybinds");

        var (_, vm) = BuildWithUe4ssMods(scratch, "MiniMapa");

        var row = Row(vm, "MiniMapa");
        Assert.Equal(Ue4ssLastRun.NotRunSince, row.LastRun);
        Assert.False(row.LastRunFailed);
        Assert.Equal("Not run since", row.LastRunText);
    }

    [Fact]
    public void Ue4ssRow_NoLog_SaysNotRunSince()
    {
        using var scratch = new Ue4ssLogScratch();
        scratch.AddMod("Keybinds", LastGameRun);

        var (_, vm) = BuildWithUe4ssMods(scratch, "Keybinds");

        Assert.Equal(Ue4ssLastRun.NotRunSince, Row(vm, "Keybinds").LastRun);
    }

    [Fact]
    public void Ue4ssRow_OffOrWaitingForApply_ShowsNoLastRunBadge()
    {
        using var scratch = new Ue4ssLogScratch();
        scratch.AddMod("Keybinds", LastGameRun.AddDays(-1));
        scratch.WriteLog(LastGameRun, "Keybinds");
        var harness = new TestHarness();
        harness.SettingsService.Current.IcarusContentPath = scratch.ContentPath;
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("Keybinds", IsEnabled: true, IsInGame: true));
        harness.Ue4ssModStateService.States.Add(new Ue4ssModState("Staged", IsEnabled: false));
        var vm = harness.Build();
        vm.ReloadInstalledUe4ssModsCommand.Execute(null);

        Assert.False(Row(vm, "Staged").ShowLastRun);

        var row = Row(vm, "Keybinds");
        Assert.True(row.ShowLastRun);
        row.IsEnabled = false;
        Assert.False(row.ShowLastRun);
    }

    [Fact]
    public void Ue4ssRow_ThePageShownAgainAfterAGameRun_ReadsTheNewLog()
    {
        using var scratch = new Ue4ssLogScratch();
        scratch.AddMod("MiniMapa", LastGameRun.AddHours(1));
        scratch.WriteLog(LastGameRun);
        var (_, vm) = BuildWithUe4ssMods(scratch, "MiniMapa");
        Assert.Equal(Ue4ssLastRun.NotRunSince, Row(vm, "MiniMapa").LastRun);

        scratch.WriteLog(LastGameRun.AddHours(2), "MiniMapa");
        File.SetLastWriteTimeUtc(scratch.LogPath, DateTime.UtcNow.AddMinutes(1));
        vm.OnPageShown();

        Assert.Equal(Ue4ssLastRun.Started, Row(vm, "MiniMapa").LastRun);
    }

    [Fact]
    public void OpenUe4ssLog_WithNoLog_SaysSoInPlainWords()
    {
        using var scratch = new Ue4ssLogScratch();
        var (_, vm) = BuildWithUe4ssMods(scratch);

        vm.OpenUe4ssLogCommand.Execute(null);

        Assert.Contains("No UE4SS.log yet", vm.Ue4ssStatusMessage);
    }
}
