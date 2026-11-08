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
            Add(Currencies.Any(c => c.IsDirty) || _removedCurrencies.Count > 0, "Currencies");
            Add(AccountFlags.Any(f => f.IsDirty), "Unlocks");
            Add(BinaryFlags.Any(f => f.IsDirty), "Owned DLC");
            Add(WorkshopTalents.Any(t => t.IsDirty && !IsMission(t)), "Workshop");
            Add(WorkshopTalents.Any(t => t.IsDirty && IsMission(t)), "Missions");
            Add(Accolades.Any(a => a.IsDirty), "Accolades");
            Add(BestiaryEntries.Any(b => b.IsDirty), "Bestiary");
            Add(_metaInventoryDirty, "Items");
            Add(_mountsListDirty || Mounts.Any(m => m.IsDirty), "Mounts");
            Add(_mergeDuplicatesIn.Count > 0, "Repeated entries");
            return parts.Count == 0 ? null : $"Unsaved: {string.Join(", ", parts)}";
        }
    }

    /// <summary>
    /// What Save would change, one line per area, for its confirm (a stray click is easy to
    /// miss in "Unsaved: Characters"). Rank changes say "3 → 0"; a long
    /// list says how many more; at most <paramref name="maxLines"/> lines.
    /// </summary>
    internal List<string> ChangeLines(int maxLines = 12)
    {
        const int perLine = 4;
        var lines = new List<string>();

        static string Some(IReadOnlyList<string> items) =>
            items.Count <= perLine ? string.Join(", ", items) : $"{string.Join(", ", items.Take(perLine))} (+{items.Count - perLine:N0} more)";

        static string Rank(SaveTalentViewModel t) => $"{t.DisplayName} {t.OriginalRank} → {t.Rank}";

        void OnOff(string area, IEnumerable<(string Name, bool On)> changed, string onWord, string offWord)
        {
            var list = changed.ToList();
            var on = list.Where(c => c.On).Select(c => c.Name).ToList();
            var off = list.Where(c => !c.On).Select(c => c.Name).ToList();
            var parts = new List<string>();
            if (on.Count > 0)
            {
                parts.Add($"{onWord} {Some(on)}");
            }

            if (off.Count > 0)
            {
                parts.Add($"{offWord} {Some(off)}");
            }

            if (parts.Count > 0)
            {
                lines.Add($"{area}: {string.Join("; ", parts)}");
            }
        }

        foreach (var character in Characters.Where(c => c.IsDirty))
        {
            var talents = character.Talents.Where(t => t.IsDirty).Select(Rank).ToList();
            var other = character.IsDirty && (talents.Count == 0 || character.Flags.Any(f => f.IsDirty) || character.Cosmetics.Any(c => c.IsDirty));
            lines.Add(talents.Count > 0
                ? $"{character.Name}: {Some(talents)}{(other ? ", and its other details" : "")}"
                : $"{character.Name}: name, XP, looks or unlocks");
        }

        if (Currencies.Where(c => c.IsDirty).Select(c => c.Label).ToList() is { Count: > 0 } currencies)
        {
            lines.Add($"Currencies: {Some(currencies)}");
        }

        if (_removedCurrencies.Count > 0)
        {
            lines.Add($"Currencies: taking out {Some([.. _removedCurrencies.Select(c => c.Label)])}");
        }

        OnOff("Workshop", WorkshopTalents.Where(t => t.IsDirty && !IsMission(t)).Select(t => (t.DisplayName, t.Rank > 0)), "researched", "cleared");
        OnOff("Missions", WorkshopTalents.Where(t => t.IsDirty && IsMission(t)).Select(t => (t.DisplayName, t.Rank > 0)), "done", "not done");
        OnOff("Unlocks", AccountFlags.Where(f => f.IsDirty).Select(f => (f.Name, f.IsUnlocked)), "on", "off");
        if (BinaryFlags.Any(f => f.IsDirty))
        {
            lines.Add("Owned DLC");
        }

        void Count(string area, int count, string what)
        {
            if (count > 0)
            {
                lines.Add($"{area}: {count:N0} {what}");
            }
        }

        Count("Accolades", Accolades.Count(a => a.IsDirty), "changed");
        Count("Bestiary", BestiaryEntries.Count(b => b.IsDirty), "creatures changed");
        if (_metaInventoryDirty)
        {
            lines.Add("Items: the bank changed");
        }

        if (_mountsListDirty || Mounts.Any(m => m.IsDirty))
        {
            var mounts = Mounts.Where(m => m.IsDirty).Select(m => m.Name).ToList();
            lines.Add(mounts.Count > 0 ? $"Mounts: {Some(mounts)}{(_mountsListDirty ? ", and the list" : "")}" : "Mounts: the list");
        }

        if (_mergeDuplicatesIn.Count > 0)
        {
            lines.Add($"Repeated entries: one of each kept in {string.Join(", ", _mergeDuplicatesIn.Order(StringComparer.Ordinal))}");
        }

        return lines.Count <= maxLines ? lines : [.. lines.Take(maxLines - 1), $"… and {lines.Count - maxLines + 1:N0} more"];
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
        OnPropertyChanged(nameof(WorkshopLeftToResearch));
        OnPropertyChanged(nameof(MissionsSummary));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(HasAnyUnsavedEdits));
        OnPropertyChanged(nameof(OwnedDlcSummary));
        NotifyFlagSummaries();
        OnPropertyChanged(nameof(SelectedLevelDisplay));
        OnPropertyChanged(nameof(SelectedLevelText));
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

        ApplyCurrencyRemovals(_profile);
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
        // would put that older copy over their newer progress.
        if (SaveFilesChangedOnDisk())
        {
            RefuseStaleSave();
            return;
        }

        // Via IDialogService, not ThemedMessageBox.Show directly — same reasoning as
        // RestoreBackupAsync's own confirm: this method's real write needs to be testable without
        // a live WPF Dispatcher, including the late re-check immediately below.
        var changes = ChangeLines();
        var confirm = _dialogService.Confirm(
            "Write your edits into the player save?\n\n"
            + (changes.Count > 0 ? $"What changes:\n{string.Join("\n", changes.Select(c => $"• {c}"))}\n\n" : "")
            + "A full backup of the slot is taken automatically first (Restore can undo this).",
            "Save player data", ThemedConfirmSeverity.Question);
        if (!(confirm))
        {
            return;
        }

        var writing = false;
        var finishedWriting = false;
        string? backupPath = null;
        var steamId = SelectedSlot.SteamId;

        // The files this Save has written so far, put back from its backup if a later one fails.
        var written = new List<string>();
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
            backupPath = _repository.BackupSlot(steamId);

            // Only now are the edits put into the loaded save: applied before these checks, an
            // aborted save left them in it, and an edit the user then undid was written anyway by
            // the next Save.
            foreach (var character in Characters.Where(c => c.IsDirty))
            {
                character.ApplyToNode();
            }

            foreach (var currency in Currencies.Where(c => c.IsDirty))
            {
                currency.ApplyToNode();
            }

            ApplyProfileEdits();

            // Repeated entries go after the edits (an edit keeps every copy's place), before the writes.
            var mergedProfile = _mergeDuplicatesIn.Contains(ProfileFile);
            if (mergedProfile)
            {
                ApplyCurrencyEditsToRepeats();
            }

            ApplyDuplicateMerges();
            writing = true;

            // Characters first, then profile — keeps write ORDER consistent with before, even
            // though backup ordering no longer matters now that there's just the one upfront zip.
            if (writeCharacters)
            {
                _repository.SaveCharacters(steamId, _characterNodes, takeBackup: false);
                written.Add(CharactersFile);
            }

            if (writeProfile)
            {
                _repository.SaveProfile(steamId, _profile!, takeBackup: false);
                written.Add(ProfileFile);
            }

            // The DLC list is written only when a pack was toggled: packs that stay keep the file's
            // own order, newly-added ones append.
            var binaryFlagIds = _binaryFlagIds;
            if (_binaryFlagIds is { } original && BinaryFlags.Any(f => f.IsDirty))
            {
                var nowSet = BinaryFlags.Where(f => f.IsUnlocked).Select(f => f.Id).ToHashSet();
                var newIds = original.Where(nowSet.Contains).ToList();
                newIds.AddRange(nowSet.Except(original).OrderBy(i => i));
                _repository.SaveBinaryFlags(steamId, newIds, takeBackup: false);
                written.Add($"flags_{steamId}.dat");
                binaryFlagIds = newIds;
            }

            // Accolades.json/BestiaryData.json are separate files from Profile.json — these only
            // write when that specific section actually changed, same conditional-write reasoning
            // BinaryFlags above already uses, rather than always re-writing every file on every Save
            // click (the upfront backup above already covers whichever of these actually run).
            if (Accolades.Any(a => a.IsDirty) || _mergeDuplicatesIn.Contains(AccoladesFile))
            {
                ApplyAccoladeEdits();
                _repository.SaveAccolades(steamId, _accoladesRoot!, takeBackup: false);
                written.Add(AccoladesFile);
            }

            if (BestiaryEntries.Any(b => b.IsDirty) || _mergeDuplicatesIn.Contains(BestiaryFile))
            {
                ApplyBestiaryEdits();
                _repository.SaveBestiary(steamId, _bestiaryRoot!, takeBackup: false);
                written.Add(BestiaryFile);
            }

            if (_metaInventoryDirty)
            {
                ApplyMetaInventoryEdits();
                _repository.SaveMetaInventory(steamId, _metaInventoryRoot!, takeBackup: false);
                written.Add(MetaInventoryFile);
            }

            // _mountsListDirty (a mount removed from the list) is checked alongside per-field
            // dirtiness — a pure removal with no remaining mount edited would otherwise never
            // trigger this write at all, silently leaving a "deleted" mount in Mounts.json.
            if (_mountsListDirty || Mounts.Any(m => m.IsDirty))
            {
                ApplyMountEdits();
                _repository.SaveMounts(steamId, _mountsRoot!, takeBackup: false);
                written.Add(MountsFile);
            }

            // Every file is written. Only now is anything marked saved: a Save that failed partway
            // puts back what it wrote, so the retry has to write every section again.
            finishedWriting = true;
            if (writeProfile)
            {
                _profileFieldsDirty = false;
                _removedCurrencies.Clear();
            }

            _binaryFlagIds = binaryFlagIds;
            _metaInventoryDirty = false;
            _mountsListDirty = false;

            foreach (var collection in DirtyTrackedCollections)
            {
                foreach (var item in collection)
                {
                    item.MarkClean();
                }
            }

            _charactersListDirty = false;
            _mergeDuplicatesIn.Clear();

            // The merge dropped repeated MetaResources entries: their rows were bound to entries
            // that are gone, and the one kept may hold another copy's count. Talents, creatures and
            // accolades are matched by row name at Save, so their rows stay right. Every file is
            // written by now: a failure here is only the page's rows, never a failed Save.
            if (mergedProfile)
            {
                try
                {
                    BuildCurrencies();
                    if (_modSources is { } index)
                    {
                        ClassifyOtherRows(index);
                    }

                    NotifyLeftoversChanged();
                }
                catch (Exception ex)
                {
                    _activityLog.Log($"Couldn't refresh the currency rows after saving: {ex.Message}", ActivityEntryKind.Warning);
                }
            }

            // These writes changed the files' stamps; the new ones are the baseline for the next Save.
            try
            {
                _loadedStamps = _repository.GetFileStamps(steamId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _loadedStamps = null;
            }

            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(ChangesSummary));
            RefreshBackupsList();
            // What's left, checked against what was just written.
            RunSaveCheck();
            StatusMessage = "Saved. A backup of the previous state was kept. "
                + (CheckFindings.Count == 0 ? "The save check finds nothing now." : $"The save check still finds {CheckSummary} (Overview tab).");
            _activityLog.Log($"Edited player save {SelectedSlot.Display}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            // Each write is atomic: the file that failed was never changed, so a failure on the
            // first write left nothing written.
            if (writing && written.Count > 0)
            {
                // All or nothing: the files already written go back to the backup taken just
                // before, so the slot isn't left half old, half new.
                var putBack = !finishedWriting && backupPath is not null && TryPutBack(steamId, backupPath, written);

                // The files' stamps are this page's own doing now (written or put back), so they
                // become the baseline: otherwise the next Save took them for the game's and
                // refused, leaving "reload and lose your edits" as the only way out.
                try
                {
                    _loadedStamps = _repository.GetFileStamps(steamId);
                }
                catch (Exception stampEx) when (stampEx is IOException or UnauthorizedAccessException)
                {
                }

                RefreshBackupsList();
                StatusMessage = putBack
                    ? $"Save failed: {ex.Message} What it had already written was put back from the backup taken just before, so the save is as it was. Your edits are still here: click Save to try again."
                    : $"Save failed partway: {ex.Message} Some files may already be written. Click Save to try again, or Restore the newest backup (taken just before this save) to undo it.";
            }
            else
            {
                StatusMessage = $"Save failed, nothing was written: {ex.Message}";
            }

            _activityLog.Log($"Player save not written: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    private const string MetaInventoryFile = "MetaInventory.json";
    private const string MountsFile = "Mounts.json";

    /// <summary>
    /// Puts the files a failed Save wrote back from its backup. False when that fails too (the
    /// game or Steam usually holds the same files), and the page then says how to undo it by hand.
    /// </summary>
    private bool TryPutBack(string steamId, string backupPath, List<string> written)
    {
        try
        {
            _repository.PutBackFiles(steamId, backupPath, written);
            return true;
        }
        catch (Exception ex)
        {
            _activityLog.Log($"Couldn't put back what the failed save wrote: {ex.Message}", ActivityEntryKind.Warning);
            return false;
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
