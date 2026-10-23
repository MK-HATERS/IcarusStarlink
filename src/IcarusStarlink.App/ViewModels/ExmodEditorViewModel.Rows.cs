using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Commands that add or copy whole rows: mass edit, Duplicate, Copy a game row, Insert file, Merge another mod.</summary>
public sealed partial class ExmodEditorViewModel
{
    [RelayCommand]
    private void ApplyMassEdit()
    {
        if (_selectedItemsForMassEdit.Count < 2)
        {
            MassEditStatusMessage = "Select 2 or more items first.";
            return;
        }

        MassEditFieldName = MassEditFieldName.Trim();
        if (string.IsNullOrWhiteSpace(MassEditFieldName))
        {
            MassEditStatusMessage = "Field name is required.";
            return;
        }

        if (IsReservedFieldName(MassEditFieldName))
        {
            MassEditStatusMessage = "'Name' is each item's own identity, not a field.";
            return;
        }

        JsonNode? parsedValue;
        try
        {
            parsedValue = string.IsNullOrWhiteSpace(MassEditFieldValue) ? null : JsonNode.Parse(MassEditFieldValue);
        }
        catch (Exception ex)
        {
            MassEditStatusMessage = $"Invalid JSON value: {ex.Message}";
            return;
        }

        PushUndoSnapshot();
        var count = 0;
        foreach (var editorItem in _selectedItemsForMassEdit)
        {
            var item = FindItem(editorItem.CurrentFile, editorItem.ItemName);
            if (item is null)
            {
                continue;
            }

            // Each item's Fields dictionary needs its own JsonNode instance — a JsonNode can only
            // ever belong to one parent, the same constraint ExmodBaseDiffer.ToKeyedObject already
            // works around by deep-cloning.
            item.Fields[MassEditFieldName] = parsedValue?.DeepClone();
            count++;
        }

        RefreshBaseDiff();
        PopulateFieldsForSelection();
        foreach (var row in _selectedItemsForMassEdit)
        {
            Recount(row);
        }

        MassEditStatusMessage = $"Set '{MassEditFieldName}' on {count} item(s).";
    }

    /// <summary>Ctrl+D — clones the selected item's own fields into a new item under the same file, named "&lt;Name&gt;_Copy" (or "_Copy2"/"_Copy3"/... if that's already taken).</summary>
    [RelayCommand]
    private void DuplicateItem()
    {
        if (SelectedItem is null)
        {
            StatusMessage = "Select an item first.";
            return;
        }

        var sourceItem = FindItem(SelectedItem.CurrentFile, SelectedItem.ItemName);
        var row = _package.Rows.FirstOrDefault(r => Same(r.CurrentFile, SelectedItem.CurrentFile));
        if (sourceItem is null || row is null)
        {
            return;
        }

        var newName = $"{sourceItem.Name}_Copy";
        var suffix = 2;
        while (row.FileItems.Any(i => Same(i.Name, newName)))
        {
            newName = $"{sourceItem.Name}_Copy{suffix}";
            suffix++;
        }

        var clonedFields = new Dictionary<string, JsonNode?>();
        foreach (var (fieldName, value) in sourceItem.Fields)
        {
            clonedFields[fieldName] = value?.DeepClone();
        }

        PushUndoSnapshot();
        row.FileItems.Add(new ExmodFileItem { Name = newName, Fields = clonedFields });
        RefreshBaseDiff();

        var newEditorItem = MakeItem(row.CurrentFile, newName);
        Items.Add(newEditorItem);
        SyncItemOrder();
        SelectedItem = newEditorItem;
        StatusMessage = $"Duplicated '{sourceItem.Name}' as '{newName}'.";
    }

    /// <summary>
    /// Classic IMM's own "Add Item to Mod" — its changelog's single most-iterated editor feature,
    /// credited with drastically cutting the time to build custom items. Picks any real in-game
    /// item and copies its COMPLETE current game values into this mod as a starting point — the
    /// user then edits the few fields they care about, instead of hand-assembling an item from
    /// blank boxes field by field. A copy left unedited is identical to base, so it merges as a
    /// no-op — safe to add speculatively.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task AddItemFromGameAsync()
    {
        IReadOnlyList<GameDataItemRef> gameItemIndex;
        StatusMessage = "Reading the game data folder…";
        try
        {
            gameItemIndex = await _gameDataIndexCache.GetItemIndexAsync(_dataFolder);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't read the game data folder: {ex.Message}";
            return;
        }

        StatusMessage = null;

        if (gameItemIndex.Count == 0)
        {
            StatusMessage = "No game data found — run Refresh game data in Settings first.";
            return;
        }

        // What this mod already touches, for the picker's own "hide items already in this mod"
        // toggle (classic IMM's View-Originals-with-modded-hidden workflow).
        var coveredKeys = _package.Rows
            .SelectMany(r => r.FileItems.Select(i => PickGameItemDialog.CoverageKey(r.CurrentFile, i.Name)))
            .ToHashSet();

        var dialog = new PickGameItemDialog(gameItemIndex, coveredKeys) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.SelectedItem is not { } picked)
        {
            return;
        }

