using System.IO;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Converting prebuilt paks into editable mods, and the silent refresh of converted and scale-rule mods after a game data update.</summary>
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
                pass ??= await Task.Run(() => CheckPass.Create(FixService));
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
        // Off the UI thread, reading only each mod's package (not its cooked files): this read every
        // mod's assets on the UI thread, freezing the app after each game data refresh. Each mod is
        // backed up before it's rewritten, like every other pass that writes mods.
        var ruleCandidates = AllLibraryItems()
            .Where(i => !i.IsOpaquePak && !foldersWithOpenEditors.Contains(i.FolderName))
            .Select(i => (i.FolderName, i.Name))
            .ToList();
        var (rulesRefreshedCount, ruleFailures) = await Task.Run(() => RebuildFromScaleRules(ruleCandidates));
        foreach (var (name, reason) in ruleFailures)
        {
            _activityLog.Log($"Couldn't rebuild '{name}' from its scale rules after updating game data: {reason}", ActivityEntryKind.Warning);
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

    /// <summary>
    /// Step 3 of the refresh after a game data update, off the UI thread: every mod built from scale
    /// rules rebuilds its rule rows from the new game data (backed up first). A mod opened in the
    /// editor meanwhile is left for its own Save. Returns how many changed, and each failure.
    /// </summary>
    private (int Rebuilt, List<(string Name, string Reason)> Failures) RebuildFromScaleRules(IReadOnlyList<(string FolderName, string Name)> mods)
    {
        var rebuilt = 0;
        var failures = new List<(string Name, string Reason)>();
        GameTableIndex? gameTables = null;
        foreach (var (folderName, name) in mods)
        {
            try
            {
                var folder = _repository.GetFolderPath(folderName);
                var package = ExmodFolder.ReadPackageOnly(folder);
                if (package.ScaleRules.Count == 0)
                {
                    continue;
                }

                gameTables ??= new GameTableIndex(_dataFolder);
                var before = ExmodJson.Serialize(package, indented: false);
                ScaleRules.ApplyToPackage(package, package.ScaleRules, _dataFolder, new MergeReport(), gameTables);
                if (ExmodJson.Serialize(package, indented: false) == before || IsEditorOpenLive(folderName))
                {
                    continue;
                }

                _repository.BackupMod(folderName);
                ExmodFolder.Write(folder, new ExmodPackageContents(package, []));
                rebuilt++;
            }
            catch (Exception ex)
            {
                failures.Add((name, ex.Message));
            }
        }

        return (rebuilt, failures);
    }
}
