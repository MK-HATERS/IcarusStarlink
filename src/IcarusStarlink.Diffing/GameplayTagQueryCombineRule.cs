using System.Text.Json.Nodes;

namespace IcarusStarlink.Diffing;

/// <summary>
/// Two mods that both add entries to the same tag-query list should keep both ("Take Home Tools +
/// Resources keeps both"), not have one silently overwrite the other like a plain scalar field
/// would. The entries are flattened and deduped by JSON text.
///
/// Only list values are combined. The game's real tag queries aren't lists:
/// D_AISetup.ValidBaitTagQuery and D_WorldBosses.SpawnerTagQuery are FGameplayTagQuery objects,
/// and the other *TagQuery fields are RowName links into D_TagQueries. An object or a plain value
/// is one value, so two mods setting it go to LastWriteWinsRule: equal values stay one value, and
/// different ones are an ordinary conflict the later mod wins.
/// </summary>
public sealed class GameplayTagQueryCombineRule : IFieldMergeRule
{
    public bool Applies(FieldChangeGroup group) =>
        // Only kick in for an actual multi-mod conflict — a lone mod's change should pass
        // through LastWriteWinsRule untouched rather than get normalized into a JsonArray.
        group.OrderedChanges.Count > 1
        // Every change must agree on the semantic, not just the first — two mods can set
        // structurally different shapes for the same field name (e.g. one looks like a
        // RowReference), which would otherwise get spliced into the combined array below.
        && group.OrderedChanges.All(c => c.Semantic == ValueSemantic.GameplayTagQuery)
        // If any mod removes the field, don't try to combine — defer to LastWriteWinsRule so
        // the removal is honored like it would be for any other field, instead of the deletion
        // turning into a literal null entry inside the union.
        && group.OrderedChanges.All(c => !c.IsFieldRemoved)
        // A query object (a creature's bait query) is one value: wrapping it in a list breaks it.
        && group.OrderedChanges.All(c => c.NewValue is JsonArray);

    public FieldChange Resolve(FieldChangeGroup group)
    {
        var combined = new JsonArray();
        var seen = new HashSet<string>();

        foreach (var change in group.OrderedChanges)
        {
            foreach (var entry in Flatten(change.NewValue))
            {
                var text = entry?.ToJsonString() ?? "null";
                if (seen.Add(text))
                {
                    combined.Add(entry?.DeepClone());
                }
            }
        }

        return group.OrderedChanges[^1] with { NewValue = combined };
    }

    /// <summary>A list's entries; anything else (only reached when Resolve is called without Applies) as one entry.</summary>
    private static IEnumerable<JsonNode?> Flatten(JsonNode? value)
    {
        if (value is JsonArray array)
        {
            foreach (var item in array)
            {
                yield return item;
            }
        }
        else
        {
            yield return value;
        }
    }
}
