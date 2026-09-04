using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One field two or more queued mods touch differently. Options[0] is always "Default (last mod
/// wins)" — SelectedOptionIndex 0 means "no manual pick", matching MergeEngine.Merge's own
/// semantics for a key absent from manualPicks; Options[i+1] corresponds to Conflict.Candidates[i].
///
/// Also doubles as a purely informational row for a NewItemNameCollision (see its own second
/// constructor below) — a genuinely different situation (two mods' own new-item additions happen
/// to share a name, not a value disagreement), so Conflict is null for that kind of row.
/// </summary>
public sealed partial class ConflictRowViewModel : ObservableObject
{
    public ConflictRowViewModel(FieldConflict conflict, int? existingPickIndex)
    {
        Conflict = conflict;
        // EXMOD's own dash convention ("Traits-D_Fuel.json") converted back to the real path, same
        // as every other place this app shows CurrentFile to a user.
        Display = $"{conflict.CurrentFile.Replace('-', '/')} — {conflict.ItemName}.{conflict.FieldName}";

        // A reference row, not another candidate to pick between — hidden entirely when no live
        // base value is known (a brand-new item, or FindConflicts was run without base data at
        // all) rather than showing a misleading "Base game value: null".
        BaseValueDisplay = conflict.HasBaseValue ? $"Base game value: {FormatRawValue(conflict.BaseValue)}" : null;

        var lastModName = conflict.Candidates[^1].ModName;
        Options = [
            $"Default (last mod wins: {lastModName})",
            .. conflict.Candidates.Select(c => $"{c.ModName}: {FormatValue(c.Change)}{NumericHintSuffix(conflict, c.Change)}"),
        ];

        _selectedOptionIndex = existingPickIndex.HasValue ? existingPickIndex.Value + 1 : 0;
    }

    /// <summary>
    /// Builds a purely informational row for two or more mods that each add a brand-new item under
    /// the exact same name (see MergeEngine.FindNewItemNameCollisions' own doc comment) — a
    /// genuinely different situation from a field-value disagreement, so this deliberately does NOT
    /// reuse FieldConflict's own "file/item.field" wording: there's nothing to "pick a winner" for
    /// here (every mod's own fields for the item are still merged together either way, exactly as
    /// before), just something worth the user's own attention. Options carries exactly one,
    /// permanently-selected entry, so PickedCandidateIndex always stays null and
    /// ConflictPickerViewModel.BuildPicks never emits a manualPicks entry for it — there's no real
    /// (file, item, field) key this could even mean.
    /// </summary>
    public ConflictRowViewModel(NewItemNameCollision collision)
    {
        Conflict = null;
        Display = $"{collision.CurrentFile.Replace('-', '/')} — '{collision.ItemName}' is added as a NEW item by "
            + $"{collision.ModNames.Count} different mods: {string.Join(", ", collision.ModNames)}. "
            + "These are two unrelated additions that happen to share a name, not a value disagreement.";
        BaseValueDisplay = null;
        Options = ["Acknowledge — every mod's own fields for this item are still merged together"];
        _selectedOptionIndex = 0;
    }

    /// <summary>Null for a NewItemNameCollision row — see that constructor's own doc comment.</summary>
    public FieldConflict? Conflict { get; }

    public string Display { get; }

    /// <summary>Null when no live base-game value is known for this field, or this row is a NewItemNameCollision — the reference row is hidden entirely rather than shown as a misleading "Base game value: null".</summary>
    public string? BaseValueDisplay { get; }

    public IReadOnlyList<string> Options { get; }

    [ObservableProperty]
    private int _selectedOptionIndex;

    /// <summary>Null means "Default" is selected — the key should be left out of the manualPicks dictionary entirely, not mapped to some sentinel index.</summary>
    public int? PickedCandidateIndex => SelectedOptionIndex == 0 ? null : SelectedOptionIndex - 1;

    private static string FormatValue(FieldChange change) =>
        change.IsFieldRemoved ? "(removed)" : FormatRawValue(change.NewValue);

    private static string FormatRawValue(JsonNode? value) => value?.ToJsonString() ?? "null";

    /// <summary>
    /// A simple directional hint (display-only — deliberately not "buff"/"nerf" wording, since
    /// whether a higher or lower value is an improvement depends on the field: a lower CraftTime is
    /// a buff, a lower Damage isn't, and this has no way to know which) for a candidate whose own
    /// value and the live base-game value are BOTH genuinely numeric — Semantic == Scalar alone
    /// isn't enough (DefaultSemanticClassifier buckets strings/bools/null into Scalar too), so this
    /// also checks the JSON value kind directly.
    /// </summary>
    private static string NumericHintSuffix(FieldConflict conflict, FieldChange change)
    {
        if (!conflict.HasBaseValue || change.IsFieldRemoved || change.Semantic != ValueSemantic.Scalar)
        {
            return "";
        }

        if (!TryGetNumeric(change.NewValue, out var candidateValue) || !TryGetNumeric(conflict.BaseValue, out var baseValue))
        {
            return "";
        }

        if (candidateValue > baseValue)
        {
            return "  ▲ above base";
        }

        if (candidateValue < baseValue)
        {
            return "  ▼ below base";
        }

        return "  = same as base";
    }

    // JsonValue.TryGetValue<T>() only succeeds for the exact backing CLR type a value was created
    // with, UNLESS it's backed by a real JsonElement (parsed from JSON text) — which tolerates
    // widening to any numeric T. A hand-built JsonValue.Create(20) (an int, e.g. from
    // GameplayOptionsFieldChangeGenerator's synthetic "Built-in gameplay options" candidate) fails
    // a bare TryGetValue<double>() outright, so every plausible backing type is tried in turn
    // rather than assuming double covers every numeric Scalar this app can ever produce.
    private static bool TryGetNumeric(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue jsonValue || jsonValue.GetValueKind() != JsonValueKind.Number)
        {
            return false;
        }

        if (jsonValue.TryGetValue(out double d)) { value = d; return true; }
        if (jsonValue.TryGetValue(out long l)) { value = l; return true; }
        if (jsonValue.TryGetValue(out int i)) { value = i; return true; }
        if (jsonValue.TryGetValue(out decimal m)) { value = (double)m; return true; }
        if (jsonValue.TryGetValue(out float f)) { value = f; return true; }

        return false;
    }
}
