namespace IcarusStarlink.Diffing;

/// <summary>
/// Two or more DIFFERENT mods in the queue each declare a brand-new item under the exact same
/// (CurrentFile, ItemName) — a genuinely different situation from a FieldConflict (which is a
/// disagreement about one field's VALUE): here, the two mods can touch entirely non-overlapping
/// fields and still collide, purely because they happened to pick the same name for what are
/// really two unrelated additions. MergeRuleRegistry's own IsNewItem OR-ing (see its own doc
/// comment) then silently splices every mod's own fields into a single merged item, with nothing
/// telling the user that happened. See MergeEngine.FindNewItemNameCollisions' own doc comment for
/// how this is detected.
///
/// ModNames is ordered by merge-queue position (matching FieldConflict.Candidates' own
/// convention), even though there's no "candidate value" being compared here — just which mods are
/// involved, for display.
/// </summary>
public sealed record NewItemNameCollision(string CurrentFile, string ItemName, IReadOnlyList<string> ModNames);
