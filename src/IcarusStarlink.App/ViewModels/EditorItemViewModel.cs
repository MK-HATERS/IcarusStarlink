namespace IcarusStarlink.App.ViewModels;

/// <summary>One FileItems entry in the EXMOD editor's left-hand list — deliberately a plain class, not observable: CurrentFile/ItemName never change after construction (renaming either would need its own guard against the multi-.EXMOD-in-one-folder ambiguity ExmodFolder.Read already rejects, out of scope for 7.1).</summary>
public sealed class EditorItemViewModel(string currentFile, string itemName)
{
    public string CurrentFile { get; } = currentFile;
    public string ItemName { get; } = itemName;

    /// <summary>The real game data relative path, e.g. "Traits-D_Fuel.json" -> "Traits/D_Fuel.json" — same convention confirmed throughout Phase 6.</summary>
    public string RealPath => CurrentFile.Replace('-', '/');

    public string Display => $"{RealPath} — {ItemName}";
}
