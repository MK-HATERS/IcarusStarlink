using System.IO;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Views;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Unsaved changes, the close question, and Save (refusing when the mod changed elsewhere or its folder is gone).</summary>
public sealed partial class ExmodEditorViewModel
{
    /// <summary>True when the mod differs from what's on disk (typed values, structural edits, or Scale rules not applied yet).</summary>
    public bool HasUnsavedChanges => _scaleRulesDirty || ExmodJson.Serialize(_package) != _savedJson;

    /// <summary>Asks what to do with unsaved changes on close: true = Save, false = Don't save, null = Cancel. Tests swap in an answer.</summary>
    internal Func<string, bool?> AskSaveOnClose
    {
        get => _askSaveOnClose ??= message => ThemedConfirmDialog.ShowWithCancel(
            OwnerWindow(), message, "Unsaved changes", ThemedConfirmSeverity.Question, "Save", "Don't save");
        set => _askSaveOnClose = value;
    }

    private Func<string, bool?>? _askSaveOnClose;

    /// <summary>
    /// Called as the window closes: true when it may close. Unsaved changes ask Save / Don't save /
    /// Cancel; a Save that doesn't go through (a rule to fix, the mod changed elsewhere, its folder
    /// gone) keeps the window open with the reason showing.
    /// </summary>
    public bool ConfirmClose()
    {
        if (!HasUnsavedChanges)
        {
            return true;
        }

        switch (AskSaveOnClose($"You have changes to '{_package.Name}' that aren't saved. Save them before closing?"))
        {
            case null:
                return false;
            case false:
                return true;
            default:
                Save();
                return !HasUnsavedChanges;
        }
    }

    /// <summary>Called when the editor window has closed: stops answering app-wide messages.</summary>
    public void Detach() => WeakReferenceMessenger.Default.UnregisterAll(this);

    /// <summary>False once the mod was deleted (or moved) in Library; the repository throws for a folder it no longer knows.</summary>
    private bool ModFolderExists()
    {
        try
        {
            return Directory.Exists(_repository.GetFolderPath(_folderName));
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    [RelayCommand]
    private void Save()
    {
        // Deleted (or moved) in Library while open here: writing would make an orphan folder that
        // Library doesn't list, so the work would look saved but be nowhere to be found.
        if (!ModFolderExists())
        {
            StatusMessage = "Not saved: this mod's folder is gone (deleted or moved in Library?). There's nowhere to save it, so nothing was written.";
            return;
        }

        // A box that shows a value the mod doesn't hold (it didn't parse) would be saved as its old
        // value under "Saved.", and the box reset to it: the typed change gone without a word.
        if (_allFields.FirstOrDefault(f => f.HasUnwrittenText) is { } unwritten)
        {
            StatusMessage = $"Not saved: {unwritten.Label} shows a value that isn't valid, so it isn't in the mod yet"
                + (unwritten.ParseError is { Length: > 0 } why ? $" ({why})" : "") + ". Fix it, or ↺ for the game's value, then Save.";
            return;
        }

        // The Editor page (or anything else) may have saved into this mod since it was opened here:
        // overwriting that silently would lose it. The first Save says so; a second one overwrites.
        void RefuseChangedElsewhere()
        {
            _overwriteOutsideChanges = true;
            StatusMessage = "Not saved: this mod was changed somewhere else (the Editor page?) since you opened it here. Save again to overwrite those changes, or close this window and reopen the mod to keep them.";
        }

        if (ExmodStamp() != _savedStamp && !_overwriteOutsideChanges)
        {
            RefuseChangedElsewhere();
            return;
        }

        if (!ApplyPendingScaleRules())
        {
            StatusMessage = $"Not saved — a scale rule needs fixing: {ScaleStatusMessage}";
            return;
        }

        try
        {
            // Writes a STRIPPED copy — never mutates the live _package/its item.Fields in place, so
            // nothing visibly vanishes from the open editor (Items list, Fields pane, amber
            // highlights, Undo stack all keep operating on the real, unstripped session state). The
            // ExmodJson round-trip is the same deep-copy pattern PushUndoSnapshot already uses, so
            // every ExmodPackage-level property (Name/Author/Version/etc.) carries over correctly
            // without being hand-copied field by field here.
            var strippedRows = ExmodBaseDiffer.StripFieldsIdenticalToBase(_package, _dataFolder);
            var strippedFieldCount = _package.Rows.Sum(r => r.FileItems.Sum(i => i.Fields.Count))
                - strippedRows.Sum(r => r.FileItems.Sum(i => i.Fields.Count));

            var writePackage = ExmodJson.Parse(ExmodJson.Serialize(_package));
            writePackage.Rows = strippedRows;
            if (!_writer.TrySave(_folderName, writePackage, _overwriteOutsideChanges ? null : _savedStamp, out var stamp))
            {
                RefuseChangedElsewhere();
                return;
            }

            _savedStamp = stamp;
            _savedJson = ExmodJson.Serialize(_package);
            _overwriteOutsideChanges = false;
            HasSaved = true;

            // What just got written is now this mod's own "original" going forward — refreshing
            // the already-displayed Fields immediately (not waiting for the next reselect) so
            // every "was: X" indicator for the just-saved item clears right away.
            SnapshotOriginalValues();
            PopulateFieldsForSelection();
            StatusMessage = strippedFieldCount > 0
                ? $"Saved — removed {strippedFieldCount} field(s) that matched base game data."
                : "Saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
            return;
        }

        // Saved either way; a link to a row that doesn't exist is worth knowing about now, not in game.
        if (Directory.Exists(_dataFolder) && RunReferenceCheck(showWhenClean: false) is > 0 and var problems)
        {
            StatusMessage += $" {problems} reference problem(s), listed above.";
        }
    }
}
