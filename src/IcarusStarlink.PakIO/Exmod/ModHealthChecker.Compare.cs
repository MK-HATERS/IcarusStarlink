using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>Comparing one mod value with the game's: the verdict (equal, older, mixed, changed), merging lists, and list differences.</summary>
public static partial class ModHealthChecker
{
    /// <summary>
    /// How the mod's value relates to the game's. A list missing some of the game's entries is
    /// Older. An object missing some of the game's keys is the merge taking those keys away (it
    /// replaces the whole field): the author's change in a hand-made mod, and an old copy's shape
    /// (the game added the keys since) only when <paramref name="leftOutKeysAreOld"/> (a prebuilt
    /// pak's whole rows).
    /// </summary>
    /// <param name="onlyModsKeys">The field merges entry by entry (StatEntries), so a key the mod leaves out keeps the game's value: only the mod's own keys are compared.</param>
    private static Verdict Judge(JsonNode? mod, JsonNode? game, bool leftOutKeysAreOld = false, bool onlyModsKeys = false)
    {
        if (JsonNode.DeepEquals(mod, game))
        {
            return Verdict.Equal;
        }

        if (mod is JsonArray modList && game is JsonArray gameList)
        {
            var (added, missing, modified) = ListDifference(modList, gameList);
            return (added, missing, modified) switch
            {
                // A changed entry (same item, another count or value) is the author's change.
                (_, _, > 0) => Verdict.Changed,
                (0, > 0, _) => Verdict.Older,
                // Both added and missing: only simple lists (tags, links) can take both sides;
                // merging a recipe's inputs would make it need the old and the new ingredient.
                (> 0, > 0, _) when IsSimpleList(modList) && IsSimpleList(gameList) => Verdict.Mixed,
                _ => Verdict.Changed,
            };
        }

        if (mod is JsonObject modObject && game is JsonObject gameObject)
        {
            var leftOut = !onlyModsKeys && gameObject.Any(kv => !modObject.ContainsKey(kv.Key));
            if (leftOut && !leftOutKeysAreOld)
            {
                // A hand-made mod leaving a key out takes it away in game: the author's change.
                return Verdict.Changed;
            }

            var verdicts = modObject.Select(kv => gameObject.ContainsKey(kv.Key) ? Judge(kv.Value, gameObject[kv.Key], leftOutKeysAreOld) : Verdict.Changed).ToList();
            if (leftOut)
            {
                // A prebuilt pak's whole row: the game added the keys since.
                verdicts.Add(Verdict.Older);
            }

            var older = verdicts.Contains(Verdict.Older);
            var changed = verdicts.Contains(Verdict.Changed);
            if (verdicts.Contains(Verdict.Mixed) || (older && changed))
            {
                // Only lists can be merged; an old value next to a real change in one object stays the author's.
                return HasOnlyListDifferences(modObject, gameObject) ? Verdict.Mixed : Verdict.Changed;
            }

            return older ? Verdict.Older : changed ? Verdict.Changed : Verdict.Equal;
        }

        return Verdict.Changed;
    }

    /// <summary>
    /// Every difference is a list the mod emptied ([] where the game has entries), at any depth: the
    /// author's edit (an emptied cost list is how "everything free" works), not a sign the value was
    /// copied from an older game, which seldom had an empty list where today's game has entries.
    /// An object leaving out keys the game has differs in more than lists.
    /// </summary>
    private static bool IsOnlyEmptiedLists(JsonNode? mod, JsonNode? game) => (mod, game) switch
    {
        (JsonArray { Count: 0 }, JsonArray { Count: > 0 }) => true,
        (JsonObject m, JsonObject g) => g.All(kv => m.ContainsKey(kv.Key))
            && m.Any(kv => !JsonNode.DeepEquals(kv.Value, g[kv.Key]))
            && m.All(kv => g.TryGetPropertyValue(kv.Key, out var gv) && (JsonNode.DeepEquals(kv.Value, gv) || IsOnlyEmptiedLists(kv.Value, gv))),
        _ => false,
    };

    private static bool HasOnlyListDifferences(JsonObject mod, JsonObject game) =>
        mod.All(kv => game.ContainsKey(kv.Key) && (JsonNode.DeepEquals(kv.Value, game[kv.Key])
            || (kv.Value is JsonArray && game[kv.Key] is JsonArray)
            || (kv.Value is JsonObject m && game[kv.Key] is JsonObject g && HasOnlyListDifferences(m, g))));

