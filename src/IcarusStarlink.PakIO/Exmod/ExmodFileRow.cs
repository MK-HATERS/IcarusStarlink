namespace IcarusStarlink.PakIO.Exmod;

/// <summary>One "Rows" entry — all the changed items within a single game DataTable JSON file.</summary>
public sealed class ExmodFileRow
{
    /// <summary>The game file this row's items belong to, e.g. "Crafting-D_ProcessorRecipes.json".</summary>
    public required string CurrentFile { get; set; }

    public List<ExmodFileItem> FileItems { get; set; } = [];
}
