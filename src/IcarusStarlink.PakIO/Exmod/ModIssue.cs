using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <param name="FieldName">Null for a whole-row issue (MissingRow). A path ("Inputs[0].Element") for a link inside a field.</param>
/// <param name="MergedValue">MixedList and LikelyOld-with-lists: the game's current value plus what the mod added.</param>
/// <param name="IsSure">Safe to fix without asking: the fix keeps everything the mod meant to do.</param>
/// <param name="Remembered">The user chose this fix for this mod before (ModFixDecisions): it's made again by itself, tidying included.</param>
public sealed record ModIssue(
    ModIssueKind Kind,
    string CurrentFile,
    string ItemName,
    string? FieldName,
    JsonNode? ModValue,
    JsonNode? GameValue,
    JsonNode? MergedValue,
    string? SuggestedRow,
    ModIssueFix DefaultFix,
    bool IsSure,
    string Why,
    bool Remembered = false)
{
    public IReadOnlyList<ModIssueFix> Choices => Kind switch
    {
        ModIssueKind.MissingRow => SuggestedRow is null ? [ModIssueFix.RemoveRow, ModIssueFix.KeepMod] : [ModIssueFix.RenameRow, ModIssueFix.RemoveRow, ModIssueFix.KeepMod],
        ModIssueKind.OldGameRow => [ModIssueFix.RemoveRow, ModIssueFix.KeepMod],
        ModIssueKind.MissingAsset when SuggestedRow is not null => [ModIssueFix.Repoint, ModIssueFix.KeepMod],
        ModIssueKind.MissingAsset or ModIssueKind.MissingAssetPart => [ModIssueFix.KeepMod],
        ModIssueKind.UnknownTable => SuggestedRow is null ? [ModIssueFix.KeepMod] : [ModIssueFix.RenameTable, ModIssueFix.KeepMod],
        // Taking out a field the game doesn't have changes nothing in game (UseGame leaves a field out).
        ModIssueKind.UnknownField => SuggestedRow is null ? [ModIssueFix.UseGame, ModIssueFix.KeepMod] : [ModIssueFix.RenameField, ModIssueFix.UseGame, ModIssueFix.KeepMod],
        ModIssueKind.WrongType => [ModIssueFix.KeepMod],
        ModIssueKind.SharedTextKey => [ModIssueFix.OwnTextKey, ModIssueFix.KeepMod],
        // A link inside a field, or in the mod's own row: no game value to go back to, only to look at.
        ModIssueKind.BrokenLink when GameValue is null => [ModIssueFix.KeepMod],
        _ when MergedValue is not null => [ModIssueFix.Merge, ModIssueFix.UseGame, ModIssueFix.KeepMod],
        _ => [ModIssueFix.UseGame, ModIssueFix.KeepMod],
    };

    /// <summary>Changes nothing in game either way: taking it out only makes the mod smaller.</summary>
    public bool IsTidyOnly => Kind is ModIssueKind.SameAsGame or ModIssueKind.OldGameRow;

    /// <summary>A short fixed code for this kind of finding, for bug reports and searching (never renumber one).</summary>
    public string Code => Kind switch
    {
        ModIssueKind.SameAsGame => "ISL-101",
        ModIssueKind.OldCopy => "ISL-102",
        ModIssueKind.LikelyOld => "ISL-103",
        ModIssueKind.MixedList => "ISL-104",
        ModIssueKind.MissingRow => "ISL-105",
        ModIssueKind.BrokenLink => "ISL-106",
        ModIssueKind.OldGameRow => "ISL-107",
        ModIssueKind.MissingAsset => "ISL-108",
        ModIssueKind.MissingAssetPart => "ISL-109",
        ModIssueKind.UnknownTable => "ISL-110",
        ModIssueKind.UnknownField => "ISL-111",
        ModIssueKind.WrongType => "ISL-112",
        ModIssueKind.SharedTextKey => "ISL-113",
        _ => "ISL-100",
    };

    /// <summary>
    /// Made by Fix sure problems without asking: a sure fix to something that matters in game, or a
    /// choice the user made before. Tidying alone never is: a hand-made mod's values that match the
    /// game are its author's (Ferani's Effortless Lights sets AlwaysActive on purpose).
    /// </summary>
    public bool IsAutomatic => IsSure && DefaultFix != ModIssueFix.KeepMod && (!IsTidyOnly || Remembered);
}