        var row = _package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, picked.CurrentFile, StringComparison.OrdinalIgnoreCase));
        if (row is not null && row.FileItems.Any(i => Same(i.Name, picked.ItemName)))
        {
            StatusMessage = $"'{picked.ItemName}' is already part of this mod.";
            SelectedItem = Items.FirstOrDefault(i => Same(i.CurrentFile, picked.CurrentFile) && Same(i.ItemName, picked.ItemName)) ?? SelectedItem;
            return;
        }

        JsonObject keyed;
        try
        {
            var fileJson = JsonNode.Parse(File.ReadAllText(Path.Combine(_dataFolder, picked.RealPath.Replace('/', Path.DirectorySeparatorChar))))!.AsObject();
            keyed = DataTableJson.RowsToKeyedObject(fileJson);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't read '{picked.RealPath}': {ex.Message}";
            return;
        }

        if (keyed[picked.ItemName] is not JsonObject fields)
        {
            StatusMessage = $"'{picked.ItemName}' isn't in '{picked.RealPath}' anymore — the index may be stale; reopen the mod editor after a Refresh game data.";
            return;
        }

        PushUndoSnapshot();
        if (row is null)
        {
            row = new ExmodFileRow { CurrentFile = picked.CurrentFile };
            _package.Rows.Add(row);
        }

        var item = new ExmodFileItem { Name = picked.ItemName };
        // Each field needs its own JsonNode instance — a node can only ever have one parent, the
        // same constraint DuplicateItem/MergeExistingMod already document.
        foreach (var (fieldName, value) in fields)
        {
            item.Fields[fieldName] = value?.DeepClone();
        }

        row.FileItems.Add(item);

        RefreshBaseDiff();
        ReloadItems();
        SelectedItem = Items.FirstOrDefault(i => Same(i.CurrentFile, picked.CurrentFile) && Same(i.ItemName, picked.ItemName));
        StatusMessage = $"Added '{picked.ItemName}' with all {item.Fields.Count} of its real game values — edit what you want changed.";
    }

    /// <summary>
    /// "Insert file at location" — classic IMM's own name for picking one of the game's own real
    /// data files from a browsable list, rather than free-typing its path the way Add item's own
    /// NewItemCurrentFile box requires. Adds it with zero items yet; Add item (or Add field once an
    /// item exists) is still what actually puts content into it.
    /// </summary>
    [RelayCommand]
    private void InsertFileAtLocation()
    {
        var dialog = new PickFileDialog(_dataFolder) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.SelectedCurrentFile is not { } currentFile)
        {
            return;
        }

        if (_package.Rows.Any(r => string.Equals(r.CurrentFile, currentFile, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"'{currentFile}' is already part of this mod.";
            return;
        }

        PushUndoSnapshot();
        var previousFile = SelectedItem?.CurrentFile;
        var previousName = SelectedItem?.ItemName;
        _package.Rows.Add(new ExmodFileRow { CurrentFile = currentFile });
        RefreshBaseDiff();
        ReloadItems();
        SelectedItem = Items.FirstOrDefault(i => Same(i.CurrentFile, previousFile) && Same(i.ItemName, previousName)) ?? Items.FirstOrDefault();
        StatusMessage = $"Added '{currentFile}' with no rows yet — add one with More ▾ › Copy a game row into this mod…, or Add an empty row.";
    }

    /// <summary>
    /// "Merge existing mod into this one" — copies another Library mod's own items into this one.
    /// An item this mod already has (same CurrentFile + item name) is left alone, never overwritten
    /// — this is a one-time fold-in action with no queue/conflict-picker semantics behind it, so
    /// "don't touch what's already there" is the one safe default. Binary assets aren't part of
    /// this (per the dialog's own description, "copies every item" — the picked mod's own real
    /// .uasset/.uexp files aren't something this in-memory JSON editor touches at all; a mod that
    /// genuinely needs the other one's assets too still needs those copied by hand into its folder).
    /// </summary>
    [RelayCommand]
    private void MergeExistingMod()
    {
        var candidates = _repository.GetAll()
            .Where(e => !e.IsOpaquePak && !string.Equals(e.FolderName, _folderName, StringComparison.OrdinalIgnoreCase));

        var dialog = new PickModDialog(candidates) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.SelectedEntry is not { } selected)
        {
            return;
        }

        ExmodPackage sourcePackage;
        try
        {
            sourcePackage = ExmodFolder.Read(_repository.GetFolderPath(selected.FolderName)).Package;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't read '{selected.Name}': {ex.Message}";
            return;
        }

        PushUndoSnapshot();
        var previousFile = SelectedItem?.CurrentFile;
        var previousName = SelectedItem?.ItemName;
        var addedCount = 0;
        foreach (var sourceRow in sourcePackage.Rows)
        {
            var targetRow = _package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, sourceRow.CurrentFile, StringComparison.OrdinalIgnoreCase));
            if (targetRow is null)
            {
                targetRow = new ExmodFileRow { CurrentFile = sourceRow.CurrentFile };
                _package.Rows.Add(targetRow);
            }

            foreach (var sourceItem in sourceRow.FileItems)
            {
                if (targetRow.FileItems.Any(i => Same(i.Name, sourceItem.Name)))
                {
                    continue;
                }

                // Each item's own Fields dictionary needs its own JsonNode instances — a JsonNode
                // can only ever belong to one parent, same reasoning DuplicateItem already documents.
                var clonedFields = new Dictionary<string, JsonNode?>();
                foreach (var (fieldName, value) in sourceItem.Fields)
                {
                    clonedFields[fieldName] = value?.DeepClone();
                }

                targetRow.FileItems.Add(new ExmodFileItem { Name = sourceItem.Name, Fields = clonedFields });
                addedCount++;
            }
        }

        RefreshBaseDiff();
        ReloadItems();
        SelectedItem = Items.FirstOrDefault(i => Same(i.CurrentFile, previousFile) && Same(i.ItemName, previousName)) ?? Items.FirstOrDefault();
        StatusMessage = addedCount == 0
            ? $"Nothing new to merge — every item in '{selected.Name}' is already here."
            : $"Merged {addedCount} item(s) from '{selected.Name}'.";
    }
}
