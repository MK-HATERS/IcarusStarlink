using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <param name="OrderedChanges">Ordered by merge-queue position: index 0 is lowest priority.</param>
/// <param name="HasBaseValue">
/// True when MergeEngine.Merge was given a live base-game value for this exact (CurrentFile,
/// ItemName, FieldName) — false covers both "no baseTablesByFile was given at all" and "it was
/// given, but this field genuinely has no base value" (a brand-new item, or a field absent from an
/// existing base row), matching MergeEngine's own TryGetBaseValue "Found" convention. A rule that
/// needs to reason about the base value (e.g. ArrayUnionCombineRule's own "is every mod's change a
/// pure addition over base" check) must check this first — BaseValue alone can't distinguish "no
/// base known" from "the base value is a real JSON null" the way FieldChange.IsFieldRemoved has to.
/// </param>
/// <param name="BaseValue">Only meaningful when HasBaseValue is true.</param>
public sealed record FieldChangeGroup(
    string CurrentFile,
    string ItemName,
    string FieldName,
    IReadOnlyList<FieldChange> OrderedChanges,
    bool HasBaseValue = false,
    JsonNode? BaseValue = null);
