using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Checking and fixing mods against the game data, the risk pill, and converting prebuilt paks.</summary>
public sealed partial class LibraryViewModel
{
    /// <summary>
    /// Manually converts an already-imported opaque .pak entry into a real, editable EXMOD, in
    /// place — the same conversion new imports now attempt automatically (IPrebuiltPakToExmodConverter),
    /// just run on demand for one of the entries that predates that feature, or that stayed opaque
    /// because conversion wasn't possible at import time (e.g. UnrealPak.exe wasn't set up yet).
    /// Backs up the mod's folder first (FolderBackup, same as "Create mod backup") since this is a
    /// real, destructive rewrite — the source .pak is deleted once its content is fully represented
    /// as EXMOD rows/assets — and restores that backup if anything after the delete throws, so a
    /// failure never leaves the mod in a half-converted, unreadable state.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ConvertToExmodAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!item.IsOpaquePak)
        {
            StatusMessage = $"'{item.Name}' is already a real, editable mod — nothing to convert.";
            return;
        }

        if (!_foldersCurrentlyConvertingToExmod.Add(item.FolderName))
        {
            // A per-folder guard, not a page-wide one — converting Mod A must never silently
            // swallow a concurrent request to convert unrelated Mod B.
            return;
        }

        try
        {
            var (converted, message) = await TryConvertOneToExmodAsync(item);
            StatusMessage = message;
            if (converted)
            {
                _activityLog.Log($"Converted '{item.Name}' from a prebuilt pak into an editable mod.", ActivityEntryKind.Success);
            }
        }
        catch (Exception ex)
        {
            // Same UI boundary the whole-library sweep already has (an audit finding: without it a
            // locked .pak sent the exception to the crash-report handler and the click did nothing).
            StatusMessage = $"Couldn't convert '{item.Name}': {ex.Message}";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
        }
        finally
        {
            _foldersCurrentlyConvertingToExmod.Remove(item.FolderName);
        }
    }

    /// <summary>
    /// The actual convert-one-mod logic, shared by the single-item command above and the
    /// whole-library sweep below — everything except the per-folder reentrancy guard and the
    /// Activity log entry, which each caller handles its own way (the sweep logs one summary line
    /// instead of one per mod).
    /// </summary>
    private async Task<(bool Converted, string Message)> TryConvertOneToExmodAsync(LibraryItemViewModel item)
    {
        if (item.IsPakBundle)
        {
            return (false, $"'{item.Name}' is a bundle of {item.PakCount} paks (one download) — it stays as paks; every one of them is folded into the merge as-is.");
        }

        var folder = _repository.GetFolderPath(item.FolderName);
        var pakFilePath = Directory.GetFiles(folder, "*.pak", SearchOption.AllDirectories).FirstOrDefault();
        if (pakFilePath is null)
        {
            return (false, $"'{item.Name}' has no .pak file to convert.");
        }

        var report = new MergeReport();
        var history = await Task.Run(FixService.History);
        var converted = await _prebuiltPakToExmodConverter.TryConvertAsync(
            pakFilePath, _dataFolder, _settingsService.Current.UnrealPakExePath ?? "", item.Name, item.Author, report, history);

        if (converted is null)
        {
            return report.Warnings.Count > 0
                ? (false, $"Couldn't convert '{item.Name}': {report.Warnings[0]}")
                : (false, $"Couldn't convert '{item.Name}' to an editable mod right now.");
        }

        _repository.BackupMod(item.FolderName);
        try
        {
            // Kept, not discarded — see IPrebuiltPakSourceStore's own doc comment: a later game
            // update can re-derive a fresher diff from this exact same original content instead of
            // this conversion being frozen forever at today's game data.
            _prebuiltPakSourceStore.Save(item.FolderName, pakFilePath);
            File.Delete(pakFilePath);
            ExmodFolder.Write(folder, converted.Contents);
        }
        catch
        {
            // The delete+write above should be near-instant on a local disk, but a failure
            // partway through (disk full, permission revoked mid-operation) must not leave the
            // mod folder in a state with neither a valid .pak nor a valid .EXMOD — the backup
            // just taken makes that recoverable instead of a real, permanent loss.
            _repository.RestoreLatestModBackup(item.FolderName);
            throw;
        }

        _repository.Refresh();
        // Only marked when the conversion actually produced placeholder metadata — see
        // IPrebuiltPakToExmodConverter's own doc comment on HasAuthorDeclaredMetadata for why
        // marking a real-author-declared conversion here would risk a later Nexus link silently
        // overwriting the name the author already got right.
        if (!converted.HasAuthorDeclaredMetadata)
        {
            _repository.MarkConvertedFromPrebuiltPak(item.FolderName);
        }
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        return (true, $"Converted '{item.Name}' to a real, editable mod — a backup of the original was kept.");
    }

    /// <summary>
    /// Sweeps every currently-opaque, non-merged-pack entry and retries conversion now — for mods
    /// that stayed opaque because UnrealPak.exe/the extracted Data folder weren't ready yet at
    /// import time (every real import path already tries conversion automatically; this is just a
    /// deliberate, explicit retry once those prerequisites exist, not a silent background pass —
    /// this still deletes each mod's own source .pak, the same real, destructive rewrite the
    /// per-mod action already is). A merged pack (IsMergedPack) is never a candidate — it's a
    /// computed aggregate of other mods, not an authored one, and converting it would produce a
    /// meaningless giant diff (see MergeInstallViewModel's own reimport, which deliberately never
    /// routes through this either).
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ConvertAllOpaqueModsAsync()
    {
        if (IsConvertingAllOpaqueMods)
        {
            return;
        }

        var candidates = AllLibraryItems().Where(i => i.IsOpaquePak && !i.IsMergedPack && !i.IsPakBundle).ToList();
        if (candidates.Count == 0)
        {
            StatusMessage = "No prebuilt paks left to make editable.";
            return;
        }

        IsConvertingAllOpaqueMods = true;
        try
        {
            var convertedCount = 0;
            var stillOpaqueCount = 0;
            foreach (var item in candidates)
            {
                if (!_foldersCurrentlyConvertingToExmod.Add(item.FolderName))
                {
                    continue;
                }

                try
                {
                    var (converted, message) = await TryConvertOneToExmodAsync(item);
                    if (converted)
                    {
                        convertedCount++;
                        _activityLog.Log($"Converted '{item.Name}' from a prebuilt pak into an editable mod.", ActivityEntryKind.Success);
                    }
                    else
                    {
                        stillOpaqueCount++;
                        _activityLog.Log(message, ActivityEntryKind.Info);
                    }
                }
                catch (Exception ex)
                {
                    // One mod's own I/O failure (disk full, a locked file) shouldn't abandon the
                    // rest of the sweep — matching CheckModsAgainstCurrentDataAsync's own per-mod
                    // isolation.
                    stillOpaqueCount++;
                    _activityLog.Log($"Couldn't convert '{item.Name}': {ex.Message}", ActivityEntryKind.Warning);
                }
                finally
                {
                    _foldersCurrentlyConvertingToExmod.Remove(item.FolderName);
                }
            }

            StatusMessage = stillOpaqueCount > 0
                ? $"Made {convertedCount} of {candidates.Count} prebuilt pak(s) editable — {stillOpaqueCount} couldn't convert right now (see Activity log)."
                : $"Made all {convertedCount} prebuilt pak(s) editable.";
        }
        finally
        {
            IsConvertingAllOpaqueMods = false;
        }
    }

    /// <summary>
    /// Runs silently after every successful "Update data folder" (see the WeeklyChangeReportUpdatedMessage
    /// registration above) — not a manual action, since it's meant to keep every convertible/converted
    /// mod current with whatever the game now looks like, without anyone having to remember to ask.
    /// Two things happen, neither ever touching a hand-edited mod:
    ///
    /// 1. Any mod still opaque (never successfully converted — UnrealPak.exe or the Data folder
    ///    likely wasn't ready yet at import time) gets the exact same conversion attempt "Convert
    ///    opaque mods now" makes, using whatever .pak already sits in its own folder.
    /// 2. Any mod already converted from a prebuilt pak (ConvertedFromPrebuiltPak) that hasn't been
    ///    opened and saved in the editor since (IsLocallyEdited) gets re-derived fresh from its own
    ///    saved source pak (IPrebuiltPakSourceStore) against the just-updated game data — a
    ///    conversion's field diff is only ever as fresh as the base data available the moment it
    ///    ran, so without this a mod converted before a patch stays frozen against pre-patch data
    ///    forever. The moment a mod is hand-edited it "graduates" and is never silently touched
    ///    here again — an edit that took real thought must never be silently replaced by a fresh
    ///    mechanical re-diff.
    ///
    /// Never surfaces a blocking dialog or a status message on its own (this can fire while the
    /// user is looking at an entirely different page) — results land in the Activity log instead,
    /// the same "silent unless something happened" convention the rest of this app already uses
    /// for background passes.
    /// </summary>
    private async Task RefreshConvertedOpaqueModsAsync()
    {
        // It rewrites mods outside the fix service's lock, so never alongside a check or Fix sure
        // problems pass (they write mods too): one already running gets this refresh right after.
        if (IsCheckingStaleness)
        {
            _refreshConvertedPending = true;
            return;
        }

        IsCheckingStaleness = true;
        try
        {
            await RefreshConvertedOpaqueModsCoreAsync();
        }
        finally
        {
            IsCheckingStaleness = false;
            RunPendingRefresh();
        }
    }

    /// <summary>A game data refresh arrived while a check or fix pass ran: the refresh it asked for, once that pass is done.</summary>
    private bool _refreshConvertedPending;

    private void RunPendingRefresh()
    {
        if (_refreshConvertedPending)
        {
            _refreshConvertedPending = false;
            _ = RefreshConvertedOpaqueModsAsync();
        }
    }

    /// <summary>The tables, old values and Library-added rows one pass of checks shares (each Check would otherwise read them again).</summary>
    private sealed record CheckPass(Dictionary<string, JsonObject?> Tables, GameValueHistory History, IReadOnlySet<string> AddedRows);

    private async Task RefreshConvertedOpaqueModsCoreAsync()
    {
        CheckPass? pass = null;
        var stillOpaque = AllLibraryItems().Where(i => i.IsOpaquePak && !i.IsMergedPack && !i.IsPakBundle).ToList();
        var alreadyConverted = AllLibraryItems()
            .Where(i => i.ConvertedFromPrebuiltPak && !i.IsLocallyEdited && !i.IsOpaquePak)
            .ToList();

        var convertedCount = 0;
        var refreshedCount = 0;

        foreach (var item in stillOpaque)
        {
            if (!_foldersCurrentlyConvertingToExmod.Add(item.FolderName))
            {
                continue;
            }

            try
            {
                var (converted, _) = await TryConvertOneToExmodAsync(item);
                if (converted)
                {
                    convertedCount++;
                }
            }
            catch (Exception ex)
            {
                _activityLog.Log($"Couldn't convert '{item.Name}' after updating game data: {ex.Message}", ActivityEntryKind.Warning);
            }
            finally
            {
                _foldersCurrentlyConvertingToExmod.Remove(item.FolderName);
            }
        }

        // Same open-editor guard CheckModsAgainstCurrentDataAsync uses (an audit finding): rewriting
        // a folder under an open editor window means its next Save writes the stale in-memory
        // package back over the fresh diff — and marks it locally edited, so it's never refreshed again.
        var foldersWithOpenEditors = _openEditorCountByFolder.Where(kv => kv.Value > 0).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in alreadyConverted)
        {
            var sourcePakPath = _prebuiltPakSourceStore.TryGetPath(item.FolderName);
            if (sourcePakPath is null || foldersWithOpenEditors.Contains(item.FolderName) || !_foldersCurrentlyConvertingToExmod.Add(item.FolderName))
            {
                continue;
            }

            try
            {
                pass ??= await Task.Run(() => new CheckPass(new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase), FixService.History(), FixService.LibraryAddedRows()));
                var report = new MergeReport();
                var converted = await _prebuiltPakToExmodConverter.TryConvertAsync(
                    sourcePakPath, _dataFolder, _settingsService.Current.UnrealPakExePath ?? "", item.Name, item.Author, report, pass.History);
                if (converted is null)
                {
                    continue;
                }

                var folder = _repository.GetFolderPath(item.FolderName);

                // The converter derives FileName from the SOURCE pak's file name, and the source store
                // keeps that pak as "{folderName}.pak" — so for a collision-suffixed folder
                // ("CoolMod_P_2" holding "CoolMod_P.EXMOD") the re-conversion wrote a SECOND .EXMOD
                // beside the original, and the mod vanished from the Library as "more than one
                // .EXMOD" on the next rescan (an audit finding). Keep the existing package's own name.
                converted.Contents.Package.FileName = ExmodFolder.ReadPackageOnly(folder).FileName;

                var folderName = item.FolderName;
                _repository.BackupMod(item.FolderName);
                try
                {
                    // This entry is already ConvertedFromPrebuiltPak=true (it's re-converting one
                    // of `alreadyConverted`) — deliberately NOT reconsidering that flag even if
                    // this fresh conversion now finds a bundled EXMOD with real author metadata:
                    // a prior Nexus/Database link may have already overwritten Name/Author once,
                    // and un-marking here wouldn't undo that. Left as a known, narrow residual gap
                    // rather than guessed at.
                    ExmodFolder.Write(folder, converted.Contents);
                }
                catch
                {
                    _repository.RestoreLatestModBackup(item.FolderName);
                    throw;
                }

                _repository.Refresh();
                refreshedCount++;

                // Exactly the fixes made to this mod before (in the fix window or by Fix sure
                // problems), made again on the fresh conversion; nothing it wasn't asked to.
                // Off the UI thread: the service is shared with any check or fix pass and waits its turn.
                var reapplied = await Task.Run(() => FixService.ReapplyRemembered(folderName, pass.Tables, pass.History, pass.AddedRows));
                if (reapplied > 0)
                {
                    _activityLog.Log($"Made {reapplied} earlier fix(es) again in '{item.Name}' after refreshing it.", ActivityEntryKind.Info);
                }

                // The fresh conversion (and those fixes) can change what the row's badge says.
                await RefreshHealthAsync(item, pass);
            }
            catch (Exception ex)
            {
                _activityLog.Log($"Couldn't refresh '{item.Name}' against updated game data: {ex.Message}", ActivityEntryKind.Warning);
            }
            finally
            {
                _foldersCurrentlyConvertingToExmod.Remove(item.FolderName);
            }
        }

        // 3. Mods built from scale rules (the editor's Scale rules view, the Quality of life
        // template) rebuild their rule rows from the new game data — the whole point of keeping
        // rules instead of frozen copies. Rules own only their own fields, so hand edits stay; a mod
        // open in the editor is left for its own Apply/Save, same guard as above.
        var rulesRefreshedCount = 0;
        var gameTables = new GameTableIndex(_dataFolder);
        foreach (var item in AllLibraryItems().Where(i => !i.IsOpaquePak && !foldersWithOpenEditors.Contains(i.FolderName)))
        {
            try
            {
                var folder = _repository.GetFolderPath(item.FolderName);
                var package = ExmodFolder.Read(folder).Package;
                if (package.ScaleRules.Count == 0)
                {
                    continue;
                }

                var before = ExmodJson.Serialize(package, indented: false);
                ScaleRules.ApplyToPackage(package, package.ScaleRules, _dataFolder, new MergeReport(), gameTables);
                if (ExmodJson.Serialize(package, indented: false) == before)
                {
                    continue;
                }

                ExmodFolder.Write(folder, new ExmodPackageContents(package, []));
                rulesRefreshedCount++;
            }
            catch (Exception ex)
            {
                _activityLog.Log($"Couldn't rebuild '{item.Name}' from its scale rules after updating game data: {ex.Message}", ActivityEntryKind.Warning);
            }
        }

        if (rulesRefreshedCount > 0)
        {
            _repository.Refresh();
        }

        if (convertedCount > 0 || refreshedCount > 0 || rulesRefreshedCount > 0)
        {
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            var parts = new List<string>();
            if (convertedCount > 0)
            {
                parts.Add($"converted {convertedCount} newly-convertible mod(s)");
            }

            if (refreshedCount > 0)
            {
                parts.Add($"refreshed {refreshedCount} mod(s) against the updated game data");
            }

            if (rulesRefreshedCount > 0)
            {
                parts.Add($"rebuilt {rulesRefreshedCount} mod(s) from their scale rules");
            }

            _activityLog.Log($"After updating game data: {string.Join(", ", parts)}.", ActivityEntryKind.Success);
        }
    }

    /// <summary>The game's recorded changes from recent Refresh game data runs; set by the app (old values a mod may still hold).</summary>
    public Func<IEnumerable<FieldChange>>? GameHistory { get; init; }

    private ModFixService? _modFixService;

    /// <summary>The app's one fix service (shared with the replacement that re-applies fixes after an update); set by the app (null: made here on first use).</summary>
    public ModFixService? SharedFixService
    {
        init => _modFixService = value;
    }

    /// <summary>Where each mod's remembered fix choices are kept; set by the app (null: nothing remembered).</summary>
    public string? FixDecisionsDirectory { get; init; }

    /// <summary>The game's own content, to tell a mod's copy of a game file from a file of its own; set by the app (null: not checked).</summary>
    public IBaseGameContentProvider? BaseGameContent { get; init; }

    private bool _fileRiskPassRunning;
    private bool _fileRiskPassAgain;

    /// <summary>The game build the first risk pass this session read (the one the game's mounted content is).</summary>
    private string? _sessionGameBuild;

    /// <summary>
    /// Rates the game files every mod replaces (the risk pill) and finds mods replacing the same
    /// file with different copies. In the background after the Library loads and after mods
    /// change; the first call mounts the game's content (about a second), every later one is
    /// quick. A pass asked for while one runs runs once more after it.
    /// </summary>
    private async Task RefreshFileRisksAsync()
    {
        if (BaseGameContent is not { } game)
        {
            return;
        }

        if (_fileRiskPassRunning)
        {
            _fileRiskPassAgain = true;
            return;
        }

        _fileRiskPassRunning = true;
        try
        {
            do
            {
                _fileRiskPassAgain = false;
                // Caught per round: a failed round must not drop the re-run asked for meanwhile.
                try
                {
                    await RateFileRisksOnceAsync(game);
                }
                catch (Exception ex)
                {
                    _activityLog.Log($"Couldn't rate the game files mods replace: {ex.Message}", ActivityEntryKind.Warning);
                }
            }
            while (_fileRiskPassAgain);
        }
        finally
        {
            _fileRiskPassRunning = false;
        }
    }

    /// <summary>One round of RefreshFileRisksAsync.</summary>
    private async Task RateFileRisksOnceAsync(IBaseGameContentProvider game)
    {
        var items = AllLibraryItems().Where(i => !i.IsMergedPack).ToList();
        var folders = items.Select(i => (i.FolderName, i.Name)).ToList();
        // The build on disk: the game's own copies of the files mods replace are recorded
        // for it, so what a later game update changes in them is known.
        var build = GameUpdates is { } updates ? await updates.ReadCurrentBuildAsync() : null;
        // The game's content is read once per app session: after Steam updated the game
        // while the app ran, what's read is still the build from before, so nothing is
        // recorded (or said) until the next start.
        _sessionGameBuild ??= build;
        var fingerprints = build is not null && string.Equals(build, _sessionGameBuild, StringComparison.OrdinalIgnoreCase) ? GameFileFingerprints : null;
        var risks = await Task.Run(() =>
        {
            var rated = FileRiskAssessor.Assess(_repository, folders, game, fingerprints, build);
            try
            {
                fingerprints?.SaveIfChanged();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Recorded again on the next pass.
            }

            return rated;
        });
        foreach (var item in items)
        {
            item.FileRisk = risks.GetValueOrDefault(item.FolderName);
        }

        // The Problems chip counts high-risk rows, and with it on, its list shows them.
        RefreshFilters();
    }

    private ModFixService FixService => _modFixService ??= ModFixService.Registered(new ModFixService(_repository, _dataFolder, GameHistory, FixDecisionsDirectory,
        BaseGameContent as IGameAssetCatalog));

    /// <summary>
    /// The row's warning badge, and right-click › Check against the game &amp; fix…: the fix window,
    /// listing what in this mod doesn't fit the current game with a fix for each. Checks the mod
    /// now when no whole-library check has yet. Not while a whole-library check or Fix sure
    /// problems is running (they write mods too), and not while the mod is open in the editor:
    /// the fix window is modal, so the editor couldn't be closed from behind it.
    /// </summary>
    /// <summary>Whether the extracted game data folder has any data in it. A folder that can't be read
    /// counts as none (said as "run Refresh game data") rather than an unhandled error from a click.</summary>
    private bool HasExtractedGameData()
    {
        try
        {
            return Directory.Exists(_dataFolder) && Directory.EnumerateFiles(_dataFolder, "*.json", SearchOption.AllDirectories).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task OpenModFixAsync(LibraryItemViewModel? item)
    {
        if (item is null || item.IsOpaquePak || _openingModFix)
        {
            return;
        }

        if (ModFixBlockedReason(item) is { } blocked)
        {
            StatusMessage = blocked;
            return;
        }

        if (!HasExtractedGameData())
        {
            StatusMessage = "No game data found — run Refresh game data in Settings first.";
            return;
        }

        ModFixViewModel? viewModel = null;
        try
        {
            var folderName = item.FolderName;
            // Off the UI thread (a whole mod against the game data); one window at a time meanwhile.
            ModHealthReport report;
            _openingModFix = true;
            try
            {
                report = await Task.Run(() => FixService.Check(folderName));
            }
            finally
            {
                _openingModFix = false;
            }

            // A check or refresh may have started while this one ran.
            if (ModFixBlockedReason(item) is { } blockedNow)
            {
                StatusMessage = blockedNow;
                return;
            }

            item.SetHealth(report);
            viewModel = new ModFixViewModel(folderName, item.Name, report, FixService, () => _openEditorCountByFolder.ContainsKey(folderName));
            var window = new ModFixWindow(viewModel) { Owner = Application.Current.MainWindow };
            viewModel.OpenInModEditor = () =>
            {
                var first = report.Issues.FirstOrDefault();
                window.Close();
                OpenEditor(folderName, first is null ? null : (first.CurrentFile, first.ItemName));
            };

            window.ShowDialog();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't check '{item.Name}': {ex.Message}";
        }

        if (viewModel is not null)
        {
            await AfterModFixClosedAsync(item, viewModel);
        }
    }

    /// <summary>The fix window's own check is running (off the UI thread): a second click waits for it.</summary>
    private bool _openingModFix;

    /// <summary>Why the fix window can't open for this mod right now (null: it can).</summary>
    internal string? ModFixBlockedReason(LibraryItemViewModel item) => ModBusyReason(item, "so its next Save doesn't undo the fixes");

    /// <summary>
    /// Why the mod can't be restored, deleted, reinstalled or updated right now (null: it can) — the
    /// fix window's guards: a check or fix pass rewriting mods in the background, this mod being
    /// converted again, or its editor open (whose next Save would write the old copy back).
    /// </summary>
    internal string? ModChangeBlockedReason(LibraryItemViewModel item) => ModBusyReason(item, "so its next Save doesn't write the old copy back");

    private string? ModBusyReason(LibraryItemViewModel item, string editorReason)
    {
        if (IsCheckingStaleness)
        {
            return "Mods are being checked or fixed against the game right now. Try again when that's done.";
        }

        if (_foldersCurrentlyConvertingToExmod.Contains(item.FolderName))
        {
            return $"'{item.Name}' is being refreshed against the game data right now. Try again in a moment.";
        }

        return _openEditorCountByFolder.ContainsKey(item.FolderName)
            ? $"'{item.Name}' is open in the mod editor. Close it there first, {editorReason}."
            : null;
    }

    /// <summary>
    /// After the fix window closes, however it closed: the badge is checked again (Restore backup
    /// and Forget change what's flagged too), and only fixes actually made are logged as such
    /// ("Keep all as they are" applies nothing; it only remembers the choices).
    /// </summary>
    internal async Task AfterModFixClosedAsync(LibraryItemViewModel item, ModFixViewModel viewModel)
    {
        var folderName = item.FolderName;
        try
        {
            item.SetHealth(await Task.Run(() => FixService.Check(folderName)));
            RefreshFilters();
        }
        catch (Exception)
        {
            // The badge keeps what the window opened with.
        }

        var changedOnDisk = viewModel.AppliedCount > 0 || viewModel.ChangedWithoutApply;
        if (changedOnDisk)
        {
            item.NotifyBackupStateChanged();
            item.NotifyRepairedFromDisk();
            if (ReferenceEquals(item, SelectedItem))
            {
                item.EnsureDetailsLoaded();
            }
        }

        if (viewModel.IsApplied && viewModel.StatusMessage is { } status)
        {
            StatusMessage = status;
        }

        if (viewModel.AppliedCount > 0)
        {
            _activityLog.Log($"Fixed {viewModel.AppliedCount} thing(s) in '{item.Name}' to fit the current game (backed up first).", ActivityEntryKind.Success);
        }
    }

    /// <summary>
    /// The automatic part: every sure fix (old copies of game data, proven broken links and
    /// renames) and every choice made before, in every mod the last check flagged, each backed up
    /// first. Tidying alone (values the game already has) never: a hand-made mod that fits the game
    /// isn't rewritten. What needs a look stays flagged for the fix window.
    /// </summary>
    [RelayCommand]
    private Task FixSureProblemsAsync() => FixSureProblemsCoreAsync();

    /// <summary>Fix sure problems in every mod; what was fixed, per mod (Update everything lists it).</summary>
    internal async Task<IReadOnlyList<GameUpdateFixedMod>> FixSureProblemsCoreAsync()
    {
        var fixedMods = new List<GameUpdateFixedMod>();
        if (IsCheckingStaleness)
        {
            return fixedMods;
        }

        // A mod being converted again right now is left for that conversion (it rewrites the mod).
        var flagged = AllLibraryItems()
            .Where(i => i.Health is { SureProblemCount: > 0 } && !i.IsOpaquePak && !_foldersCurrentlyConvertingToExmod.Contains(i.FolderName))
            .ToList();
        if (flagged.Count == 0)
        {
            StalenessCheckStatusMessage = "Nothing sure to fix. Run Check mods against game data first, or click a flagged row's warning badge.";
            return fixedMods;
        }

        var folders = flagged.Select(i => i.FolderName).ToList();
        // Mods whose editor is open are skipped, checked again right before each one is written
        // (an editor can be opened while the pass runs): the editor's next Save would undo the fixes.
        var skippedFolders = new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        IsCheckingStaleness = true;
        try
        {
            var results = await Task.Run(() =>
            {
                var cache = new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
                var history = FixService.History();
                var addedRows = FixService.LibraryAddedRows();
                var outcome = new Dictionary<string, (int Applied, ModHealthReport After)>(StringComparer.OrdinalIgnoreCase);
                foreach (var folder in folders)
                {
                    if (IsEditorOpenLive(folder))
                    {
                        skippedFolders[folder] = true;
                        continue;
                    }

                    try
                    {
                        var (applied, _) = FixService.FixSure(folder, cache, history, addedRows);
                        outcome[folder] = (applied, FixService.Check(folder, cache, history, addedRows));
                    }
                    catch (Exception)
                    {
                        // One unreadable mod doesn't stop the others.
                    }
                }

                return outcome;
            });

            var total = 0;
            var openedDuringPass = new List<string>();
            foreach (var item in flagged)
            {
                if (results.TryGetValue(item.FolderName, out var result))
                {
                    item.SetHealth(result.After);
                    if (result.Applied > 0)
                    {
                        total += result.Applied;
                        fixedMods.Add(new GameUpdateFixedMod(item.FolderName, item.Name, result.Applied));
                        item.NotifyBackupStateChanged();
                        item.NotifyRepairedFromDisk();
                        _activityLog.Log($"Fixed {result.Applied} thing(s) in '{item.Name}' to fit the current game (backed up first).", ActivityEntryKind.Success);

                        // Opened in the editor after this mod's own check but before the pass ended:
                        // that editor still holds the mod as it was, and its Save would undo these.
                        if (_openEditorCountByFolder.ContainsKey(item.FolderName))
                        {
                            openedDuringPass.Add(item.Name);
                        }
                    }
                }
            }

            var skipped = flagged.Where(i => skippedFolders.ContainsKey(i.FolderName)).Select(i => i.Name).ToList();
            var failed = flagged.Where(i => !skippedFolders.ContainsKey(i.FolderName) && !results.ContainsKey(i.FolderName)).Select(i => i.Name).ToList();
            var left = flagged.Count(i => i.HasModIssues);
            StalenessCheckStatusMessage = $"Fixed {total} sure thing(s) in {results.Count(r => r.Value.Applied > 0)} mod(s), each backed up first (right-click a mod › Restore latest backup undoes it)."
                + (left > 0 ? $" {left} mod(s) still have things to look over: click a row's warning badge." : "")
                + (skipped.Count > 0 ? $" Skipped (open in the mod editor): {string.Join(", ", skipped)}." : "")
                + (failed.Count > 0 ? $" Couldn't read: {string.Join(", ", failed)}." : "")
                + (openedDuringPass.Count > 0 ? $" Opened in the mod editor while fixing: {string.Join(", ", openedDuringPass)} — close that editor without saving, or its Save undoes the fixes." : "");

            RefreshFilters();
            if (total > 0)
            {
                // Like Apply and the editor's Save: the rest of the app (merge state, details) re-reads the rewritten mods.
                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            }

            SelectedItem?.EnsureDetailsLoaded();
            return fixedMods;
        }
        finally
        {
            IsCheckingStaleness = false;
            RunPendingRefresh();
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private Task CheckModsAgainstCurrentData() => CheckModsAgainstCurrentDataAsync();

    /// <summary>
    /// A separate question and a separate action from CheckForUpdatesAsync above (that's Nexus/
    /// catalog version-string checking) — this asks "does this mod's own data still match what the
    /// currently-extracted game data actually defines", independent of any catalog or version
    /// string. Explicit action (a toolbar button), not automatic on every Library load, since a
    /// whole-library base-vs-modded diff is real work a routine navigation shouldn't pay for.
    ///
    /// Looks only: what it finds (ModHealthChecker, through the fix service) is fixed from the fix
    /// window or Check ▾ › Fix sure problems. It used to rename rows silently, which renamed mods'
    /// own new rows to similar game rows (Homestead's Water_Well became the game's Waterwheel).
    /// </summary>
    internal async Task CheckModsAgainstCurrentDataAsync()
    {
        // Fix sure problems and the refresh after a game data update write mods: never at once.
        // Checked here, not via [RelayCommand]'s CanExecute, since IsCheckingStaleness itself is what's guarded.
        if (IsCheckingStaleness)
        {
            return;
        }

        // Opaque .pak entries have no .EXMOD to diff — same exclusion EnsureDetailsLoaded's own
        // ChangesContent formatting already applies.
        var candidates = AllLibraryItems().Where(i => !i.IsOpaquePak).ToList();
        if (candidates.Count == 0)
        {
            // Said, not silent: the click otherwise looked like it did nothing.
            StalenessCheckStatusMessage = "No editable mods to check — prebuilt paks can't be compared with the game data (Make prebuilt paks editable first).";
            return;
        }

        // Without this, a Data folder that's missing, empty, or was never extracted makes every
        // mod's own base-file lookup fail silently — every mod would come back as fitting the game,
        // a false all-clear. Same check, same wording, ExmodEditorViewModel's own game-data search
        // already uses for the identical underlying condition.
        if (!HasExtractedGameData())
        {
            StatusMessage = "No game data found — run Refresh game data in Settings first.";
            return;
        }

        IsCheckingStaleness = true;
        try
        {
            var folderNames = candidates.Select(i => i.FolderName).ToList();

            // Off the UI thread: every non-opaque mod against the real extracted game data is real,
            // avoidable-to-block-on work for a large library. Results are collected into a plain
            // dictionary and applied to the rows back on this (UI) thread afterward.
            var resultsByFolder = await Task.Run(() =>
            {
                // Shared for the whole pass, not per mod — many mods touch the same real file (e.g.
                // Traits-D_Itemable.json), and re-parsing it once per mod that touches it is real,
                // avoidable cost.
                var pass = new CheckPass(new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase), FixService.History(), FixService.LibraryAddedRows());
                var results = new Dictionary<string, ModHealthReport?>();
                foreach (var folderName in folderNames)
                {
                    try
                    {
                        results[folderName] = FixService.Check(folderName, pass.Tables, pass.History, pass.AddedRows);
                    }
                    catch (Exception)
                    {
                        // Best-effort per mod, matching CheckForUpdatesAsync's own per-source
                        // isolation — a locked or malformed .EXMOD just means that one mod isn't
                        // checked this pass (named below; its badge keeps what it had).
                        results[folderName] = null;
                    }
                }

                return results;
            });

            var flaggedCount = 0;
            var couldNotCheck = new List<string>();
            foreach (var item in candidates)
            {
                if (resultsByFolder.GetValueOrDefault(item.FolderName) is not { } health)
                {
                    couldNotCheck.Add(item.Name);
                }
                else
                {
                    item.SetHealth(health);
                }

                if (item.Health is { HasProblems: true })
                {
                    flaggedCount++;
                }
            }

            var messageParts = new List<string>
            {
                flaggedCount > 0
                    ? $"{flaggedCount} mod(s) don't fully fit the current game — click a flagged row's warning badge to see and fix them, or Check ▾ › Fix sure problems in every mod."
                    : couldNotCheck.Count > 0 ? "Every mod that could be checked fits the current game." : "Every mod fits the current game.",
            };
            if (couldNotCheck.Count > 0)
            {
                messageParts.Add($"Couldn't check {couldNotCheck.Count} mod(s): {string.Join(", ", couldNotCheck)}.");
            }

            StalenessCheckStatusMessage = string.Join(" ", messageParts);
            RefreshFilters();
        }
        finally
        {
            IsCheckingStaleness = false;
            RunPendingRefresh();
        }
    }

    /// <summary>Checks one row's mod again off the UI thread and updates its badge; a failed check keeps the badge it had.</summary>
    /// <param name="pass">What a pass of checks shares (the refresh after a game data update); null reads it afresh.</param>
    private async Task RefreshHealthAsync(LibraryItemViewModel item, CheckPass? pass = null)
    {
        var folderName = item.FolderName;
        try
        {
            item.SetHealth(await Task.Run(() => FixService.Check(folderName, pass?.Tables, pass?.History, pass?.AddedRows)));
            RefreshFilters();
        }
        catch (Exception)
        {
            // Only the badge: the next check against game data tries again.
        }
    }
}
