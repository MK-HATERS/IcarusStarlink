namespace IcarusStarlink.App.ViewModels;

/// <summary>One real DataTable row in the extracted game data — the pickable unit for "Add item from game data". CurrentFile is EXMOD's own dash-flattened form; RealPath keeps the slashes for display and for reading the file back.</summary>
public sealed record GameDataItemRef(string CurrentFile, string RealPath, string ItemName)
{
    /// <summary>A plain label set by a list that knows better ("Canteen — Items"); otherwise the file and row.</summary>
    public string? Label { get; init; }

    public string Display => Label ?? $"{RealPath} — {ItemName}";

    /// <summary>What a screen reader says for the row in a picker (the record's own text otherwise).</summary>
    public override string ToString() => Display;
}
