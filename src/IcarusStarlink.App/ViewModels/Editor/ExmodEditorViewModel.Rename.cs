using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Views;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// More ▾ › Rename this row… (F2): a row the mod adds gets a new name, and every link to it inside
/// the mod follows (RowRenamer). A mod's copy of a game row is refused: it changes the game's row
/// by having its name, so a new name would add a new row instead.
/// </summary>
public sealed partial class ExmodEditorViewModel
{
    /// <summary>Asks for the row's new name (current name, explanation): the typed name, or null when cancelled. Tests swap in an answer.</summary>
    internal Func<string, string, string?> AskNewRowName
    {
        get => _askNewRowName ??= (current, description) =>
        {
            var dialog = new RenameModDialog(current, description, title: "Rename this row", fieldLabel: "New row name", showReset: false) { Owner = OwnerWindow() };
            return dialog.ShowDialog() == true ? dialog.NewDisplayName : null;
        };
        set => _askNewRowName = value;
    }

    private Func<string, string, string?>? _askNewRowName;

    /// <summary>Renames the selected row and re-points the mod's links to it. One Undo step.</summary>
    [RelayCommand]
    private void RenameRow()
    {
        if (SelectedItem is not { } selected || FindItem(selected.CurrentFile, selected.ItemName) is not { } item || Same(selected.CurrentFile, "EndOfMod"))
        {
            StatusMessage = "Select a row first.";
            return;
        }

        var oldName = item.Name;
        if (RowRenamer.RowProblem(GameTables, selected.CurrentFile, oldName) is { } rowProblem)
        {
            StatusMessage = $"Can't rename: {rowProblem}";
            return;
        }

        var typed = AskNewRowName(oldName,
            $"A new name for this row of {GameTableIndex.TableNameOf(selected.CurrentFile)}. Links to it in this mod are changed to the new name too. Letters, digits and _ only.");
        if (typed is null)
        {
            return;
        }

        var newName = typed.Trim();
        if (RowRenamer.Problem(_package, GameTables, selected.CurrentFile, oldName, newName) is { } problem)
        {
            StatusMessage = $"Not renamed: {problem}";
            return;
        }

        PushUndoSnapshot();
        var result = RowRenamer.Rename(_package, GameTables, selected.CurrentFile, oldName, newName);
        RefreshBaseDiff();
        ReloadItems();
        SelectedItem = Items.FirstOrDefault(i => Same(i.CurrentFile, selected.CurrentFile) && Same(i.ItemName, result.NewName)) ?? Items.FirstOrDefault();
        StatusMessage = $"Renamed '{oldName}' to '{result.NewName}'"
            + (result.LinksMoved == 0 ? "; nothing in this mod linked to it." : $" and pointed {result.LinksMoved} link(s) in this mod at the new name.")
            + " Links from other mods still use the old name. Undo (Ctrl+Z) puts it back; Save to keep it.";
    }
}
