namespace IcarusStarlink.App.ViewModels;

/// <summary>One FileItems entry in the EXMOD editor's left-hand list — deliberately a plain class, not observable: CurrentFile/ItemName never change after construction (renaming either would need its own guard against the multi-.EXMOD-in-one-folder ambiguity ExmodFolder.Read already rejects, out of scope for 7.1).</summary>
public sealed class EditorItemViewModel(string currentFile, string itemName)
{
    public string CurrentFile { get; } = currentFile;
    public string ItemName { get; } = itemName;

    /// <summary>The real game data relative path, e.g. "Traits-D_Fuel.json" -> "Traits/D_Fuel.json" — same convention confirmed throughout Phase 6.</summary>
    public string RealPath => CurrentFile.Replace('-', '/');

    /// <summary>The list's heading for this row: the part of the game it changes ("Armour", "Itemable", "Processor recipes").</summary>
    public string Group { get; init; } = "";

    /// <summary>The in-game name of the item this row belongs to ("Xigo S5-II Envirosuit"), else the row's own name.</summary>
    public string Title { get; init; } = itemName;

    /// <summary>The game item this row belongs to, for "Open in Editor"; null for rows no single item uses.</summary>
    public string? GameItem { get; init; }

    /// <summary>How many of the row's values differ from the game's (0 for a row the game doesn't have).</summary>
    public int ChangeCount { get; init; }

    public bool HasChanges => ChangeCount > 0;

    /// <summary>Searchable text: file, row and in-game name.</summary>
    public string Display => $"{RealPath} — {ItemName}";

    public string SearchText => $"{Display} {Title} {Group}";
}
