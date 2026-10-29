using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Undo and Redo: package snapshots before each change, and putting one back.</summary>
public sealed partial class ExmodEditorViewModel
{
    /// <summary>Snapshots the package before a change. A new change (not Redo itself) ends any redo chain.</summary>
    private void PushUndoSnapshot() => PushUndoSnapshot(clearRedo: true);

    private void PushUndoSnapshot(bool clearRedo)
    {
        if (_restoringSnapshot && clearRedo)
        {
            return;
        }

        _lastEditedField = null;
        var hadRedo = _history.CanRedo;
        _history.Push(CurrentSnapshot(), clearRedo);
        if (hadRedo != _history.CanRedo)
        {
            OnPropertyChanged(nameof(CanRedo));
        }

        OnPropertyChanged(nameof(CanUndo));
    }

    private string CurrentSnapshot() => ExmodJson.ToJsonObject(_package).ToJsonString();

    public bool CanUndo => _history.CanUndo;

    public bool CanRedo => _history.CanRedo;

    /// <summary>
    /// Called by a field row just before it writes a new value: the first write to a field after
    /// anything else (another field, a structural action, an Undo) snapshots first, so Undo takes
    /// back that field's typing as one change and never the typing along with an earlier action.
    /// </summary>
    private void SnapshotBeforeFieldEdit(string currentFile, string itemName, string fieldName)
    {
        var key = (currentFile, itemName, fieldName);
        if (_lastEditedField == key)
        {
            return;
        }

        PushUndoSnapshot();
        _lastEditedField = key;
    }

    /// <summary>Reverts the last change (see ExmodUndoHistory for exactly what counts); what it took away goes on the redo stack.</summary>
    [RelayCommand]
    private void Undo()
    {
        if (_history.Undo(CurrentSnapshot()) is not { } snapshotJson)
        {
            StatusMessage = "Nothing to undo.";
            return;
        }

        RestoreSnapshot(snapshotJson);
        StatusMessage = "Undid last action.";
    }

    /// <summary>Puts back what the last Undo took away (until a new change is made).</summary>
    [RelayCommand]
    private void Redo()
    {
        if (_history.Redo(CurrentSnapshot()) is not { } snapshotJson)
        {
            StatusMessage = "Nothing to redo.";
            return;
        }

        RestoreSnapshot(snapshotJson);
        StatusMessage = "Redid last action.";
    }

    private void RestoreSnapshot(string snapshotJson)
    {
        _lastEditedField = null;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));

        var snapshot = ExmodJson.Parse(snapshotJson);
        var previousFile = SelectedItem?.CurrentFile;
        var previousName = SelectedItem?.ItemName;
        _restoringSnapshot = true;
        try
        {
            ApplyParsedPackage(snapshot, reloadRules: !_scaleRulesDirty);
            SelectedItem = Items.FirstOrDefault(i => Same(i.CurrentFile, previousFile) && Same(i.ItemName, previousName)) ?? Items.FirstOrDefault();
        }
        finally
        {
            _restoringSnapshot = false;
        }

        _lastEditedField = null;
    }

    /// <summary>Set while Undo/Redo rebuild the view: anything that writes then is the restore, not a new change to snapshot.</summary>
    private bool _restoringSnapshot;
}
