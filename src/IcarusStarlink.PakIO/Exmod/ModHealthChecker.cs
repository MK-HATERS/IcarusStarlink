using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;

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
}

/// <summary>How to handle one issue.</summary>
public enum ModIssueFix
{
    KeepMod,
    UseGame,
    Merge,
    RenameRow,
    RemoveRow,
}

/// <param name="FieldName">Null for a whole-row issue (MissingRow).</param>
/// <param name="MergedValue">MixedList and LikelyOld-with-lists: the game's current value plus what the mod added.</param>
/// <param name="IsSure">Safe to fix without asking: the fix keeps everything the mod meant to do.</param>
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
    string Why)
{
    public IReadOnlyList<ModIssueFix> Choices => Kind switch
    {
        ModIssueKind.MissingRow => SuggestedRow is null ? [ModIssueFix.RemoveRow, ModIssueFix.KeepMod] : [ModIssueFix.RenameRow, ModIssueFix.RemoveRow, ModIssueFix.KeepMod],
        _ when MergedValue is not null => [ModIssueFix.Merge, ModIssueFix.UseGame, ModIssueFix.KeepMod],
        ModIssueKind.SameAsGame => [ModIssueFix.UseGame, ModIssueFix.KeepMod],
        _ => [ModIssueFix.UseGame, ModIssueFix.KeepMod],
    };
}

public sealed record ModHealthReport(IReadOnlyList<ModIssue> Issues, int AuthorChangeCount)
{
    public static ModHealthReport Healthy { get; } = new([], 0);

    public int SureCount => Issues.Count(i => i.IsSure);

    public int ReviewCount => Issues.Count(i => !i.IsSure);

    public bool HasIssues => Issues.Count > 0;

    public int Count(ModIssueKind kind) => Issues.Count(i => i.Kind == kind);
}

/// <summary>
/// "Does this mod still fit the game?" A mod made for an older game version often carries a copy
/// of that version's rows (prebuilt paks especially: they ship whole tables), so beside the
/// author's real changes it puts old values back: tag lists missing what the game added since,
/// fields the game added being deleted, values the game has since changed. This sorts every
/// difference between the mod and the game into the author's changes (kept, not listed) and the
/// kinds above, each with a default fix, and applies chosen fixes.
///
/// Without the old game version there is no true three-way merge, so the rules use what an old
/// copy looks like: a list that is the game's list minus some entries (the game only added since),
/// an object whose differences are all such lists, a field the mod deletes that the game has, and
/// a value the game's own change history (the weekly reports) shows it had before an update.
/// </summary>
public static class ModHealthChecker
{
    /// <summary>A table where the mod changes at least this many rows, and at least this share of them carry an old copy, was copied wholesale.</summary>
    private const int WholesaleCopyMinRows = 25;
    private const double WholesaleCopyOldShare = 0.6;

    private enum Verdict
    {
        Equal,
        Older,
        Changed,
        Mixed,
    }

