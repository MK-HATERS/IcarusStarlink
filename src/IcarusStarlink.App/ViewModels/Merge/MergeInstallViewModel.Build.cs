using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.PakIO;
using IcarusStarlink.PakIO.ArcFission;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Rebuild;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Building the merged pak: the Install button's build step, the game-data guard before it, the snapshot a build is made from, and filing the built pack in the Library.</summary>
public sealed partial class MergeInstallViewModel
{
    /// <summary>
    /// While a build runs the queue, profile and merge options can't be changed (the build works from
    /// a snapshot taken when it starts, see BuildSnapshot; this keeps what's on screen matching it).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRebuilding), nameof(IsInstallIdle))]
    [NotifyCanExecuteChangedFor(nameof(CancelBuildCommand))]
    private bool _isRebuilding;

    public bool IsNotRebuilding => !IsRebuilding;

    /// <summary>Cancels the build that runs now; null when none runs. Set and cleared on the UI thread.</summary>
    private CancellationTokenSource? _buildCancellation;

    /// <summary>
    /// Cancel next to a running build's progress bar: the build stops (UnrealPak is stopped too)
    /// and nothing is installed. For a build that takes too long, or an UnrealPak that hangs.
    /// </summary>
    [RelayCommand(CanExecute = nameof(IsRebuilding))]
    private void CancelBuild() => _buildCancellation?.Cancel();

    [ObservableProperty]
    private int _rebuildProgressPercent;

    [ObservableProperty]
    private string? _rebuildStageText;

    /// <summary>A ticked mod that's in the Library, or a merge option: something for a build to make.</summary>
    internal bool HasAnythingToBuild => Queue.Any(IsBuildable) || BuildGameplayOptionsFromUi().IsAnyActive;

    /// <summary>
    /// The one visible button for turning the current queue into a real install — builds and
    /// installs in a single click, because a player who wants the mods in the game shouldn't have
    /// to press a separate build button first. Internally still two distinct
    /// steps (RebuildAsync, a safe local-only pak build; InstallAsync, the one action in this whole
    /// app that writes into the real game folder, gated behind its own explicit Yes/No confirmation)
    /// — chaining them, rather than merging their bodies into one method, keeps each independently
    /// callable/testable and means the confirmation dialog still only appears right before the
    /// actual risky write, not before the safe rebuild step that precedes it. Skips the install
    /// step (and never shows its confirmation dialog at all) if the rebuild itself failed.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task RebuildAndInstallAsync()
    {
        // Gates on RebuildAsync's own success THIS call, not merely "a pak file happens to exist
        // on disk" — a stale pak from an earlier successful build stays on disk after a later
        // Rebuild is skipped (the guard below) or fails (an exception, a missing UnrealPak.exe
        // path), and File.Exists alone can't tell "just built" apart from "leftover from before".
        // Regression: a Category-1-only option (Remove Level Cap) with an empty queue tripped the
        // guard below (it only checked Category-2's RequiredCurrentFiles), so the build silently
        // did nothing and the button went on to install the old pak with no error, not reflecting
        // what was actually configured. That guard is fixed too (see GameplayOptions.IsAnyActive), but this
        // is the deeper fix: Install must never run off a rebuild that didn't actually happen.
        //
        // Nothing ticked while Starlink's merge is in the game: the Library tells the player that
        // Merge & Install takes those mods out, so this does, with Remove from game's own question,
        // game check and backup, rather than stopping at "add a mod first".
        if (!HasAnythingToBuild && HasExistingInstall && !IsRebuilding && !IsInstalling)
        {
            var why = Queue.Count > 0
                ? "None of the ticked mods are in your Library, so there's nothing to build.\n\n"
                : "Nothing is ticked, so there's nothing to build.\n\n";
            await RunInstallStepAsync(() => RemoveFromGameCoreAsync(why));
            return;
        }

        if (await RebuildAsync())
        {
            await InstallAsync();
        }
    }