    /// <summary>The game's current value plus everything the mod added (entries it lacks stay: the game added them).</summary>
    private static JsonNode? Merge(JsonNode? mod, JsonNode? game)
    {
        if (mod is JsonArray modList && game is JsonArray gameList)
        {
            var merged = new JsonArray(gameList.Select(e => e?.DeepClone()).ToArray());
            var identities = gameList.Select(Identity).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in modList.Where(e => !identities.Contains(Identity(e))))
            {
                merged.Add(entry?.DeepClone());
            }

            return merged;
        }

        if (mod is JsonObject modObject && game is JsonObject gameObject)
        {
            var merged = new JsonObject();
            foreach (var (key, gameValue) in gameObject)
            {
                merged[key] = modObject.ContainsKey(key) ? Merge(modObject[key], gameValue) : gameValue?.DeepClone();
            }

            foreach (var (key, modValue) in modObject.Where(kv => !gameObject.ContainsKey(kv.Key)))
            {
                merged[key] = modValue?.DeepClone();
            }

            return merged;
        }

        return mod?.DeepClone();
    }

    /// <summary>
    /// (entries only the mod has, entries only the game has, entries both have but with other
    /// values). Entries are matched by what they are (Identity), so a recipe input whose count
    /// changed is one changed entry, not one added and one missing.
    /// </summary>
    private static (int Added, int Missing, int Modified) ListDifference(JsonArray mod, JsonArray game)
    {
        var remaining = game.ToList();
        int added = 0, modified = 0;
        foreach (var entry in mod)
        {
            var exact = remaining.FindIndex(e => JsonNode.DeepEquals(e, entry));
            if (exact >= 0)
            {
                remaining.RemoveAt(exact);
                continue;
            }

            var identity = Identity(entry);
            var same = remaining.FindIndex(e => string.Equals(Identity(e), identity, StringComparison.OrdinalIgnoreCase));
            if (same >= 0)
            {
                remaining.RemoveAt(same);
                modified++;
            }
            else
            {
                added++;
            }
        }

        return (added, remaining.Count, modified);
    }

    /// <summary>What a list entry is: its tag, linked row, item or property; the whole entry when none of those.</summary>
    private static string Identity(JsonNode? entry) => entry switch
    {
        JsonObject o when o["TagName"] is JsonValue tag => "tag:" + tag,
        JsonObject o when o["Element"] is JsonObject element && element["RowName"] is JsonValue row => "element:" + row,
        JsonObject o when o["Item"] is JsonObject item && item["RowName"] is JsonValue row => "item:" + row,
        JsonObject o when o["Meta"] is JsonObject meta && meta["RowName"] is JsonValue row => "meta:" + row,
        JsonObject o when o["PropertyType"] is JsonValue property => "property:" + property,
        JsonObject o when o["RowName"] is JsonValue row => "row:" + row + "/" + o["DataTableName"],
        _ => entry?.ToJsonString() ?? "null",
    };

    /// <summary>A list of plain values, tags or row links: one side's entries can be added to the other's.</summary>
    private static bool IsSimpleList(JsonArray list) =>
        list.All(e => e is JsonValue
            || (e is JsonObject o && o.All(kv => kv.Key is "TagName" or "RowName" or "DataTableName" && kv.Value is JsonValue)));

    private static (int Added, int Missing) TotalListDifference(JsonNode? mod, JsonNode? game)
    {
        if (mod is JsonArray m && game is JsonArray g)
        {
            var (added, missing, _) = ListDifference(m, g);
            return (added, missing);
        }

        if (mod is JsonObject mo && game is JsonObject go)
        {
            // Keys the game has and the mod leaves out count as missing entries too.
            var total = (Added: 0, Missing: go.Count(kv => !mo.ContainsKey(kv.Key)));
            foreach (var (key, value) in mo.Where(kv => go.ContainsKey(kv.Key)))
            {
                var (a, b) = TotalListDifference(value, go[key]);
                total = (total.Added + a, total.Missing + b);
            }

            return total;
        }

        return (0, 0);
    }
}
