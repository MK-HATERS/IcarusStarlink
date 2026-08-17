using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <param name="IsNewItem">
/// True when ItemName did not exist in the base table this change was diffed against — lets
/// TableApplier tell "row legitimately added by a mod" apart from "row an upstream game patch
/// removed", which look identical (missing row) at apply time otherwise.
/// </param>
/// <param name="IsFieldRemoved">
/// True when FieldName is absent from the modded row entirely. NewValue == null is ambiguous on
/// its own — System.Text.Json.Nodes represents both "key absent" and "key present with an
/// explicit JSON null" as the same C# null reference — so this is the actual signal TableApplier
/// uses to decide between removing the key and setting it to an explicit null.
/// </param>
public sealed record FieldChange(
    string CurrentFile,
    string ItemName,
    string FieldName,
    JsonNode? OriginalValue,
    JsonNode? NewValue,
    ValueSemantic Semantic,
    bool IsNewItem = false,
    bool IsFieldRemoved = false);