    // No [RelayCommand] — RebuildAndInstallAsync above is the only caller since the combined
    // Install button replaced the separate Rebuild/Install pair; the generated commands would be
    // dead bindings nothing in XAML references anymore.
    /// <returns>True only if this call actually produced a fresh pak — RebuildAndInstallAsync uses this, not File.Exists(_outputPakPath), to decide whether it's safe to Install (see its own comment for why that distinction is load-bearing).</returns>
    /// <remarks>Internal, not private — RebuildAndInstallAsync's own orchestration (does Install only run when Rebuild really succeeded, does it never run when Rebuild guards out or throws) is exactly the thing worth testing directly against fakes for IRebuildService/IInstallService, without ever reaching InstallAsync's own confirmation.</remarks>
    internal async Task<bool> RebuildAsync()
    {
        // Gameplay options can apply on their own, with an empty queue (merge options added to the
        // game with no mods selected) — only block if there's genuinely nothing to do at all. Queue can hold opaque pak entries too now (folded into the same
        // output by Rebuild, not field-merged), so a queue containing only those is still real work.
        // GameplayOptions.IsAnyActive, not a separately-rederived check — reads the SAME
        // classification GenerateFixedFieldChanges/RequiredCurrentFiles conceptually mirror, so
        // this guard can't miss an option the way it once did (Category 2's RequiredCurrentFiles
        // alone used to miss a Category-1-only option enabled with an empty queue, silently
        // skipping the rebuild those options needed). A greyed missing row is nothing to build.
        // Nor while an install is copying the staged pak this build would rewrite.
        if (IsRebuilding || IsInstalling)
        {
            StatusMessage = IsRebuilding
                ? "Merge & Install is already building — wait for it to finish."
                : "Merge & Install is changing the game's mods folder — wait for it to finish, then build.";
            return false;
        }

        if (!Queue.Any(IsBuildable) && !BuildGameplayOptionsFromUi().IsAnyActive)
        {
            StatusMessage = Queue.Count > 0
                ? "None of the queued mods are in your Library — add mods to the queue, or enable a gameplay option, first."
                : "Add at least one mod to the queue, or enable a gameplay option, first.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_settingsService.Current.UnrealPakExePath))
        {
            StatusMessage = "Set UnrealPak.exe path in Settings first.";
            return false;
        }

        // From here on the queue, profile and options are locked (RefuseWhileRebuilding, and the
        // page's controls), including while game data refreshes below.
        IsRebuilding = true;
        // The staged pak is about to be replaced: whatever the Library holds is of the old one.
        _builtPackInLibrary = false;
        using var cancellation = new CancellationTokenSource();
        _buildCancellation = cancellation;
        var cancel = cancellation.Token;
        try
        {
            // The merge writes whole tables from the extracted game data: built after a game update
            // but before Refresh game data, it puts the old version of every table a mod touches back
            // into the game, undoing the update there.
            if (!await EnsureGameDataCurrentAsync(cancel))
            {
                return false;
            }

            cancel.ThrowIfCancellationRequested();

            // Everything this build is made from, taken once: a queue, profile or option change made
            // while it runs (from the Library's ON box, say) can't change which mod a conflict pick
            // means, what's merged, what's logged or which profile the built pack is filed under.
            var snapshot = TakeSnapshot(forBuild: true);
            if (snapshot.Queue.Count == 0 && !snapshot.Options.IsAnyActive)
            {
                StatusMessage = "Add at least one mod to the queue, or enable a gameplay option, first.";
                return false;
            }

            // The queue saves itself into "Default" a moment after the first change
            // (MarkWorkingStateChanged); a build started before any change (or before that save)
            // still files what it built under a real, reloadable "Default" profile here, without
            // ever overriding a profile the user picked themselves. Best-effort: a failure here must
            // never block the real Rebuild the user actually asked for.
            if (SelectedProfileName is null)
            {
                try
                {
                    // Taken in the same moment as the snapshot (no await between), so it's the same state.
                    _profileStore.Save(CurrentWorkingProfile("Default"));
                    ReloadProfileNames();
                }
                catch (Exception)
                {
                }
            }

            StatusMessage = "Rebuilding…";
            RebuildProgressPercent = 0;
            RebuildStageText = null;
            Warnings.Clear();

            // A mod folder that exists on disk but can't be read never becomes a Library entry at all,
            // so it can't be queued and silently contributes nothing to the merge. Until now the only
            // hint was a line on the Library page — meaning a user could rebuild and install a pack
            // quietly missing a mod they believe is included. Surfaced here, where it actually matters.
            // Added after the Clear above, or it would be wiped before anyone saw it.
            foreach (var folderName in _libraryRepository.UnreadableFolders)
            {
                Warnings.Add(
                    $"'{folderName}' is in your mods folder but couldn't be read, so it is NOT part of this build. "
                    + "Check that mod's own files (a corrupt .EXMOD is the usual cause).");
            }

            foreach (var missing in snapshot.SkippedMissing)
            {
                Warnings.Add($"'{missing}' is in this profile but not in your Library, so it is NOT part of this build. Import it again, or remove it from the queue.");
            }

            using var perfScope = _performanceTracker.Track("Rebuild");
            // Before the mods are read, so a change made while the build runs counts as "Changed".
            var builtFrom = await FingerprintQueueAsync(snapshot.Queue);
            var loaded = await LoadQueuedPackagesAsync(snapshot.Queue);
            cancel.ThrowIfCancellationRequested();
            foreach (var gone in loaded.SkippedMissing)
            {
                Warnings.Add($"'{gone.Name}' is in this profile but not in your Library, so it is NOT part of this build. Import it again, or remove it from the queue.");
            }

            if (loaded.Entries.Count == 0 && loaded.PrebuiltPakFilePaths.Count == 0 && !snapshot.Options.IsAnyActive && snapshot.ArcFission is not { HasRows: true })
            {
                StatusMessage = "None of the queued mods are in your Library — add mods to the queue, or enable a gameplay option, first.";
                return false;
            }

            // The ArcFission mods' rows (read from their rows.lua): into the merge (which outranks
            // the data pak ArcFission Library writes), lowest priority, so any queued mod or option
            // wins a clash with them.
            var arcFissionNotes = new List<string>(snapshot.ArcFission?.Problems ?? []);
            List<ExmodPackage> lowPriority = snapshot.ArcFission is { HasRows: true } arcFission
                ? [await Task.Run(() => ArcFissionRows.ToPackage(arcFission, GetOrCreateGameTableIndex(), arcFissionNotes))]
                : [];
            if (snapshot.ArcFission is null && _settingsService.Current.IcarusContentPath is { Length: > 0 } content
                && InstallManifestNames.MisnamedArcFissionLibraryFolder(content) is { } misnamed)
            {
                arcFissionNotes.Add($"ArcFission Library is in a folder named '{misnamed}'. ArcFission mods only reach it in a folder named exactly {InstallManifestNames.ArcFissionLibraryFolder}, so their rows aren't in game or in this merge. Rename the folder in ue4ss\\Mods.");
            }

            foreach (var note in arcFissionNotes)
            {
                Warnings.Add($"ArcFission: {note}");
            }

            // Progress<T>'s own callback marshals back through the SynchronizationContext captured
            // at construction time — safe to update these bound properties directly from it since
            // this constructor runs on the UI thread (a RelayCommand invoked from a button click),
            // even though RebuildAsync's own pipeline reports from a background Task.Run.
            var progress = new Progress<RebuildStageProgress>(p =>
            {
                RebuildStageText = p.Stage;
                RebuildProgressPercent = p.PercentComplete;
            });

            var (picks, unmatchedPicks) = await ResolveWinnerPicksAsync(snapshot, loaded);
            cancel.ThrowIfCancellationRequested();
            // A Find the broken mod round leaves mods out, so a winner whose partner is out can't match.
            if (unmatchedPicks > 0 && _finderOnly is null)
            {
                Warnings.Add(
                    $"{unmatchedPicks} saved conflict pick(s) no longer match what the queued mods change (a mod was updated or reordered into agreement), so they weren't used. "
                    + "Check the merge to pick again.");
            }

            // Advanced log: exactly what this build was made from.
            Serilog.Log.Debug("Merge & Install build: queue (lowest priority first) {Queue}; prebuilt paks {Paks} at {Positions}; ArcFission/low-priority {Low}; {Picks} conflict pick(s) {PickList}; options {Options}",
                string.Join(" > ", snapshot.Queue.Select(e => e.Name)), loaded.PrebuiltPakFilePaths.Select(Path.GetFileName), loaded.PrebuiltPakQueuePositions,
                lowPriority.Select(p => $"{p.Name} ({p.Rows.Sum(r => r.FileItems.Count)} rows)"),
                picks?.Count ?? 0, picks?.Select(p => $"{p.Key.CurrentFile}/{p.Key.ItemName}.{p.Key.FieldName}=#{p.Value}"), snapshot.OptionsSummary);
            var result = await _rebuildService.RebuildAsync(
                loaded.Packages, snapshot.Options, _dataFolder, snapshot.UnrealPakExePath, _outputPakPath, loaded.PrebuiltPakFilePaths, picks, progress,
                cancellationToken: cancel, lowPriorityPackages: lowPriority, prebuiltPakQueuePositions: loaded.PrebuiltPakQueuePositions);
            // Cancel pressed as the build finished: still nothing goes in the game.
            cancel.ThrowIfCancellationRequested();

            var foldedInCount = loaded.PrebuiltPakFilePaths.Count;
            var prebuiltNote = foldedInCount > 0 ? $", {foldedInCount} prebuilt pak(s) folded in" : "";
            StatusMessage = $"Built '{result.OutputPakPath}' — {result.PackedFileCount} files packed, "
                + $"{result.MergedFileCount} data table(s) merged{prebuiltNote}.";
            foreach (var warning in result.Warnings)
            {
                Warnings.Add(warning);
            }

            // Notes go in the same list, prefixed rather than styled separately: they're read at
            // the same moment for the same reason ("did anything about this build need my
            // attention?"), and the prefix keeps "this is fine" distinguishable from "this failed".
            foreach (var note in result.Notes)
            {
                Warnings.Add($"Note: {note}");
            }

            CanCopyToGame = true;
            await RecordBuildAsync(builtFrom);

            // The Server page's "Install my merged pak" shows the new build without a page switch.
            _messenger.Send(new MergedPakBuiltMessage());

            // The import is bookkeeping AFTER a verified pak exists, so its own failure (a locked
            // ISL-Merged folder, antivirus) is a warning, never "Rebuild failed" with Install
            // withheld (Install tries it again). A Find the broken mod round's pak (part of the
            // queue) isn't the player's build: the Library keeps their own built pack.
            _stagedBuildProfileName = snapshot.ProfileName;
            _stagedIsFinderRound = _finderOnly is not null;
            if (!_stagedIsFinderRound)
            {
                try
                {
                    await ImportBuiltPakIntoLibraryAsync(snapshot.ProfileName);
                }
                catch (Exception ex)
                {
                    Warnings.Add($"The built pack couldn't be added to your Library ({ex.Message}) — the pak itself is fine and can still be installed.");
                }
            }

            var pickNote = picks is { Count: > 0 } ? $", {picks.Count} conflict(s) won by your pick" : "";
            _activityLog.Log($"Rebuilt pack with {snapshot.Queue.Count} mod(s) — {result.PackedFileCount} files packed{pickNote}.", ActivityEntryKind.Success);
            return true;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            StatusMessage = "Build cancelled. Nothing was installed, and the game still has what it had.";
            RebuildStageText = null;
            _activityLog.Log("Build cancelled.", ActivityEntryKind.Info);
            // What's in Staged_Build now may be half made: Copy to game doesn't offer it (as after a failed build).
            CanCopyToGame = false;
            return false;
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else in this app: a missing base data file, a
            // UnrealPak failure, or a malformed queued mod should show a status message, not
            // crash the app.
            StatusMessage = $"Rebuild failed: {ex.Message}";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
            // Whatever is (or isn't) at _outputPakPath now is not a build the user asked for — the
            // standalone Copy to game must not offer it (it used to install a stale or
            // partial pak after a failed rebuild).
            CanCopyToGame = false;
            return false;
        }
        finally
        {
            _buildCancellation = null;
            IsRebuilding = false;
        }
    }

    private string BuildRecordPath => Path.Combine(Path.GetDirectoryName(_outputPakPath)!, MergedModFingerprints.FileName);

    /// <summary>
    /// Each queued mod's version and a hash of its files, taken BEFORE the build reads them: a mod
    /// saved in the editor or updated while a long build runs then reads "Changed" afterwards
    /// (the pak has the copy from before), never "In game". Null when they can't be read (the
    /// build goes on without the hint). A mod whose folder is gone is left out, as the build leaves it.
    /// </summary>
    private async Task<IReadOnlyList<MergedModFingerprint>?> FingerprintQueueAsync(IReadOnlyList<LibraryEntry> queue)
    {
        try
        {
            var mods = new List<(string FolderName, string Version, string Folder)>();
            foreach (var entry in queue)
            {
                try
                {
                    mods.Add((entry.FolderName, entry.Version, _libraryRepository.GetFolderPath(entry.FolderName)));
                }
                catch (DirectoryNotFoundException)
                {
                }
            }

            return await Task.Run(() => (IReadOnlyList<MergedModFingerprint>)
                [.. mods.Select(m => new MergedModFingerprint(m.FolderName, m.Version, MergedModFingerprints.HashFolder(m.Folder)))]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Couldn't fingerprint the queued mods before the build");
            return null;
        }
    }

    /// <summary>
    /// What this build was made from (each mod's version and a hash of its files, taken before the
    /// build: FingerprintQueueAsync), keyed by the pak's hash, so the Library can later say which
    /// installed mods changed since (ChangedSinceInstall). Best-effort: a failure only loses that hint.
    /// </summary>
    private async Task RecordBuildAsync(IReadOnlyList<MergedModFingerprint>? builtFrom)
    {
        if (builtFrom is null)
        {
            return;
        }

        var pak = _outputPakPath;
        var recordPath = BuildRecordPath;
        try
        {
            await Task.Run(() => MergedModFingerprints.Add(recordPath, new MergedBuildRecord(
                MergedModFingerprints.HashFile(pak), DateTimeOffset.UtcNow, builtFrom)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Serilog.Log.Warning(ex, "Couldn't record what the merge was built from");
        }
    }

    /// <summary>True while a build runs the Refresh game data it asked for (EnsureGameDataCurrentAsync).</summary>
    private bool _refreshingGameDataForBuild;

    /// <summary>
    /// What this page has running that must not be cut off or have the game data changed under it:
    /// the answer to BackgroundWorkRequestMessage (AppBusyCheck), which the in-app update, the
    /// window's close prompt and Refresh game data ask. A build reads the extracted game data
    /// throughout, so a Refresh game data started meanwhile (Settings, or Weekly Changes' button)
    /// could swap tables out from under it.
    /// </summary>
    internal IEnumerable<string> DescribeWorkInProgress()
    {
        if (IsRebuilding && !_refreshingGameDataForBuild)
        {
            yield return "a merge is being built";
        }

        if (IsInstalling)
        {
            yield return "a merge being installed";
        }
    }

    /// <summary>
    /// The game-update guard before a build: when Icarus was updated since the last Refresh game
    /// data, asks to refresh first (Yes runs it, then the build goes on with the fresh data) and
    /// stops with the reason otherwise. True when the build can go ahead.
    /// </summary>
    private async Task<bool> EnsureGameDataCurrentAsync(CancellationToken cancellationToken)
    {
        if (_gameDataFreshness is null)
        {
            return true;
        }

        bool outdated;
        try
        {
            outdated = await _gameDataFreshness.IsGameDataOutdatedAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Can't tell: not a reason to refuse a build.
            return true;
        }

        if (!outdated)
        {
            return true;
        }

        var refresh = Confirm(
            "Icarus has been updated since your last Refresh game data.\n\n"
            + "Building now would put the old version of every game table your mods change back into the game, undoing the update there.\n\n"
            + "Refresh game data now, then build?",
            "Game data is out of date");
        if (!refresh)
        {
            StatusMessage = "Not built: Icarus was updated since your last Refresh game data. Refresh game data (Settings), then Install again.";
            return false;
        }

        StatusMessage = "Refreshing game data…";
        RebuildStageText = "Refreshing game data…";
        bool refreshed;
        // Refresh game data refuses while a build is reported running (DescribeWorkInProgress): this
        // one is the build's own, so the build isn't reported until it's done.
        _refreshingGameDataForBuild = true;
        try
        {
            refreshed = await _gameDataFreshness.RefreshGameDataAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Not built: Refresh game data failed ({ex.Message}).";
            return false;
        }
        finally
        {
            _refreshingGameDataForBuild = false;
        }

        if (!refreshed)
        {
            StatusMessage = "Not built: Refresh game data didn't finish — see Settings for why, then Install again.";
            return false;
        }

        _activityLog.Log("Refreshed game data before building (Icarus had been updated).", ActivityEntryKind.Info);
        return true;
    }

    /// <summary>
    /// Everything one build or conflict check is made from, taken at once from the page: the queue
    /// in order (only rows that can build — no greyed missing rows, no ISL-built packs), the options,
    /// the conflict winners, the ArcFission mods' rows and the profile name.
    /// </summary>
    internal sealed record BuildSnapshot(
        IReadOnlyList<LibraryEntry> Queue,
        IReadOnlyList<string> SkippedMissing,
        GameplayOptions Options,
        string OptionsSummary,
        IReadOnlyDictionary<string, string> Winners,
        ArcFissionScan? ArcFission,
        string ProfileName,
        string UnrealPakExePath);

    private bool IsBuildable(LibraryEntry entry) => !entry.IsMissingFromLibrary && !IsIslBuiltPack(entry);

    /// <param name="forBuild">
    /// The build's own snapshot: a Find the broken mod round builds only part of the queue
    /// (FindBroken.cs). Everything else (the badge recounted during a round, Check the merge) sees
    /// the whole queue, which stays as it is.
    /// </param>
    private BuildSnapshot TakeSnapshot(bool forBuild = false) => new(
        [.. Queue.Where(IsBuildable).Where(e => !forBuild || _finderOnly is null || _finderOnly.Contains(e.FolderName))],
        [.. Queue.Where(e => e.IsMissingFromLibrary).Select(e => e.Name)],
        BuildGameplayOptionsFromUi(),
        ActiveOptionsSummary,
        new Dictionary<string, string>(_winners, StringComparer.OrdinalIgnoreCase),
        ScanArcFission(),
        SelectedProfileName ?? "Default",
        _settingsService.Current.UnrealPakExePath ?? "");

    /// <summary>The profile the staged pak was built from (this session); null when it was built before the app started.</summary>
    private string? _stagedBuildProfileName;

    /// <summary>Whether the staged pak is already the Library's built pack (the build imported it, or an install after it did).</summary>
    private bool _builtPackInLibrary;

    /// <summary>The staged pak is a Find the broken mod round's (part of the queue): it never goes in the Library.</summary>
    private bool _stagedIsFinderRound;

    /// <summary>
    /// True while this page sends LibraryChangedMessage for its own built pack landing in the
    /// Library: that changes no queued mod, pick or check, so this page's own handler keeps its
    /// caches and conflict line (the other pages still reload on the message).
    /// </summary>
    private bool _sendingOwnBuiltPackChange;

    /// <summary>
    /// Imports the just-built pak into the Library, so a build is an entry there the moment it
    /// exists, installed or not. Off the UI thread: it deletes the previous built pack and copies
    /// the merged pak (100+ MB). The repository is safe to read meanwhile, and this page refuses
    /// its own edits while it builds or installs.
    ///
    /// "Replace, not accumulate": ImportPak would otherwise derive a fresh "_2"/"_3"-suffixed folder
    /// name every time (its own collision-avoidance rule), leaving one stale Library entry behind
    /// per build instead of one that stays current.
    /// </summary>
    /// <param name="profileName">
    /// "Default" when none was selected (RebuildAsync's own auto-save uses the same effective name,
    /// without changing SelectedProfileName itself); for a build, the profile the build started
    /// from, not whatever is selected by the time it finishes.
    /// </param>
    private async Task ImportBuiltPakIntoLibraryAsync(string profileName)
    {
        var outputPak = _outputPakPath;
        await Task.Run(() =>
        {
            // The built pack under the merged pak's other names is replaced too.
            foreach (var legacy in InstallManifestNames.AllMergedPakFileNames.Select(Path.GetFileNameWithoutExtension))
            {
                if (_libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, legacy, StringComparison.OrdinalIgnoreCase)) is { IsOpaquePak: true } old
                    && old.MergedPackModNames is not null)
                {
                    _libraryRepository.Delete(old.FolderName);
                }
            }

            var builtFolderName = Path.GetFileNameWithoutExtension(outputPak);
            if (_libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, builtFolderName, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                _libraryRepository.Delete(existing.FolderName);
            }

            _libraryRepository.ImportPak(outputPak, mergedPackProfileName: profileName);
        });

        _builtPackInLibrary = true;
        _sendingOwnBuiltPackChange = true;
        try
        {
            _messenger.Send(new LibraryChangedMessage());
        }
        finally
        {
            _sendingOwnBuiltPackChange = false;
        }
    }

    /// <summary>
    /// The Library's built pack is a copy of the staged pak (same size and time written; the
    /// import's File.Copy keeps the time), so there's nothing to import again (after a restart
    /// _builtPackInLibrary starts false).
    /// </summary>
    private bool LibraryHasThisBuild()
    {
        var folder = Path.GetFileNameWithoutExtension(_outputPakPath);
        if (_libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, folder, StringComparison.OrdinalIgnoreCase)) is not { MergedPackModNames: not null } built)
        {
            return false;
        }

        try
        {
            var copy = new FileInfo(Path.Combine(_libraryRepository.GetFolderPath(built.FolderName), Path.GetFileName(_outputPakPath)));
            var staged = new FileInfo(_outputPakPath);
            return copy.Exists && staged.Exists && copy.Length == staged.Length && copy.LastWriteTimeUtc == staged.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The profile the Library's built pack was filed under (its Author), when there is one.</summary>
    private string? BuiltPackProfileInLibrary()
    {
        var builtFolderName = Path.GetFileNameWithoutExtension(_outputPakPath);
        return _libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, builtFolderName, StringComparison.OrdinalIgnoreCase)) is { MergedPackModNames: not null } built
            ? built.Author
            : null;
    }
}
