using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Exmod;

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
public static partial class ModHealthChecker
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

    /// <param name="gameHistory">Old values from recent game updates (build once per pass, share across mods).</param>
    /// <param name="decisions">This mod's remembered choices: a kept value stops being flagged, any other choice becomes automatic.</param>
    /// <param name="assetIssues">What the mod's cooked files use that the game doesn't have (AssetReferenceIssues.From), listed with the rest.</param>
    /// <param name="fromPrebuiltPak">
    /// The mod was converted from a prebuilt pak, which ships whole tables: a shorter list or a
    /// missing field there is the old game version's, not a choice. In a hand-made mod, which
    /// lists only what its author changed, the same shape is deliberate (an emptied fuel list is
    /// how "torches need no fuel" works), so it only counts as old in a table the mod copied
    /// wholesale.
    /// </param>
    public static ModHealthReport Check(
        ExmodPackage package,
        string dataFolder,
        IDictionary<string, JsonObject?>? baseTableCache = null,
        GameValueHistory? gameHistory = null,
        IReadOnlyList<string>? ownAssetPaths = null,
        ISemanticClassifier? classifier = null,
        ModFixDecisions? decisions = null,
        bool fromPrebuiltPak = false,
        GameTableIndex? gameIndex = null,
        IReadOnlySet<string>? rowsOtherModsAdd = null,
        IReadOnlySet<string>? rowsSeveralModsAdd = null,
        GameRowHistory? rowHistory = null,
        IReadOnlyList<ModIssue>? assetIssues = null)
    {
        // Which table a link field points into, and whether a row is there (share one across a pass).
        gameIndex ??= new GameTableIndex(dataFolder);
        baseTableCache ??= new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        var history = gameHistory ?? GameValueHistory.Empty;

        var issues = new List<ModIssue>();
        var authorChanges = 0;
        var wholesaleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Every link to a row nothing has, at any depth of any row; a game row's own link field is
        // judged with its other differences below, the rest are listed after.
        var brokenLinks = BrokenLinks(package, gameIndex, rowsOtherModsAdd).ToList();
        var topLevelLinks = brokenLinks.Where(l => l.Path == l.Field)
            .GroupBy(l => (File: l.File.ToLowerInvariant(), Item: l.Item.ToLowerInvariant(), l.Field))
            .ToDictionary(g => g.Key, g => g.Last());
        var linksJudged = new HashSet<BrokenLinkFound>();

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

            // Old by shape only (a shorter list, a deleted field): decided once it's known whether
            // this table is a copy (below).
            var oldByShape = new List<ModIssue>();

            // A value the game had before a recent update: an old copy in a prebuilt pak or a copied
            // table, but in a hand-made mod possibly the author putting it back on purpose (a
            // "revert the nerf" mod). Also decided once it's known whether this table is a copy.
            var oldByHistory = new List<ModIssue>();

            // A row listed twice: the merge takes every copy's fields (a field in two copies: the
            // later one's value), so judge them together. A fix goes to each copy that sets the field.
            foreach (var item in ExmodFileItem.CombineByName(row.FileItems))
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
                            oldByShape.Add(new(ModIssueKind.OldCopy, row.CurrentFile, item.Name, field, null, gameValue?.DeepClone(), null, null,
                                ModIssueFix.UseGame, IsSure: true, "Deletes this, but the game has it now: it was added after the mod was made."));
                        }
                        else
                        {
                            tableIssues.Add(new(ModIssueKind.SameAsGame, row.CurrentFile, item.Name, field, null, null, null, null,
                                ModIssueFix.UseGame, IsSure: true, "The mod deletes something the game doesn't have, so it changes nothing. Taking it out is only tidying."));
                        }

                        continue;
                    }

                    if (!hasField)
                    {
                        authorChanges++;
                        continue;
                    }

                    if (history.WasOldValue(row.CurrentFile, item.Name, field, value) && !JsonNode.DeepEquals(value, gameValue))
                    {
                        rowsWithOld.Add(item.Name);
                        oldByHistory.Add(new(ModIssueKind.OldCopy, row.CurrentFile, item.Name, field, value.DeepClone(), gameValue?.DeepClone(), null, null,
                            ModIssueFix.UseGame, IsSure: true, "The game changed this in a recent update; the mod still has the value from before."));
                        continue;
                    }

                    switch (Judge(value, gameValue))
                    {
                        case Verdict.Equal:
                            tableIssues.Add(new(ModIssueKind.SameAsGame, row.CurrentFile, item.Name, field, value.DeepClone(), gameValue?.DeepClone(), null, null,
                                ModIssueFix.UseGame, IsSure: true, "The mod sets this to exactly what the game already has, so it changes nothing. Taking it out is only tidying; the game's value stays either way."));
                            break;
                        case Verdict.Older:
                            rowsWithOld.Add(item.Name);
                            oldByShape.Add(new(ModIssueKind.OldCopy, row.CurrentFile, item.Name, field, value.DeepClone(), gameValue?.DeepClone(), null, null,
                                ModIssueFix.UseGame, IsSure: true, OlderWhy(value, gameValue)));
                            break;
                        case Verdict.Mixed:
                            rowsWithMixed.Add(item.Name);
                            tableIssues.Add(new(ModIssueKind.MixedList, row.CurrentFile, item.Name, field, value.DeepClone(), gameValue?.DeepClone(), Merge(value, gameValue), null,
                                ModIssueFix.Merge, IsSure: false, MixedWhy(value, gameValue)));
                            break;
                        default:
                            if (topLevelLinks.TryGetValue((row.CurrentFile.ToLowerInvariant(), item.Name.ToLowerInvariant(), field), out var broken)
                                && gameValue is JsonObject gameLink && gameLink["RowName"] is JsonValue gameRowValue && gameRowValue.TryGetValue<string>(out var gameLinkRow)
                                && gameIndex.HasRow(broken.TargetFile, gameLinkRow))
                            {
                                // A link to a row the game doesn't have does nothing in game (an item that
                                // can't be used, a recipe that never shows). Sure only when the game's own
                                // history proves the row was the game's and it removed it: a row it never
                                // had may be one another mod (not in this Library) adds.
                                linksJudged.Add(broken);
                                var removed = rowHistory?.Find(broken.TargetFile, broken.Row);
                                var proven = removed is not null && rowsOtherModsAdd is not null;
                                tableIssues.Add(new(ModIssueKind.BrokenLink, row.CurrentFile, item.Name, field, value.DeepClone(), gameValue.DeepClone(), null, null,
                                    proven ? ModIssueFix.UseGame : ModIssueFix.KeepMod, IsSure: proven,
                                    removed is not null
                                        ? $"Links to '{broken.Row}' in {broken.Table}, which the game removed after {removed.LastSeen}, so this does nothing in game. The game links it to '{gameLinkRow}'."
                                        : $"Links to '{broken.Row}' in {broken.Table}, which neither the game nor any mod in your Library has: it needs a mod that adds '{broken.Row}'. Without one it does nothing in game; the game links it to '{gameLinkRow}'."));
                                if (removed is not null)
                                {
                                    // A link to a row the game used to have is what an old copy looks like.
                                    rowsWithOld.Add(item.Name);
                                }
                            }
                            else
                            {
                                changedOnRow.Add((item, field, value, gameValue!));
                            }
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
                wholesaleFiles.Add(row.CurrentFile);
            }

            if (fromPrebuiltPak || wholesale)
            {
                tableIssues.AddRange(oldByShape);
                tableIssues.AddRange(oldByHistory);
            }
            else
            {
                // A hand-made mod's own edit: an author emptying a list or deleting a field meant it.
                authorChanges += oldByShape.Count;

                // An old value is listed but kept unless you choose otherwise: putting a value back
                // the game changed can be the whole point of a hand-made mod.
                tableIssues.AddRange(oldByHistory.Select(i => i with
                {
                    IsSure = false,
                    DefaultFix = ModIssueFix.KeepMod,
                    Why = "The game changed this in a recent update, and the mod has the value from before: either an old copy, or the author putting the old value back on purpose.",
                }));
            }

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

        var oldRows = new HashSet<string>(StringComparer.Ordinal);
        if ((fromPrebuiltPak || wholesaleFiles.Count > 0) && (rowsSeveralModsAdd is { Count: > 0 } || rowHistory is { VersionCount: > 0 }))
        {
            foreach (var old in OldGameRows(package, gameIndex, fromPrebuiltPak ? null : wholesaleFiles, rowsSeveralModsAdd, rowHistory))
            {
                issues.Add(old);
                oldRows.Add(AddedRowKey(old.CurrentFile, old.ItemName));
            }
        }

        // Small edits to rows the game no longer has: renamed since, or removed. A row the mod itself
        // makes or links to elsewhere is its own new content (a new bag's priority row), never
        // "missing"; and without a likely new name or the history's proof that the game removed it,
        // there is nothing to offer, so it isn't listed.
        var ownNames = OwnRowNames(package, dataFolder, baseTableCache);
        var missingRows = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stale in ExmodStalenessChecker.FindLikelyStaleItems(package, dataFolder, classifier ?? new DefaultSemanticClassifier(), baseTableCache, ownAssetPaths))
        {
            // Row names ignore case in the game, so "Pickaxe_Mk2" already is the game's "Pickaxe_MK2".
            if (ownNames.Contains(stale.ItemName)
                || oldRows.Contains(AddedRowKey(stale.CurrentFile, stale.ItemName))
                || (Table(stale.CurrentFile, dataFolder, baseTableCache) is { } staleTable && RowsByName(staleTable).ContainsKey(stale.ItemName)))
            {
                continue;
            }

            // A row no game version ever had is the mod's own, never a renamed one.
            var hadIt = rowHistory?.Find(stale.CurrentFile, stale.ItemName);
            if (rowHistory is { VersionCount: > 0 } && hadIt is null)
            {
                continue;
            }

            var fields = package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, stale.CurrentFile, StringComparison.OrdinalIgnoreCase))
                ?.FileItems.Where(i => string.Equals(i.Name, stale.ItemName, StringComparison.OrdinalIgnoreCase)).SelectMany(i => i.Fields.Keys).Distinct() ?? Enumerable.Empty<string>();
            var suggestion = baseTableCache.TryGetValue(stale.CurrentFile, out var baseTable) && baseTable is not null ? StaleItemFixSuggester.Suggest(stale.ItemName, fields, baseTable) : null;
            if (suggestion is null)
            {
                // No likely new name, but the history proves the game had this row and removed it:
                // the edit has nothing left to change (it's what the "made for" line counts).
                if (hadIt is not null && hadIt.Last < rowHistory!.Latest)
                {
                    missingRows.Add(AddedRowKey(stale.CurrentFile, stale.ItemName));
                    issues.Add(new(ModIssueKind.MissingRow, stale.CurrentFile, stale.ItemName, null, null, null, null, null,
                        ModIssueFix.RemoveRow, IsSure: false,
                        $"The game removed this row after {hadIt.LastSeen}, so this edit has nothing left to change. The game has no row with a similar name to move it to."));
                }

                continue;
            }

            // The game's own history settles it: the old name gone in the very update the new one
            // arrived is a rename; both there at once is two different rows.
            var newName = suggestion.SuggestedItemName;
            var hasNew = hadIt is not null ? rowHistory!.Find(stale.CurrentFile, newName) : null;
            var renamedInOneUpdate = hadIt is not null && hasNew is not null && hasNew.First == hadIt.Last + 1;
            var bothAtOnce = hadIt is not null && hasNew is not null && hasNew.First <= hadIt.Last;
            // Made by itself only when the history proves the rename and the names match closely too;
            // a similar name alone is a guess, made only when you confirm it.
            var sure = renamedInOneUpdate && suggestion.CanAutoApply;
            var why = renamedInOneUpdate
                ? $"The game renamed this row to '{newName}' in the update after {hadIt!.LastSeen}."
                : bothAtOnce
                    ? $"The game removed this row after {hadIt!.LastSeen}. '{newName}' has a similar name, but the game had both at once, so it's probably a different thing."
                    : hadIt is not null
                        ? $"The game removed this row after {hadIt.LastSeen}. It may have been renamed to '{newName}', which has a similar name."
                        : $"The game no longer has this row. It may have been renamed to '{newName}', which has a similar name, or this is the mod's own new row.";
            missingRows.Add(AddedRowKey(stale.CurrentFile, stale.ItemName));
            issues.Add(new(ModIssueKind.MissingRow, stale.CurrentFile, stale.ItemName, null, null, null, null, newName,
                renamedInOneUpdate ? ModIssueFix.RenameRow : ModIssueFix.KeepMod, IsSure: sure, why));
        }

        // The links not judged with a game row's other differences: inside a field, or in a row the
        // game doesn't have (the mod's own new rows too). Only listed: there's no game value to go
        // back to. Rows already listed as old or missing are left out (their links go with them).
        foreach (var link in brokenLinks.Where(l => !linksJudged.Contains(l)
            && !oldRows.Contains(AddedRowKey(l.File, l.Item)) && !missingRows.Contains(AddedRowKey(l.File, l.Item))))
        {
            var removed = rowHistory?.Find(link.TargetFile, link.Row);
            var links = gameIndex.HasRow(link.File, link.Item) ? "Links" : "This row of the mod's own links";
            issues.Add(new(ModIssueKind.BrokenLink, link.File, link.Item, link.Path, link.Link.DeepClone(), null, null, null,
                ModIssueFix.KeepMod, IsSure: false,
                removed is not null
                    ? $"{links} to '{link.Row}' in {link.Table}, which the game removed after {removed.LastSeen}, so this link does nothing in game. Edit the mod to link it to a row the game has now."
                    : $"{links} to '{link.Row}' in {link.Table}, which neither the game nor any mod in your Library has: it needs a mod that adds '{link.Row}'. Without one this link does nothing in game."));
        }

        // Tables, fields and text keys the game can't use as the mod writes them; not on rows
        // already listed as old or missing (they go as a whole).
        issues.AddRange(FormatIssues(package, gameIndex).Where(i => !oldRows.Contains(AddedRowKey(i.CurrentFile, i.ItemName))
            && !missingRows.Contains(AddedRowKey(i.CurrentFile, i.ItemName))));

        // What the mod's cooked files use that the game doesn't have (AssetReferenceIssues): its
        // remembered choices apply to them too.
        if (assetIssues is { Count: > 0 })
        {
            issues.AddRange(assetIssues);
        }

        // What the user decided before for this mod: a value they kept is theirs now (unless either
        // side has changed since); any other choice is done by itself from now on.
        if (decisions is { Count: > 0 })
        {
            var remembered = new List<ModIssue>(issues.Count);
            foreach (var issue in issues)
            {
                if (!decisions.TryGet(issue, out var fix))
                {
                    remembered.Add(issue);
                }
                else if (fix == ModIssueFix.KeepMod)
                {
                    authorChanges++;
                }
                else if (issue.Choices.Contains(fix))
                {
                    remembered.Add(issue with { DefaultFix = fix, IsSure = true, Remembered = true, Why = issue.Why + " Done as you chose before." });
                }
                else
                {
                    remembered.Add(issue);
                }
            }

            issues = remembered;
        }

        return new ModHealthReport(issues, authorChanges, rowHistory is null ? null : ModGameVersionEstimator.Estimate(package, rowHistory));
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
