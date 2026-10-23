using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.Core.GameHealth;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SettingsViewModel
{
    [ObservableProperty]
    private string? _icarusContentPath;

    [ObservableProperty]
    private string? _unrealPakExePath;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateDataFolderCommand))]
    private bool _isUpdatingDataFolder;

    /// <summary>The extraction in progress, so a second caller waits for it instead of starting another into the same folder.</summary>
    private Task _dataFolderUpdate = Task.CompletedTask;

    [ObservableProperty]
    private string? _dataFolderStatusMessage;

    /// <summary>True only right after an UpdateDataFolderAsync run that actually found real
    /// changed files — drives a "View Weekly Changes" shortcut next to the status message instead
    /// of making the user find the nav item themselves.</summary>
    [ObservableProperty]
    private bool _hasWeeklyChanges;

    /// <summary>Non-null only while a local data.pak hash mismatch has actually been detected — see CheckForGameUpdateAsync.</summary>
    [ObservableProperty]
    private string? _gameDataOutdatedMessage;

    /// <summary>Shown in the "Update data folder" Expander's collapsed header — reads straight from settings rather than caching, since the only thing that changes it (UpdateDataFolderAsync) already explicitly notifies this property when it does.</summary>
    public string DataFolderSummary => _settingsService.Current.LastDataFolderUpdatedAt is { } updatedAt
        ? $"last updated {updatedAt.ToLocalTime():yyyy-MM-dd}"
        : "never updated";

    [RelayCommand]
    private void BrowseIcarusContentFolder()
    {
        if (_dialogService.PickFolder("Select the Icarus\\Icarus\\Content folder", IcarusContentPath) is { } folder)
        {
            IcarusContentPath = folder;
        }
    }

    /// <summary>
    /// Phase 7.5: reads Steam's own install path from the registry, walks its real
    /// libraryfolders.vdf, and checks each library for Icarus's own App ID (1149460) —
    /// pre-fills the field but doesn't save on its own; the user still confirms via the
    /// existing Save button (or overrides it with Browse… first) just like a manual edit would.
    /// </summary>
    [RelayCommand]
    private void AutoDetectIcarusContentFolder()
    {
        var detected = _steamInstallLocator.FindIcarusContentPath();
        if (detected is null)
        {
            SavedMessage = "Couldn't find Icarus through Steam automatically — use Browse… instead.";
            return;
        }

        IcarusContentPath = detected;
        SavedMessage = "Found via Steam — click Save settings to keep it.";
    }

    [RelayCommand]
    private void BrowseUnrealPakExe()
    {
        if (_dialogService.PickFile("Select UnrealPak.exe", "UnrealPak.exe|UnrealPak.exe|Executable files (*.exe)|*.exe") is { } exePath)
        {
            UnrealPakExePath = exePath;
            _ = VerifyUnrealPakAsync();
        }
    }

    // --- UnrealPak locate-or-install ---

    [ObservableProperty]
    private string? _unrealPakStatusMessage;

    /// <summary>
    /// Always true (see IUnrealPakInstaller.PayloadAvailable) — kept as the binding target for the
    /// install button's IsEnabled rather than removed, so a future build that genuinely can't offer
    /// an install (e.g. a sandboxed environment with no filesystem/network access at all) has
    /// somewhere to wire that in without touching the view.
    /// </summary>
    public bool CanInstallBundledUnrealPak => _unrealPakInstaller.PayloadAvailable;

    /// <summary>Label flips to Reinstall once the installed copy is what's configured — same button, and reinstalling IS the repair path for a broken copy. Says "UnrealPak" rather than "bundled copy" since installing it may mean downloading it, not just extracting something already shipped alongside the app.</summary>
    public string InstallUnrealPakButtonLabel =>
        string.Equals(UnrealPakExePath, _unrealPakInstaller.InstalledExePath, StringComparison.OrdinalIgnoreCase)
            ? "Reinstall UnrealPak"
            : "Install UnrealPak";

    partial void OnUnrealPakExePathChanged(string? value) => OnPropertyChanged(nameof(InstallUnrealPakButtonLabel));

    /// <summary>
    /// Really runs the exe (see UnrealPakInstaller.VerifyAsync) — deliberately NOT a "check for
    /// updates" against anything newer: UnrealPak is engine-version-specific, Icarus is a UE 4.27
    /// title, and a newer UE5 UnrealPak writes pak formats the game's engine can't mount. The
    /// property that matters is "4.x and intact", which is exactly what this reports.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task VerifyUnrealPakAsync()
    {
        if (string.IsNullOrWhiteSpace(UnrealPakExePath))
        {
            UnrealPakStatusMessage = "No UnrealPak.exe set — install it, or Browse… to one you already have.";
            return;
        }

        UnrealPakStatusMessage = "Checking…";
        var result = await _unrealPakInstaller.VerifyAsync(UnrealPakExePath);
        UnrealPakStatusMessage = result.Health switch
        {
            UnrealPakHealth.Ok when result.EngineVersion is not null =>
                $"Working — UnrealPak {result.EngineVersion}"
                + (result.EngineVersion.StartsWith('4') ? " (UE4, matches Icarus)." : " — WARNING: not a UE4 build; its paks may not load in Icarus."),
            UnrealPakHealth.Ok => "Working.",
            UnrealPakHealth.Missing => $"Not found at that path. {result.Detail}",
            _ => $"Broken copy: {result.Detail} Reinstall fixes this.",
        };
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task InstallBundledUnrealPakAsync()
    {
        try
        {
            UnrealPakStatusMessage = "Installing…";
            var exePath = await _unrealPakInstaller.InstallAsync();
            UnrealPakExePath = exePath;
            Save();
            await VerifyUnrealPakAsync();
        }
        catch (Exception ex)
        {
            UnrealPakStatusMessage = $"Install failed: {ex.Message}";
        }
    }

    /// <summary>Refresh game data, for Merge &amp; Install's "Refresh game data, then build" (a no-op while one is already running).</summary>
    /// <remarks>While one is running this returns THAT run (it used to return an already-finished
    /// task, so a caller checked "is the data fresh now?" while the extraction was still going).</remarks>
    /// <remarks>Not refused while other work runs, unlike the button: its callers ARE that work
    /// (Merge &amp; Install's refresh-then-build, the game-update run).</remarks>
    public Task RefreshGameDataAsync() => IsUpdatingDataFolder ? _dataFolderUpdate : StartDataFolderUpdate();

    private bool CanUpdateDataFolder() => !IsUpdatingDataFolder;

    /// <summary>The Refresh game data button (Settings and Weekly Changes).</summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true, CanExecute = nameof(CanUpdateDataFolder))]
    private Task UpdateDataFolderAsync()
    {
        // A merge being built reads the Data folder this rewrites, and a download being imported
        // checks against it: refreshing under them mixed two game versions (review 2026-10-23).
        if (!IsUpdatingDataFolder && AppBusyCheck.DescribeRunning() is { } running)
        {
            DataFolderStatusMessage = $"Wait until {running} has finished, then click Refresh game data again.";
            return Task.CompletedTask;
        }

        return StartDataFolderUpdate();
    }

    private Task StartDataFolderUpdate()
    {
        // Two extractions into the same Data folder at once (the button, first-launch setup and
        // the game-update run can all start one) would overwrite each other's files.
        if (IsUpdatingDataFolder)
        {
            return _dataFolderUpdate;
        }

        _dataFolderUpdate = UpdateDataFolderCoreAsync();
        return _dataFolderUpdate;
    }

    private async Task UpdateDataFolderCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(IcarusContentPath) || string.IsNullOrWhiteSpace(UnrealPakExePath))
        {
            DataFolderStatusMessage = "Set both the Icarus Content folder and UnrealPak.exe path first.";
            return;
        }

        // Same paths this is about to extract with — save them now so a path typed but never
        // explicitly run through Save Settings still persists across restarts.
        Save();

        IsUpdatingDataFolder = true;
        DataFolderStatusMessage = "Extracting…";
        HasWeeklyChanges = false;

        try
        {
            var previousUpdateAt = _settingsService.Current.LastDataFolderUpdatedAt;
            var result = await _unrealPakService.ExtractDataPakAsync(UnrealPakExePath, IcarusContentPath, _dataOutputDirectory, previousUpdateAt);

            if (result.ChangeReport is { } report)
            {
                _weeklyChangeReportStore.Save(report);
            }

            // This game version joins the row history, so rows a later update removes are known as
            // the game's (an old copy a mod carries), not a mod's new content.
            try
            {
                var dataFolder = _dataOutputDirectory;
                await Task.Run(() => GameRowHistory.Record(dataFolder, GameRowHistory.LocalFileFor(dataFolder), DateTimeOffset.UtcNow));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _activityLog.Log($"Couldn't add this game version to the row history: {ex.Message}", ActivityEntryKind.Warning);
            }

            // Recorded regardless of whether a report was produced (also covers the first-ever
            // run, which has nothing to diff against yet) — this is what the *next* run's
            // previousUpdateAt, and CheckForGameUpdateAsync's own baseline, come from.
            _settingsService.Current.LastDataFolderUpdatedAt = DateTimeOffset.UtcNow;
            _settingsService.Current.LastDataPakHash = await _unrealPakService.TryGetDataPakHashAsync(IcarusContentPath);
            _settingsService.Save();
            GameDataOutdatedMessage = null;
            OnPropertyChanged(nameof(DataFolderSummary));
            WeakReferenceMessenger.Default.Send(new WeeklyChangeReportUpdatedMessage());

            DataFolderStatusMessage = $"Extracted {result.ExtractedFileCount} files. {DescribeChangeReport(result.ChangeReport)}";
            HasWeeklyChanges = result.ChangeReport is { ChangedFiles.Count: > 0 };
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else in this app: a wrong path, a UnrealPak.exe that
            // can't run, or the game having moved/renamed data.pak should show a status message,
            // not crash the app. A raw exception message alone gives a new user nothing to act on
            // when the real cause is UnrealPak.exe itself — a missing sibling DLL or antivirus
            // quarantine makes Process.Start throw a cryptic Win32 error, and a corrupted extract
            // of the bundled payload can fail with an equally unhelpful non-zero exit. Re-running
            // the same health check "Check" already offers distinguishes that case and reuses its
            // exact, actionable wording (pointing at Reinstall) instead of duplicating it here.
            var verify = await _unrealPakInstaller.VerifyAsync(UnrealPakExePath);
            DataFolderStatusMessage = verify.Health switch
            {
                UnrealPakHealth.Ok => $"Refresh failed: {ex.Message}",
                UnrealPakHealth.Missing => $"Refresh failed: {(verify.Detail ?? "UnrealPak.exe isn't at that path").TrimEnd('.')}. "
                    + "Reinstall in the UnrealPak section above.",
                _ => $"Refresh failed: your UnrealPak.exe copy looks broken ({verify.Detail?.TrimEnd('.')}). "
                    + "Reinstall in the UnrealPak section above fixes this.",
            };
        }
        finally
        {
            IsUpdatingDataFolder = false;
        }
    }

    [RelayCommand]
    private void ViewWeeklyChanges() => WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("weekly-changes"));

    private static string DescribeChangeReport(WeeklyChangeReport? report) => report switch
    {
        null => "This is your first update — nothing to compare yet.",
        { ChangedFiles.Count: 0 } => "No JSON changes since your last update.",
        var r => $"{r.ChangedFiles.Count} JSON file(s) changed since your last update — see Weekly Changes.",
    };

    /// <summary>
    /// A passive, local-only check (no network) — compares data.pak's current hash against the one
    /// recorded at the last successful Update data folder run. Only ever notifies; never
    /// re-extracts on its own. Classic IMM originally did auto-extract on this kind of detection
    /// and walked it back ("Weekly updates are no longer auto updated when you run the program. You
    /// will have to manually click the update data folder button when an update needs to be done."
    /// — from its own changelog), so this deliberately stops at telling the user, the same way
    /// classic IMM's own settled behavior does.
    /// </summary>
    private async Task CheckForGameUpdateAsync()
    {
        if (await _gameDataFreshness.IsGameDataOutdatedAsync())
        {
            GameDataOutdatedMessage = "Icarus has been updated since your last data refresh — click Refresh game data to see what changed.";
        }
    }

    /// <summary>
    /// Silent once-per-launch check, same shape as CheckForAppUpdatesOnLaunchAsync — the closest
    /// thing this app can offer to "did that merge actually work in Icarus" without guessing at
    /// DataTable-parse-error text (see GameSessionHealthCheck's own doc comment for why). Only a
    /// real crash reported AFTER the last recorded Install logs anything; nothing here ever nags on
    /// a launch with no prior install, or one where the game simply never crashed.
    /// </summary>
    private void CheckForGameCrashesSinceLastInstall()
    {
        if (_settingsService.Current.LastInstallAtUtc is not { } lastInstallAtUtc)
        {
            return;
        }

        try
        {
            var crashes = GameSessionHealthCheck.FindCrashesSince(lastInstallAtUtc);
            if (crashes.Count == 0)
            {
                return;
            }

            var mostRecent = crashes[0];
            _activityLog.Log(
                $"Icarus crashed since your last install ({mostRecent.OccurredAtUtc.ToLocalTime():g}): {mostRecent.ErrorMessage}",
                ActivityEntryKind.Warning);

            // The other half of this same health check (see GameSessionHealthCheck's own doc
            // comment): a real crash is a strong enough signal to also show what UE4SS itself was
            // last doing, in case a UE4SS mod's load failure is what actually caused it — plain log
            // text, not pattern-matched, for the same "no guessing" reason FindCrashesSince's own
            // message isn't inferred either.
            if (!string.IsNullOrWhiteSpace(IcarusContentPath))
            {
                var logTail = GameSessionHealthCheck.ReadUe4ssLogTail(IcarusContentPath);
                if (logTail.Count > 0)
                {
                    _activityLog.Log(
                        $"UE4SS.log tail at the time of that crash:\n{string.Join('\n', logTail)}",
                        ActivityEntryKind.Info);
                }
            }
        }
        catch (Exception)
        {
            // Passive background check — a locked/inaccessible crash folder is not something to
            // ever surface as an error, same "can't tell, don't nag" contract every other silent
            // launch check in this class already follows.
        }
    }
}
