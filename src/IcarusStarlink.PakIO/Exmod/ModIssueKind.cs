namespace IcarusStarlink.PakIO.Exmod;

/// <summary>What a flagged change in a mod is, judged against the game's current data.</summary>
public enum ModIssueKind
{
    /// <summary>Already the game's value: does nothing.</summary>
    SameAsGame,

    /// <summary>An older copy of the game's value (the game has since added to it or changed it): only brings old data back.</summary>
    OldCopy,

    /// <summary>A real-looking change on a row the mod otherwise copied from an older game version: probably old too.</summary>
    LikelyOld,

    /// <summary>A list where the mod added entries and the game has entries the mod lacks (most likely added since).</summary>
    MixedList,

    /// <summary>A small edit to a row the game no longer has.</summary>
    MissingRow,

    /// <summary>
    /// A link, at any depth of any row (the mod's own new rows too), to a row the game doesn't have
    /// and no mod in the Library adds: does nothing in game (a canteen you can't drink from, an
    /// item with no model). Sure only for a game row's own link field where the game's link is real
    /// and the game's row history proves the game had the row the mod links to (and every Library
    /// mod's new rows are known); otherwise only listed, kept as the mod has it.
    /// </summary>
    BrokenLink,

    /// <summary>
    /// A whole row from an older game version that the game has since removed (laanp's paks carry
    /// Cobalt Ingot, Electric Motor…): in a prebuilt pak (or a table copied wholesale), not in the
    /// game, used by nothing else in the mod, and either known from the game's row history or
    /// carried by another Library mod too. Harmless, so only tidying.
    /// </summary>
    OldGameRow,

    /// <summary>
    /// A file of the mod uses a game file (a material, mesh, class…) the game doesn't have under
    /// that path: the game stops when it loads something built on it, or that part of the mod does
    /// nothing. Sure when the game moved it (same name and objects, one other folder): the fix
    /// points the mod there. Otherwise only listed.
    /// </summary>
    MissingAsset,

    /// <summary>A file of the mod uses parts of a game file the game has since taken out of it (a component of a game blueprint): those parts of the mod do nothing. Only listed.</summary>
    MissingAssetPart,

    /// <summary>
    /// A CurrentFile that names no game table: the merge skips every row in it. Sure when the game
    /// has a table of that name in one place (the folder part is missing or wrong): the fix moves
    /// the rows there (SuggestedRow is that CurrentFile). ItemName is empty.
    /// </summary>
    UnknownTable,

    /// <summary>
    /// A field the table doesn't have: the game ignores it, so the change does nothing. SuggestedRow
    /// is the field it most likely means (the same name in other case: sure, matching the spelling;
    /// a close name: a guess).
    /// </summary>
    UnknownField,

    /// <summary>A value of another kind than the game's (text where the game has a number): the game may ignore it. Only listed.</summary>
    WrongType,

    /// <summary>
    /// Text (NSLOCTEXT) using the key of another row's text with other words: two texts under one
    /// key, so the game can show one in place of the other. The fix gives it its own key
    /// ("&lt;Row&gt;-&lt;Field&gt;", SuggestedRow); the words stay.
    /// </summary>
    SharedTextKey,
}
