using System.IO;
using System.Windows;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Update everything in the real app: each step is a page's own command (Settings' Refresh game
/// data, the Library's checks and fixes, Merge &amp; Install's build and install), so nothing here
/// does that work a second way. Also the review window's buttons, and Roll back.
/// </summary>
public sealed class GameUpdateCoordinator(
    Func<LibraryViewModel> library, Func<MergeInstallViewModel> mergeInstall, Func<SettingsViewModel> settingsViewModel,
    ISettingsService settingsService, IUe4ssModStateService ue4ssModStateService, LastKnownGoodStore snapshots,
    GameUpdateDetector detector, IGameProcessChecker gameProcessChecker, IDialogService dialogService, IActivityLog activityLog,
    string backupDirectory) : IGameUpdateSteps, IGameUpdateReviewActions
{
    private GameUpdateWindow? _window;

    /// <summary>The bar's Update everything…: runs the steps in the review window, then fills in what changed.</summary>
    public async Task RunAsync()
    {
        if (_window is { IsVisible: true } open && open.DataContext is GameUpdateReviewViewModel { IsRunning: true })
        {
            open.Activate();
            return;
        }

        if (!dialogService.Confirm(
                "Update everything gets your mods ready for the updated game:\n\n"
                + "1. Saves your setup as it is now, so you can roll back.\n"
                + "2. Refreshes game data.\n"
                + "3. Checks every mod and makes the proven fixes (each mod backed up first).\n"
                + "4. Checks for newer versions of your mods.\n"
                + "5. Builds the merge and installs it (not while Icarus is running).\n\n"
                + "Then it shows what changed. Start?",
                "Update everything", ThemedConfirmSeverity.Question))
        {
            return;
        }

        var viewModel = new GameUpdateReviewViewModel(this);
        ShowWindow(viewModel);

        var build = await detector.ReadCurrentBuildAsync();
        var result = await viewModel.RunAsync(this);
        if (result.Snapshot is { } snapshot)
        {
            try
            {
                snapshots.SaveRunRecord(snapshot, new GameUpdateRunRecord
                {
                    FinishedAtUtc = DateTimeOffset.UtcNow,
                    GameBuildId = build,
                    Completed = result.Completed,
                    Message = result.Message,
                    Fixed = [.. result.Fixed],
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Only the review opened again later misses what was fixed.
            }
        }

        if (result.Completed && build is not null)
        {
            library().MarkGameUpdateHandled(build);
        }

        activityLog.Log(result.Completed ? $"Update everything: {result.Message}" : result.Message,
            result.Completed && !result.InstallSkippedGameRunning ? ActivityEntryKind.Success : ActivityEntryKind.Warning);

        await FillReviewAsync(viewModel, result.Fixed, build);
    }

    /// <summary>Check ▾ › What the last game update changed…: the last run's fixes, and the mods as they are now.</summary>
    public void ShowLastReview()
    {
        if (_window is { IsVisible: true } open && open.DataContext is GameUpdateReviewViewModel { IsRunning: true })
        {
            open.Activate();
            return;
        }

        var viewModel = new GameUpdateReviewViewModel(this);
        var snapshot = snapshots.Latest();
        var record = snapshot is null ? null : snapshots.LoadRunRecord(snapshot);
        viewModel.Snapshot = snapshot;
        viewModel.Summary = (snapshot, record) switch
        {
            (null, _) => "Update everything hasn't run yet. When Icarus updates, the Library's bar offers it. Below is how your mods look against the game now.",
            (_, null) => $"The last Update everything started {snapshot.CreatedAtUtc.ToLocalTime():g} and didn't finish. Below is how your mods look against the game now.",
            _ => $"The last Update everything ran {record.FinishedAtUtc.ToLocalTime():g}: {record.Message}",
        };
        ShowWindow(viewModel);
        _ = FillReviewAsync(viewModel, record?.Fixed ?? [], null);
    }

    private void ShowWindow(GameUpdateReviewViewModel viewModel)
    {
        _window?.Close();
        _window = new GameUpdateWindow(viewModel) { Owner = Application.Current.MainWindow };
        _window.Show();
    }

    private async Task FillReviewAsync(GameUpdateReviewViewModel viewModel, IReadOnlyList<GameUpdateFixedMod> fixedMods, string? build)
    {
        var page = library();
        build ??= await detector.ReadCurrentBuildAsync();
        if (build is not null && page.IsGameContentFromEarlierBuild(build))
        {
            viewModel.Note = "Steam updated Icarus while Starlink was open, so Starlink still sees the game's files from before the update. "
                + "Restart Starlink, then open Check ▾ › What the last game update changed… to see which mods the update affects.";
        }

        // The risk pill as it is now, with this build's game files recorded.
        await page.RateFileRisksNowAsync();
        viewModel.Load(fixedMods, CollectMods(page));
    }

    private static List<GameUpdateModInfo> CollectMods(LibraryViewModel page)
    {
        var mods = page.ModItems
            .Where(i => !i.IsMergedPack)
            .Select(i => new GameUpdateModInfo(
                i.FolderName, i.Name, IsUe4ss: false,
                ReviewCount: i.Health?.Issues.Count(issue => !issue.IsTidyOnly && !issue.IsAutomatic) ?? 0,
                i.HasUpdateAvailable, i.Version, i.LatestVersion, i.FileRisk, i.IsEnabled))
            .ToList();
        mods.AddRange(page.Ue4ssMods
            .Where(r => r.HasUpdateAvailable)
            .Select(r => new GameUpdateModInfo(r.Name, r.Name, IsUe4ss: true, 0, true, r.KnownVersion, r.LatestVersion, null, r.IsEnabled)));
        return mods;
    }

    // --- The steps (IGameUpdateSteps) ---

    public async Task<LastKnownGoodSnapshot> SnapshotAsync()
    {
        var merge = mergeInstall();
        merge.SaveWorkingState();
        var profile = merge.CurrentWorkingProfile();
        var contentPath = settingsService.Current.IcarusContentPath;
        var enabledUe4ss = contentPath is { Length: > 0 }
            ? ue4ssModStateService.GetAll(Ue4ssGamePaths.ResolveModsFolder(contentPath)).Where(m => m.IsEnabled).Select(m => m.Name).ToList()
            : [];
        var build = await detector.ReadCurrentBuildAsync();
        var snapshot = await Task.Run(() => snapshots.Create(profile, contentPath, enabledUe4ss, build, DateTimeOffset.UtcNow));
        activityLog.Log($"Saved your mod setup before updating everything ({snapshot.FolderPath}).", ActivityEntryKind.Info);
        return snapshot;
    }

    public Task RecordGameFingerprintsAsync() => library().RateFileRisksNowAsync();

    public async Task<GameUpdateStepResult> RefreshGameDataAsync()
    {
        var settings = settingsViewModel();
        while (settings.IsUpdatingDataFolder)
        {
            await Task.Delay(200);
        }

        var before = settingsService.Current.LastDataFolderUpdatedAt;
        await settings.RefreshGameDataAsync();
        return settingsService.Current.LastDataFolderUpdatedAt != before
            ? GameUpdateStepResult.Done(settings.DataFolderStatusMessage ?? "")
            : GameUpdateStepResult.Failed(settings.DataFolderStatusMessage ?? "Refresh game data didn't finish.");
    }

    public async Task CheckModsAsync()
    {
        var page = library();
        await page.WaitForModPassesAsync();
        await page.CheckModsAgainstCurrentDataAsync();
    }

    public async Task<IReadOnlyList<GameUpdateFixedMod>> FixSureProblemsAsync()
    {
        var page = library();
        await page.WaitForModPassesAsync();
        return await page.FixSureProblemsCoreAsync();
    }

    public async Task CheckForModUpdatesAsync()
    {
        var page = library();
        while (page.IsCheckingForUpdates)
        {
            await Task.Delay(200);
        }

        await page.CheckForUpdatesAsync(isAutomatic: false);
    }

    public async Task<GameUpdateStepResult> RebuildAsync()
    {
        var merge = mergeInstall();
        if (!merge.HasAnythingToBuild)
        {
            return GameUpdateStepResult.Skipped("Nothing is ticked to merge, so there's nothing to build.");
        }

        return await merge.RebuildAsync()
            ? GameUpdateStepResult.Done(merge.StatusMessage ?? "")
            : GameUpdateStepResult.Failed(merge.StatusMessage ?? "The merge couldn't be built.");
    }

    public bool IsGameRunning()
    {
        try
        {
            return gameProcessChecker.IsRunning();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<GameUpdateStepResult> InstallAsync()
    {
        var merge = mergeInstall();
        var outcome = await merge.InstallAsync();
        var message = merge.InstallStatusMessage ?? "";
        return outcome switch
        {
            MergeInstallViewModel.InstallOutcome.Installed => GameUpdateStepResult.Done(message),
            MergeInstallViewModel.InstallOutcome.NotDone => GameUpdateStepResult.Skipped(message),
            _ => GameUpdateStepResult.Failed(message),
        };
    }

    // --- The review's buttons (IGameUpdateReviewActions) ---

    public async Task OpenFixWindowAsync(string folderName)
    {
        var page = library();
        if (page.FindItem(folderName) is { } item)
        {
            await page.OpenModFixCommand.ExecuteAsync(item);
        }
    }

    public async Task GetUpdateAsync(string folderName, bool isUe4ss)
    {
        var page = library();
        if (isUe4ss)
        {
            page.Ue4ssMods.FirstOrDefault(r => string.Equals(r.Name, folderName, StringComparison.OrdinalIgnoreCase))?.OpenOnNexusCommand.Execute(null);
            return;
        }

        if (page.FindItem(folderName) is { } item)
        {
            await page.GetUpdateCommand.ExecuteAsync(item);
        }
    }

    public async Task<string> KeepOffAsync(string folderName)
    {
        var page = library();
        var merge = mergeInstall();
        if (page.FindItem(folderName) is not { } item)
        {
            return "That mod isn't in your Library any more.";
        }

        // The row's own tick: the same as unticking it in the Library (its Lua half follows).
        item.IsEnabled = false;
        if (merge.IsQueued(folderName))
        {
            return merge.StatusMessage ?? $"Couldn't turn '{item.Name}' off right now.";
        }

        if (!merge.HasAnythingToBuild)
        {
            return $"Turned '{item.Name}' off. Nothing else is ticked, so there's no merge to build: Remove from game on Merge & Install takes the old one out.";
        }

        if (IsGameRunning())
        {
            return $"Turned '{item.Name}' off. Icarus is running, so the merge wasn't built again: close the game, then Merge & Install.";
        }

        if (!await merge.RebuildAsync())
        {
            return $"Turned '{item.Name}' off, but the merge couldn't be built: {merge.StatusMessage}";
        }

        return await merge.InstallAsync() == MergeInstallViewModel.InstallOutcome.Installed
            ? $"Turned '{item.Name}' off and installed the merge without it. Tick it again once its author updates it."
            : $"Turned '{item.Name}' off and built the merge without it, but it isn't installed: {merge.InstallStatusMessage}";
    }

    public async Task<string?> RollBackAsync(LastKnownGoodSnapshot snapshot)
    {
        if (IsGameRunning())
        {
            return "Icarus is running. Close it, then Roll back.";
        }

        if (!dialogService.Confirm(
                $"Roll back puts your mod setup back as it was on {snapshot.CreatedAtUtc.ToLocalTime():g}:\n\n"
                + "• the merged pak in the game (what's there now is backed up first)\n"
                + $"• what's ticked, in what order, merge options and conflict picks{(snapshot.ProfileName is { } name ? $" (profile '{name}')" : "")}\n"
                + "• which UE4SS mods are on\n\n"
                + "The game itself can't be rolled back, only your mod setup. That merge was made for the game before its update, so some things in it may not fit. "
                + "Fixes made to mods stay (right-click a mod › Restore latest backup undoes them).\n\nRoll back?",
                "Roll back", ThemedConfirmSeverity.Warning))
        {
            return null;
        }

        var done = new List<string>();
        var problems = new List<string>();
        var contentPath = settingsService.Current.IcarusContentPath;
        var merge = mergeInstall();
        if (contentPath is { Length: > 0 })
        {
            try
            {
                await Task.Run(() => snapshots.RestoreMergedPak(snapshot, contentPath, backupDirectory));
                done.Add(snapshot.MergedFiles.Count > 0 ? "the merged pak" : "no merged pak (none was installed then)");
            }
            catch (Exception ex)
            {
                problems.Add($"the merged pak couldn't be put back ({ex.Message})");
            }
        }

        if (snapshot.Profile is { } profile)
        {
            if (merge.RestoreWorkingState(profile))
            {
                done.Add("what's ticked, the order and options");
            }
            else
            {
                problems.Add("the merge queue is busy building (try Roll back again when it's done)");
            }
        }

        if (contentPath is { Length: > 0 })
        {
            try
            {
                var modsFolder = Ue4ssGamePaths.ResolveModsFolder(contentPath);
                var wanted = snapshot.EnabledUe4ssMods.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var desired = ue4ssModStateService.GetAll(modsFolder)
                    .Where(m => m.IsEnabled != wanted.Contains(m.Name))
                    .ToDictionary(m => m.Name, m => wanted.Contains(m.Name));
                IReadOnlyList<Ue4ssModApplyFailure> failures = desired.Count == 0 ? [] : await Task.Run(() => ue4ssModStateService.Apply(modsFolder, desired, backupDirectory));
                done.Add("UE4SS mods on/off");
                problems.AddRange(failures.Select(f => $"UE4SS mod {f.Name} ({f.Reason})"));
            }
            catch (Exception ex)
            {
                problems.Add($"UE4SS mods couldn't be set ({ex.Message})");
            }
        }

        await merge.RefreshInstalledModNamesAsync();
        library().ReloadInstalledUe4ssModsCommand.Execute(null);

        var message = $"Rolled back: {string.Join(", ", done)}."
            + (problems.Count > 0 ? $" Not done: {string.Join("; ", problems)}." : "")
            + " The game itself wasn't changed.";
        activityLog.Log(message, problems.Count > 0 ? ActivityEntryKind.Warning : ActivityEntryKind.Success);
        return message;
    }
}