    public static ModHealthReport Check(
        ExmodPackage package,
        string dataFolder,
        IDictionary<string, JsonObject?>? baseTableCache = null,
        IEnumerable<FieldChange>? gameHistory = null,
        IReadOnlyList<string>? ownAssetPaths = null,
        ISemanticClassifier? classifier = null)
    {
        baseTableCache ??= new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        var history = (gameHistory ?? [])
            .Where(c => c.OriginalValue is not null && !c.IsNewItem)
            .GroupBy(c => Key(c.CurrentFile, c.ItemName, c.FieldName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(c => c.OriginalValue!).ToList(), StringComparer.OrdinalIgnoreCase);

        var issues = new List<ModIssue>();
        var authorChanges = 0;

        foreach (var row in package.Rows)
        {
            if (ExmodSentinelFiles.IsEndOfModMarker(row.CurrentFile) || Table(row.CurrentFile, dataFolder, baseTableCache) is not { } table)
            {
                continue;
            }

            var rowsByName = RowsByName(table);
            var tableIssues = new List<ModIssue>();
            var changedOnRow = new List<(ExmodFileItem Item, string Field, JsonNode? Value, JsonNode Game)>();
            var rowsWithOld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rowsWithMixed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rowsTouched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The last entry wins when a mod lists a row twice (as the merge does), so judge that one.
            foreach (var item in row.FileItems.GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()))
            {
                if (!rowsByName.TryGetValue(item.Name, out var gameRow))
                {
                    continue;
                }

                foreach (var (field, value) in item.Fields)
                {
                    var gameValue = gameRow[field];
                    var hasField = gameRow.ContainsKey(field);
                    rowsTouched.Add(item.Name);

                    if (value is null)
                    {
                        if (hasField && gameValue is not null)
                        {
                            rowsWithOld.Add(item.Name);
                            tableIssues.Add(new(ModIssueKind.OldCopy, row.CurrentFile, item.Name, field, null, gameValue?.DeepClone(), null, null,
                                ModIssueFix.UseGame, IsSure: true, "Deletes this, but the game has it now: it was added after the mod was made."));
                        }
                        else
                        {
                            tableIssues.Add(new(ModIssueKind.SameAsGame, row.CurrentFile, item.Name, field, null, null, null, null,
                                ModIssueFix.UseGame, IsSure: true, "Deletes something the game doesn't have: does nothing."));
                        }

                        continue;
                    }

                    if (!hasField)
                    {
                        authorChanges++;
                        continue;
                    }

                    if (history.TryGetValue(Key(row.CurrentFile, item.Name, field), out var oldValues)
                        && oldValues.Any(old => JsonNode.DeepEquals(old, value)) && !JsonNode.DeepEquals(value, gameValue))
                    {
                        rowsWithOld.Add(item.Name);
                        tableIssues.Add(new(ModIssueKind.OldCopy, row.CurrentFile, item.Name, field, value.DeepClone(), gameValue?.DeepClone(), null, null,
                            ModIssueFix.UseGame, IsSure: true, "The game changed this in a recent update; the mod still has the value from before."));
                        continue;
                    }

                    switch (Judge(value, gameValue))
                    {
                        case Verdict.Equal:
                            tableIssues.Add(new(ModIssueKind.SameAsGame, row.CurrentFile, item.Name, field, value.DeepClone(), gameValue?.DeepClone(), null, null,
                                ModIssueFix.UseGame, IsSure: true, "Already the game's value: does nothing."));
                            break;
                        case Verdict.Older:
                            rowsWithOld.Add(item.Name);
                            tableIssues.Add(new(ModIssueKind.OldCopy, row.CurrentFile, item.Name, field, value.DeepClone(), gameValue?.DeepClone(), null, null,
                                ModIssueFix.UseGame, IsSure: true, OlderWhy(value, gameValue)));
                            break;
                        case Verdict.Mixed:
                            rowsWithMixed.Add(item.Name);
                            tableIssues.Add(new(ModIssueKind.MixedList, row.CurrentFile, item.Name, field, value.DeepClone(), gameValue?.DeepClone(), Merge(value, gameValue), null,
                                ModIssueFix.Merge, IsSure: false, MixedWhy(value, gameValue)));
                            break;
                        default:
                            changedOnRow.Add((item, field, value, gameValue!));
                            break;
                    }
                }
            }

            // A table copied wholesale from an older game: its few remaining "changes" on rows that
            // are otherwise old copies are almost certainly old values too, not the author's idea.
            // Measured in rows: most rows the mod touches here carry an old copy somewhere.
            var wholesale = rowsTouched.Count >= WholesaleCopyMinRows && rowsWithOld.Union(rowsWithMixed, StringComparer.OrdinalIgnoreCase).Count() >= WholesaleCopyOldShare * rowsTouched.Count;
            if (wholesale)
            {
                // A mixed list in a copied table is the game's list reorganised since (tags renamed),
                // not the author adding to it: the game's own list is the better guess.
                for (var i = 0; i < tableIssues.Count; i++)
                {
                    if (tableIssues[i].Kind == ModIssueKind.MixedList)
                    {
                        tableIssues[i] = tableIssues[i] with
                        {
                            Kind = ModIssueKind.LikelyOld,
                            DefaultFix = ModIssueFix.UseGame,
                            Why = $"This mod copied {rowsTouched.Count} rows of this table from an older game version, and the game has reorganised this list since. Merge would keep the mod's extra entries too.",
                        };
                    }
                }
            }

            foreach (var (item, field, value, gameValue) in changedOnRow)
            {
                if (wholesale && rowsWithOld.Contains(item.Name))
                {
                    tableIssues.Add(new(ModIssueKind.LikelyOld, row.CurrentFile, item.Name, field, value?.DeepClone(), gameValue.DeepClone(), null, null,
                        ModIssueFix.UseGame, IsSure: false,
                        $"This mod copied {rowsTouched.Count} rows of this table from an older game version, and the rest of this row is an old copy."));
                }
                else
                {
                    authorChanges++;
                }
            }

            issues.AddRange(tableIssues);
        }

        // Small edits to rows the game no longer has, renamed since. A row the mod itself makes or
        // links to elsewhere is its own new content (a new bag's priority row), never "missing";
        // and without a likely new name there is nothing to offer, so it isn't listed.
        var ownNames = OwnRowNames(package, dataFolder, baseTableCache);
        foreach (var stale in ExmodStalenessChecker.FindLikelyStaleItems(package, dataFolder, classifier ?? new DefaultSemanticClassifier(), baseTableCache, ownAssetPaths))
        {
            if (ownNames.Contains(stale.ItemName))
            {
                continue;
            }

            var fields = package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, stale.CurrentFile, StringComparison.OrdinalIgnoreCase))
                ?.FileItems.LastOrDefault(i => i.Name == stale.ItemName)?.Fields.Keys ?? Enumerable.Empty<string>();
            var suggestion = baseTableCache.TryGetValue(stale.CurrentFile, out var baseTable) && baseTable is not null ?StaleItemFixSuggester.Suggest(stale.ItemName, fields, baseTable) : null;
            if (suggestion is null)
            {
                continue;
            }

