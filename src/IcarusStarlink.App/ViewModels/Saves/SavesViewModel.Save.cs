using System.IO;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Unsaved-change tracking (the Save bar's summary, bad number boxes, batched notifications) and Save itself.</summary>
public sealed partial class SavesViewModel
{
    /// <summary>"Unsaved: Characters, Unlocks, Bestiary" — what Save would write, named on the Save bar.</summary>
    public string? ChangesSummary
    {
        get
        {
            if (_loadFailed)
            {
                return null;
            }

            var parts = new List<string>();
            void Add(bool dirty, string name)
            {
                if (dirty)
                {
                    parts.Add(name);
                }
            }

            Add(CharactersDirty, "Characters");
            Add(Currencies.Any(c => c.IsDirty), "Currencies");
            Add(AccountFlags.Any(f => f.IsDirty), "Unlocks");
            Add(BinaryFlags.Any(f => f.IsDirty), "Owned DLC");
            Add(WorkshopTalents.Any(t => t.IsDirty && !IsMission(t)), "Workshop");
            Add(WorkshopTalents.Any(t => t.IsDirty && IsMission(t)), "Missions");
            Add(Accolades.Any(a => a.IsDirty), "Accolades");
            Add(BestiaryEntries.Any(b => b.IsDirty), "Bestiary");
            Add(_metaInventoryDirty, "Items");
            Add(_mountsListDirty || Mounts.Any(m => m.IsDirty), "Mounts");
            return parts.Count == 0 ? null : $"Unsaved: {string.Join(", ", parts)}";
        }
    }

    /// <summary>
    /// The edited number boxes Save couldn't write ("Alpha: XP", "Ren"): exactly the boxes showing
    /// a red border and a line under them (each row's XpError / NumberError, by SaveNumberRules,
    /// the same rules each row's ApplyToNode uses: a whole number, 0 or more; an appearance value
    /// may be negative).
    /// </summary>
    internal List<string> BadNumberBoxes()
    {
        var bad = new List<string>();
        foreach (var character in Characters)
        {
            if (character.HasXpError)
            {
                bad.Add($"{character.Name}: XP");
            }

            bad.AddRange(character.Cosmetics.Where(f => f.HasNumberError).Select(f => $"{character.Name}: {f.Key}"));
        }

        bad.AddRange(Currencies.Where(c => c.HasNumberError).Select(c => c.Label));
        bad.AddRange(BestiaryEntries.Where(b => b.HasNumberError).Select(b => $"{b.DisplayName} (Bestiary)"));
        bad.AddRange(Mounts.Where(m => m.HasNumberError).Select(m => $"{m.Name}: level"));
        return bad;
    }

    /// <summary>Above 0 inside BatchedDirtyChanges: row edits only note that something changed.</summary>
    private int _dirtyBatchDepth;

    private bool _dirtyChangedInBatch;

    /// <summary>
    /// Many row edits at once (Complete all shown, Fix all, Remove leftovers) tell the page once,
    /// at the end: each used to re-check every list and every points editor, per row.
    /// </summary>
    private DirtyBatch BatchedDirtyChanges()
    {
        _dirtyBatchDepth++;
        return new DirtyBatch(this);
    }

    private readonly struct DirtyBatch(SavesViewModel page) : IDisposable
    {
        public void Dispose()
        {
            if (--page._dirtyBatchDepth == 0 && page._dirtyChangedInBatch)
            {
                page._dirtyChangedInBatch = false;
                page.NotifyDirtyChanged();
            }
        }
    }

    private void NotifyDirtyChanged()
    {
        if (_dirtyBatchDepth > 0)
        {
            _dirtyChangedInBatch = true;
            return;
        }

        OnPropertyChanged(nameof(ChangesSummary));
        OnPropertyChanged(nameof(AccoladesSummary));
        OnPropertyChanged(nameof(BestiarySummary));
        OnPropertyChanged(nameof(TalentRanksSummary));
        OnPropertyChanged(nameof(PointsNotes));
        OnPropertyChanged(nameof(HasPointsNotes));
        RefreshPointsEditors();
        OnPropertyChanged(nameof(WorkshopSummary));
        OnPropertyChanged(nameof(MissionsSummary));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(HasAnyUnsavedEdits));
        OnPropertyChanged(nameof(OwnedDlcSummary));
        NotifyFlagSummaries();
        OnPropertyChanged(nameof(SelectedLevelDisplay));
    }

    /// <summary>
    /// Writes AccountFlags/WorkshopTalents back into the profile node — the profile-level
    /// counterpart of SaveCharacterViewModel.ApplyToNode, same minimal-diff rules. Each list is
    /// rebuilt only when it was edited, so a section that couldn't be read (empty, see LoadSection)
    /// can never wipe the save's own list.
    /// </summary>
    private void ApplyProfileEdits()
    {
        if (_profile is null)
        {
            return;
        }

        if (AccountFlags.Any(f => f.IsDirty))
        {
            ApplyAccountFlagEdits(_profile);
        }

        if (WorkshopTalents.Any(t => t.IsDirty))
        {
            ApplyWorkshopTalentEdits(_profile);
        }
    }

    [RelayCommand]
    private void SaveChanges()
    {
        if (SelectedSlot is null)
        {
            return;
        }

        if (!HasUnsavedChanges)
        {
            StatusMessage = "Nothing changed.";
            return;
        }

        // A box that isn't a whole number is skipped by the write, so Save used to say "Saved."
        // while that edit never reached the file (and the page stayed "Unsaved" with no reason).
        if (BadNumberBoxes() is { Count: > 0 } bad)
        {
            StatusMessage = $"Not saved: {string.Join(", ", bad.Take(6))}{(bad.Count > 6 ? $" and {bad.Count - 6} more" : "")} "
                + $"{(bad.Count == 1 ? "needs" : "need")} a whole number (digits only, no commas or spaces). Fix {(bad.Count == 1 ? "it" : "them")} and save again.";
            return;
        }

        if (IsGameRunning())
        {
            StatusMessage = "Icarus is running — close it before saving, or the game will overwrite your edits when it exits.";
            return;
        }

        // The page is a singleton that read the save once: if the user played since, writing now
        // would put that older copy over their newer progress (a review finding).
        if (SaveFilesChangedOnDisk())
        {
            RefuseStaleSave();
            return;
        }

        // Via IDialogService, not ThemedMessageBox.Show directly — same reasoning as
        // RestoreBackupAsync's own confirm: this method's real write needs to be testable without
        // a live WPF Dispatcher, including the late re-check immediately below.
        var confirm = _dialogService.Confirm(
            "Write your edits into the player save?\n\nA full backup of the slot is taken automatically first (Restore can undo this).",
            "Save player data", ThemedConfirmSeverity.Question);
        if (!(confirm))
        {
            return;
        }

        var writing = false;
        try
        {
            // Each file is written only when its own section changed: Characters.json for the
            // characters, Profile.json for currencies, account flags, workshop research (and the
            // NextChrSlot a duplicate advances). Only edited characters/currencies touch their nodes.
            var writeCharacters = CharactersDirty;
            var writeProfile = ProfileDirty;

            // A SECOND, LATE check, immediately before the first real disk write below — the first
            // IsGameRunning() check above (before the confirm dialog) can go stale for however long
            // the user takes to answer that dialog, and Icarus could be launched in that window.
            // This doesn't close the race entirely (no check here can, without an OS-level file
            // lock held across processes for the whole write) — it only shrinks the exposed window
            // down to "immediately before the write" instead of "however long the confirm dialog
            // was up", aborting late-and-safe (nothing has been written yet) rather than
            // early-and-stale.
            if (IsGameRunning())
            {
                StatusMessage = "Icarus started running while this save was about to write — nothing was touched. Close it and try Save again.";
                return;
            }

            // Same late re-check for an outside write while the confirm dialog was up.
            if (SaveFilesChangedOnDisk())
            {
                RefuseStaleSave();
                return;
            }

            // ONE backup for the whole save pass, taken up front — every SaveXxx call below passes
            // takeBackup: false. A single click used to be able to zip the same slot up to 6 times
            // back-to-back (Characters/Profile always, plus BinaryFlags/Accolades/Bestiary/
            // MetaInventory whenever dirty), each one blocking the UI in turn, when one pre-save
            // snapshot already covers everything this pass is about to change. Taken BEFORE the
            // edits go into the loaded save: a backup that failed (a file the game held) used to
            // leave them applied, so an edit the user then set back was written by the next Save.
            _repository.BackupSlot(SelectedSlot.SteamId);

            // Only now are the edits put into the loaded save: applied before these checks, an
            // aborted save left them in it, and an edit the user then undid was written anyway by
            // the next Save (a review finding).
            foreach (var character in Characters.Where(c => c.IsDirty))
            {
                character.ApplyToNode();
            }

            foreach (var currency in Currencies.Where(c => c.IsDirty))
            {
                currency.ApplyToNode();
            }

            ApplyProfileEdits();
            writing = true;

            // Characters first, then profile — keeps write ORDER consistent with before, even
            // though backup ordering no longer matters now that there's just the one upfront zip.
            if (writeCharacters)
            {
                _repository.SaveCharacters(SelectedSlot.SteamId, _characterNodes, takeBackup: false);
            }

            if (writeProfile)
            {
                _repository.SaveProfile(SelectedSlot.SteamId, _profile!, takeBackup: false);
                _profileFieldsDirty = false;
            }

            // The DLC list is written only when a pack was toggled: packs that stay keep the file's
            // own order, newly-added ones append.
            if (_binaryFlagIds is { } original && BinaryFlags.Any(f => f.IsDirty))
            {
                var nowSet = BinaryFlags.Where(f => f.IsUnlocked).Select(f => f.Id).ToHashSet();
                var newIds = original.Where(nowSet.Contains).ToList();
                newIds.AddRange(nowSet.Except(original).OrderBy(i => i));
                _repository.SaveBinaryFlags(SelectedSlot.SteamId, newIds, takeBackup: false);
                _binaryFlagIds = newIds;
            }

            // Accolades.json/BestiaryData.json are separate files from Profile.json — these only
            // write when that specific section actually changed, same conditional-write reasoning
            // BinaryFlags above already uses, rather than always re-writing every file on every Save
            // click (the upfront backup above already covers whichever of these actually run).
            if (Accolades.Any(a => a.IsDirty))
            {
                ApplyAccoladeEdits();
                _repository.SaveAccolades(SelectedSlot.SteamId, _accoladesRoot!, takeBackup: false);
            }

            if (BestiaryEntries.Any(b => b.IsDirty))
            {
                ApplyBestiaryEdits();
                _repository.SaveBestiary(SelectedSlot.SteamId, _bestiaryRoot!, takeBackup: false);
            }

            if (_metaInventoryDirty)
            {
                ApplyMetaInventoryEdits();
                _repository.SaveMetaInventory(SelectedSlot.SteamId, _metaInventoryRoot!, takeBackup: false);
                _metaInventoryDirty = false;
            }

            // _mountsListDirty (a mount removed from the list) is checked alongside per-field
            // dirtiness — a pure removal with no remaining mount edited would otherwise never
            // trigger this write at all, silently leaving a "deleted" mount in Mounts.json.
            if (_mountsListDirty || Mounts.Any(m => m.IsDirty))
            {
                ApplyMountEdits();
                _repository.SaveMounts(SelectedSlot.SteamId, _mountsRoot!, takeBackup: false);
                _mountsListDirty = false;
            }

            foreach (var collection in DirtyTrackedCollections)
            {
                foreach (var item in collection)
                {
                    item.MarkClean();
                }
            }

            _charactersListDirty = false;

            // These writes changed the files' stamps; the new ones are the baseline for the next Save.
            try
            {
                _loadedStamps = _repository.GetFileStamps(SelectedSlot.SteamId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _loadedStamps = null;
            }

            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(ChangesSummary));
            RefreshBackupsList();
            StatusMessage = "Saved. A backup of the previous state was kept.";
            _activityLog.Log($"Edited player save {SelectedSlot.Display}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            if (writing)
            {
                // Some files may already be written. Their new stamps are this page's own doing, so
                // they become the baseline: otherwise the next Save took them for the game's and
                // refused, leaving "reload and lose your edits" as the only way out.
                try
                {
                    _loadedStamps = _repository.GetFileStamps(SelectedSlot.SteamId);
                }
                catch (Exception stampEx) when (stampEx is IOException or UnauthorizedAccessException)
                {
                }

                RefreshBackupsList();
                StatusMessage = $"Save failed partway: {ex.Message} Some files may already be written. Click Save to try again, or Restore the newest backup (taken just before this save) to undo it.";
            }
            else
            {
                StatusMessage = $"Save failed, nothing was written: {ex.Message}";
            }

            _activityLog.Log($"Player save not written: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// The game holds these files and rewrites them on exit — any edit made while it runs is lost
    /// or half-read, so save/restore refuse rather than race it. Delegates to IGameProcessChecker
    /// (rather than calling Process.GetProcessesByName directly, which is all this used to do)
    /// purely so a test can fake the answer — including a DIFFERENT answer on a second call within
    /// the same pass, to exercise the late re-check SaveChanges/RestoreBackupAsync each run
    /// immediately before their real write.
    /// </summary>
    private bool IsGameRunning() => _gameProcessChecker.IsRunning();
}
