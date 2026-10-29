namespace IcarusStarlink.PakIO.Exmod;

/// <summary>How to handle one issue.</summary>
public enum ModIssueFix
{
    KeepMod,
    UseGame,
    Merge,
    RenameRow,
    RemoveRow,

    /// <summary>Point the mod's file at the game's new place for what it uses (MissingAsset; the place is SuggestedRow).</summary>
    Repoint,

    /// <summary>Move the rows to the game's table of that name (UnknownTable; the CurrentFile is SuggestedRow).</summary>
    RenameTable,

    /// <summary>Give the field the game's name for it (UnknownField; the name is SuggestedRow).</summary>
    RenameField,

    /// <summary>Give the text its own key (SharedTextKey; the key is SuggestedRow).</summary>
    OwnTextKey,
}