            issues.Add(new(ModIssueKind.MissingRow, stale.CurrentFile, stale.ItemName, null, null, null, null, suggestion.SuggestedItemName,
                suggestion.CanAutoApply ? ModIssueFix.RenameRow : ModIssueFix.KeepMod,
                IsSure: suggestion.CanAutoApply,
                suggestion.CanAutoApply
                    ? $"The game no longer has this row: it was renamed to '{suggestion.SuggestedItemName}'."
                    : $"The game no longer has this row. It may have been renamed to '{suggestion.SuggestedItemName}', or this is the mod's own new row."));
        }

        return new ModHealthReport(issues, authorChanges);
    }

    /// <summary>Applies the fixes to the package in memory; returns how many were applied. The caller backs the mod up and writes it.</summary>
    public static int Apply(ExmodPackage package, IEnumerable<(ModIssue Issue, ModIssueFix Fix)> fixes)
    {
        var applied = 0;
        foreach (var (issue, fix) in fixes)
        {
            if (fix == ModIssueFix.KeepMod)
            {
                continue;
            }

            var row = package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, issue.CurrentFile, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                continue;
            }

            var items = row.FileItems.Where(i => string.Equals(i.Name, issue.ItemName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (items.Count == 0)
            {
                continue;
            }

            switch (fix)
            {
                case ModIssueFix.UseGame when issue.FieldName is { } field:
                    // Leaving the field out is what makes the game's own value apply.
                    items.ForEach(i => i.Fields.Remove(field));
                    break;
                case ModIssueFix.Merge when issue.FieldName is { } field && issue.MergedValue is not null:
                    items.ForEach(i => i.Fields[field] = issue.MergedValue.DeepClone());
                    break;
                case ModIssueFix.RenameRow when issue.SuggestedRow is { } newName:
                    items.ForEach(i => i.Name = newName);
                    break;
                case ModIssueFix.RemoveRow:
                    row.FileItems.RemoveAll(items.Contains);
                    break;
                default:
                    continue;
            }

            applied++;
        }

        // An item left with nothing to change is gone from the mod; a file left with no items too.
        foreach (var row in package.Rows)
        {
            row.FileItems.RemoveAll(i => i.Fields.Count == 0 && fixes.Any(f => f.Fix != ModIssueFix.KeepMod && string.Equals(f.Issue.ItemName, i.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(f.Issue.CurrentFile, row.CurrentFile, StringComparison.OrdinalIgnoreCase)));
        }

        package.Rows.RemoveAll(r => r.FileItems.Count == 0 && !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile) && fixes.Any(f => string.Equals(f.Issue.CurrentFile, r.CurrentFile, StringComparison.OrdinalIgnoreCase)));
        return applied;
    }

    /// <summary>Applies every sure fix (the automatic part); returns how many.</summary>
    public static int ApplySure(ExmodPackage package, ModHealthReport report) =>
        Apply(package, report.Issues.Where(i => i.IsSure).Select(i => (i, i.DefaultFix)).ToList());

    private static Verdict Judge(JsonNode? mod, JsonNode? game)
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
            var verdicts = modObject.Select(kv => gameObject.ContainsKey(kv.Key) ? Judge(kv.Value, gameObject[kv.Key]) : Verdict.Changed).ToList();
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
            var total = (Added: 0, Missing: 0);
            foreach (var (key, value) in mo.Where(kv => go.ContainsKey(kv.Key)))
            {
                var (a, b) = TotalListDifference(value, go[key]);
                total = (total.Added + a, total.Missing + b);
            }

            return total;
        }

        return (0, 0);
    }

    private static string OlderWhy(JsonNode? mod, JsonNode? game)
    {
        var (_, missing) = TotalListDifference(mod, game);
        return missing > 0
            ? $"An older copy: the game has since added {missing} {(missing == 1 ? "entry" : "entries")} here, which this would take away."
            : "An older copy of the game's value.";
    }

    private static string MixedWhy(JsonNode? mod, JsonNode? game)
    {
        var (added, missing) = TotalListDifference(mod, game);
        return $"The mod adds {added} {(added == 1 ? "entry" : "entries")}, and the game has {missing} the mod doesn't (most likely added since). Merge keeps both.";
    }

    /// <summary>
    /// The mod's own new content: rows it adds in more than one table (a new item's ItemsStatic,
    /// Itemable, Template… rows) and every row it links to. A name new in one table only and linked
    /// from nowhere is what an edit to a since-renamed game row looks like, so it isn't included.
    /// </summary>
    private static HashSet<string> OwnRowNames(ExmodPackage package, string dataFolder, IDictionary<string, JsonObject?> cache)
    {
        var newRowTables = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in package.Rows.Where(r => !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile)))
        {
            var gameRows = Table(row.CurrentFile, dataFolder, cache) is { } table ? RowsByName(table) : null;
            foreach (var item in row.FileItems)
            {
                if (gameRows is not null && !gameRows.ContainsKey(item.Name))
                {
                    newRowTables[item.Name] = newRowTables.GetValueOrDefault(item.Name) + 1;
                }

                foreach (var value in item.Fields.Values)
                {
                    CollectLinks(value, linked);
                }
            }
        }

        var names = newRowTables.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(linked);
        return names;
    }

    private static void CollectLinks(JsonNode? node, HashSet<string> names)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["RowName"] is JsonValue row && row.TryGetValue<string>(out var name))
                {
                    names.Add(name);
                }

                foreach (var (_, child) in o)
                {
                    CollectLinks(child, names);
                }

                break;
            case JsonArray a:
                foreach (var child in a)
                {
                    CollectLinks(child, names);
                }

                break;
        }
    }

    private static string Key(string file, string row, string field) => $"{file}|{row}|{field}";

    private static JsonObject? Table(string currentFile, string dataFolder, IDictionary<string, JsonObject?> cache)
    {
        if (!cache.TryGetValue(currentFile, out var table))
        {
            table = BaseDataFileReader.ReadKeyedTable(dataFolder, currentFile, report: null);
            cache[currentFile] = table;
        }

        return table;
    }

    private static Dictionary<string, JsonObject> RowsByName(JsonObject table)
    {
        var rows = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in table)
        {
            if (value is JsonObject row)
            {
                rows.TryAdd(name, row);
            }
        }

        return rows;
    }
}
