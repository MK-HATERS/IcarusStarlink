namespace IcarusStarlink.App.ViewModels;

/// <summary>Which of the editor's views is showing in the right-hand pane. (The old raw JSON views are gone: the Fields view edits every field, lists included.)</summary>
public enum ExmodEditorViewMode
{
    ItemFields,

    /// <summary>"Scale a field across a table" rules (ExmodEditorViewModel.Scale.cs).</summary>
    ScaleRules,

    /// <summary>Game rows of this mod's tables that it doesn't have yet (ExmodEditorViewModel.NotInMod.cs).</summary>
    NotInMod,
}
