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

    /// <summary>
    /// A link to a row the game doesn't have (and no mod in the Library adds), where the game's own
    /// link is real: does nothing in game (a canteen you can't drink from, an item with no model).
    /// Sure, but only made where every Library mod's new rows are known (not while converting a pak).
    /// </summary>
    BrokenLink,

    /// <summary>
    /// A whole row from an older game version that the game has since removed (laanp's paks carry
    /// Cobalt Ingot, Electric Motor…): in a prebuilt pak (or a table copied wholesale), not in the
    /// game, used by nothing else in the mod, and either known from the game's row history or
    /// carried by another Library mod too. Harmless, so only tidying.
    /// </summary>
    OldGameRow,
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
        ModIssueKind.OldGameRow => [ModIssueFix.RemoveRow, ModIssueFix.KeepMod],
        _ when MergedValue is not null => [ModIssueFix.Merge, ModIssueFix.UseGame, ModIssueFix.KeepMod],
        ModIssueKind.SameAsGame => [ModIssueFix.UseGame, ModIssueFix.KeepMod],
        _ => [ModIssueFix.UseGame, ModIssueFix.KeepMod],
    };

    /// <summary>Changes nothing in game either way: taking it out only makes the mod smaller.</summary>
    public bool IsTidyOnly => Kind is ModIssueKind.SameAsGame or ModIssueKind.OldGameRow;
}

/// <param name="BuiltFor">Which game version the mod's rows come from (when the game's row history is known).</param>
public sealed record ModHealthReport(IReadOnlyList<ModIssue> Issues, int AuthorChangeCount, ModGameVersion? BuiltFor = null)
{
    public static ModHealthReport Healthy { get; } = new([], 0);

    public int SureCount => Issues.Count(i => i.IsSure);

    public int ReviewCount => Issues.Count(i => !i.IsSure);

    public bool HasIssues => Issues.Count > 0;

    /// <summary>Findings that matter: everything but tidying (values that already match the game, and old game rows nothing uses), which changes nothing in game.</summary>
    public int ProblemCount => Issues.Count(i => !i.IsTidyOnly);

    public bool HasProblems => ProblemCount > 0;

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

    /// <param name="gameHistory">Old values from recent game updates (build once per pass, share across mods).</param>
    /// <param name="decisions">This mod's remembered choices: a kept value stops being flagged, any other choice becomes automatic.</param>
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
        GameRowHistory? rowHistory = null)
    {
        // Which table a link field points into, and whether a row is there (share one across a pass).
        gameIndex ??= new GameTableIndex(dataFolder);
        baseTableCache ??= new Dictionary<string, JsonObject?>(StringComparer.OrdinalIgnoreCase);
        var history = gameHistory ?? GameValueHistory.Empty;

        var issues = new List<ModIssue>();
        var authorChanges = 0;
        var wholesaleFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                        tableIssues.Add(new(ModIssueKind.OldCopy, row.CurrentFile, item.Name, field, value.DeepClone(), gameValue?.DeepClone(), null, null,
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
                            if (BrokenLink(gameIndex, package, row.CurrentFile, field, value, gameValue, rowsOtherModsAdd) is { } broken)
                            {
                                // A link to a row the game doesn't have does nothing in game (an item that
                                // can't be used, a recipe that never shows): never what anyone meant.
                                var gone = rowHistory?.Find(broken.TargetFile, broken.Row) is { } seen
                                    ? $"which the game removed after {seen.LastSeen}"
                                    : "which the game doesn't have";
                                tableIssues.Add(new(ModIssueKind.BrokenLink, row.CurrentFile, item.Name, field, value.DeepClone(), gameValue?.DeepClone(), null, null,
                                    ModIssueFix.UseGame, IsSure: true,
                                    $"Links to '{broken.Row}' in {broken.Table}, {gone}, so this does nothing in game. The game links it to '{broken.GameRow}'."));
                                rowsWithOld.Add(item.Name);
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
            }
            else
            {
                // A hand-made mod's own edit: an author emptying a list or deleting a field meant it.
                authorChanges += oldByShape.Count;
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

        // Small edits to rows the game no longer has, renamed since. A row the mod itself makes or
        // links to elsewhere is its own new content (a new bag's priority row), never "missing";
        // and without a likely new name there is nothing to offer, so it isn't listed.
        var ownNames = OwnRowNames(package, dataFolder, baseTableCache);
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
                ?.FileItems.LastOrDefault(i => i.Name == stale.ItemName)?.Fields.Keys ?? Enumerable.Empty<string>();
            var suggestion = baseTableCache.TryGetValue(stale.CurrentFile, out var baseTable) && baseTable is not null ?StaleItemFixSuggester.Suggest(stale.ItemName, fields, baseTable) : null;
            if (suggestion is null)
            {
                continue;
            }

            // The game's own history settles it: the old name gone in the very update the new one
            // arrived is a rename; both there at once is two different rows.
            var newName = suggestion.SuggestedItemName;
            var hasNew = hadIt is not null ? rowHistory!.Find(stale.CurrentFile, newName) : null;
            var renamedInOneUpdate = hadIt is not null && hasNew is not null && hasNew.First == hadIt.Last + 1;
            var bothAtOnce = hadIt is not null && hasNew is not null && hasNew.First <= hadIt.Last;
            // Made by itself only when the names match closely too: timing alone picks the fix, you confirm it.
            var sure = suggestion.CanAutoApply && !bothAtOnce;
            var why = renamedInOneUpdate
                ? $"The game renamed this row to '{newName}' in the update after {hadIt!.LastSeen}."
                : bothAtOnce
                    ? $"The game removed this row after {hadIt!.LastSeen}. '{newName}' has a similar name, but the game had both at once, so it's probably a different thing."
                    : sure
                        ? $"The game no longer has this row: it was renamed to '{newName}'."
                        : hadIt is not null
                            ? $"The game removed this row after {hadIt.LastSeen}. It may have been renamed to '{newName}'."
                            : $"The game no longer has this row. It may have been renamed to '{newName}', or this is the mod's own new row.";
            issues.Add(new(ModIssueKind.MissingRow, stale.CurrentFile, stale.ItemName, null, null, null, null, newName,
                sure || renamedInOneUpdate ? ModIssueFix.RenameRow : ModIssueFix.KeepMod, IsSure: sure, why));
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
                    remembered.Add(issue with { DefaultFix = fix, IsSure = true, Why = issue.Why + " Done as you chose before." });
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

    /// <summary>Applies the fixes to the package in memory; returns how many were applied. The caller backs the mod up and writes it.</summary>
    public static int Apply(ExmodPackage package, IEnumerable<(ModIssue Issue, ModIssueFix Fix)> fixes)
    {
        // Field fixes first, through an index (a wholesale-copied mod has a thousand-plus fixes over
        // hundreds of rows); row renames and removals last, since they change the names indexed.
        var changing = fixes.Where(f => f.Fix != ModIssueFix.KeepMod)
            .OrderBy(f => f.Fix is ModIssueFix.RenameRow or ModIssueFix.RemoveRow ? 1 : 0)
            .ToList();
        var rowsByFile = new Dictionary<string, ExmodFileRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in package.Rows)
        {
            rowsByFile.TryAdd(row.CurrentFile, row);
        }

        var itemsByRow = new Dictionary<ExmodFileRow, ILookup<string, ExmodFileItem>>();
        var touched = new HashSet<(string File, string Item)>(new FileItemComparer());
        var applied = 0;
        foreach (var (issue, fix) in changing)
        {
            if (!rowsByFile.TryGetValue(issue.CurrentFile, out var row))
            {
                continue;
            }

            var items = fix is ModIssueFix.RenameRow or ModIssueFix.RemoveRow
                ? row.FileItems.Where(i => string.Equals(i.Name, issue.ItemName, StringComparison.OrdinalIgnoreCase)).ToList()
                : (itemsByRow.TryGetValue(row, out var lookup) ? lookup : itemsByRow[row] = row.FileItems.ToLookup(i => i.Name, StringComparer.OrdinalIgnoreCase))[issue.ItemName].ToList();
            if (items.Count == 0)
            {
                continue;
            }

            touched.Add((row.CurrentFile, issue.ItemName));
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

        // An item a fix left with nothing to change is gone from the mod; a file left with no items
        // too. Items that were already empty (one being built in the editor) are left alone.
        var touchedFiles = touched.Select(t => t.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var row in package.Rows.Where(r => touchedFiles.Contains(r.CurrentFile)))
        {
            row.FileItems.RemoveAll(i => i.Fields.Count == 0 && touched.Contains((row.CurrentFile, i.Name)));
        }

        package.Rows.RemoveAll(r => r.FileItems.Count == 0 && touchedFiles.Contains(r.CurrentFile) && !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile));
        return applied;
    }

    private sealed class FileItemComparer : IEqualityComparer<(string File, string Item)>
    {
        public bool Equals((string File, string Item) x, (string File, string Item) y) =>
            string.Equals(x.File, y.File, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Item, y.Item, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string File, string Item) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.File), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item));
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

    /// <summary>
    /// The mod links this field to a row its target table doesn't have (and the mod doesn't add),
    /// where the game's own link is to a real row: (the missing row, its table, the game's row).
    /// The target table is the link's own DataTableName, else the field's in the table's Defaults.
    /// </summary>
    private static (string Row, string Table, string TargetFile, string GameRow)? BrokenLink(
        GameTableIndex index, ExmodPackage package, string currentFile, string field, JsonNode? mod, JsonNode? game,
        IReadOnlySet<string>? rowsOtherModsAdd)
    {
        if (mod is not JsonObject link || link["RowName"] is not JsonValue rowValue || !rowValue.TryGetValue<string>(out var rowName)
            || rowName.Length == 0 || string.Equals(rowName, "None", StringComparison.OrdinalIgnoreCase)
            || game is not JsonObject gameLink || gameLink["RowName"] is not JsonValue gameRowValue || !gameRowValue.TryGetValue<string>(out var gameRow))
        {
            return null;
        }

        var table = link["DataTableName"] is JsonValue t && t.TryGetValue<string>(out var named) && named.Length > 0 ? named : index.LinkTarget(currentFile, field);
        if (table is null || index.CurrentFileFor(table) is not { } targetFile || index.Rows(targetFile) is null
            || index.HasRow(targetFile, rowName) || !index.HasRow(targetFile, gameRow))
        {
            return null;
        }

        // A row this mod adds, or another Library mod does (an add-on linking to the mod it's for).
        var modAddsIt = package.Rows.Any(r => string.Equals(r.CurrentFile, targetFile, StringComparison.OrdinalIgnoreCase)
            && r.FileItems.Any(i => string.Equals(i.Name, rowName, StringComparison.OrdinalIgnoreCase)));
        var anotherModAddsIt = rowsOtherModsAdd?.Contains(AddedRowKey(targetFile, rowName)) == true;
        return modAddsIt || anotherModAddsIt ? null : (rowName, table, targetFile, gameRow);
    }

    /// <summary>
    /// Whole rows a prebuilt pak (or a table a mod copied wholesale) carries from an older game: not
    /// in the game, linked from nothing else in this mod, and with evidence it was the game's:
    /// the game's row history shows it had the row (proof), or another Library mod carries the
    /// same row (two mods both "adding" Cobalt Ingot are both carrying the game's old row). Rows
    /// that only link to each other (an old item's ItemsStatic, Itemable and Template rows) go
    /// together; one the mod's real content links to stays. Hand-made mods' own tables are never
    /// judged: some deliberately bring a removed item back.
    /// </summary>
    /// <param name="onlyFiles">Null: every table (a prebuilt pak). Otherwise only these (tables copied wholesale).</param>
    private static IEnumerable<ModIssue> OldGameRows(ExmodPackage package, GameTableIndex index, IReadOnlySet<string>? onlyFiles,
        IReadOnlySet<string>? rowsSeveralModsAdd, GameRowHistory? rowHistory)
    {
        var candidates = new List<(string File, ExmodFileItem Item)>();
        var others = new List<ExmodFileItem>();
        foreach (var row in package.Rows.Where(r => !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile)))
        {
            var known = index.Rows(row.CurrentFile) is not null && (onlyFiles is null || onlyFiles.Contains(row.CurrentFile));
            foreach (var item in row.FileItems)
            {
                if (known && !index.HasRow(row.CurrentFile, item.Name)
                    && (rowHistory?.Find(row.CurrentFile, item.Name) is not null || rowsSeveralModsAdd?.Contains(AddedRowKey(row.CurrentFile, item.Name)) == true))
                {
                    candidates.Add((row.CurrentFile, item));
                }
                else
                {
                    others.Add(item);
                }
            }
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in others)
        {
            foreach (var value in item.Fields.Values)
            {
                CollectLinks(value, used);
            }
        }

        // A candidate something real uses is real too, and so is whatever it links to.
        bool changed;
        do
        {
            changed = false;
            foreach (var (_, item) in candidates.Where(c => used.Contains(c.Item.Name)).ToList())
            {
                candidates.RemoveAll(c => ReferenceEquals(c.Item, item));
                foreach (var value in item.Fields.Values)
                {
                    CollectLinks(value, used);
                }

                changed = true;
            }
        }
        while (changed);

        foreach (var (file, item) in candidates.GroupBy(c => (c.File.ToLowerInvariant(), c.Item.Name.ToLowerInvariant())).Select(g => g.Last()))
        {
            var seen = rowHistory?.Find(file, item.Name);
            var shared = rowsSeveralModsAdd?.Contains(AddedRowKey(file, item.Name)) == true;
            var why = seen is not null
                ? $"The game had this row until {seen.LastSeen} and has since removed it; the mod still carries a copy from then."
                    + (shared ? " Another mod in your Library carries the same copy." : "")
                : "Most likely a copy from an older game version: another mod in your Library carries the same row, and the game doesn't have it.";
            yield return new ModIssue(ModIssueKind.OldGameRow, file, item.Name, null, null, null, null, null, ModIssueFix.RemoveRow, IsSure: false,
                why + " Nothing else in the mod uses it, so it does nothing in game.");
        }
    }

    /// <summary>The key for <c>rowsOtherModsAdd</c>: a row a mod adds to a table ("Traits-D_Consumable.json|canteen_drink").</summary>
    public static string AddedRowKey(string currentFile, string rowName) => $"{currentFile.ToLowerInvariant()}|{rowName.ToLowerInvariant()}";

    /// <summary>The rows a mod adds (not in the game), as AddedRowKey keys: collect over the Library for <c>rowsOtherModsAdd</c>.</summary>
    public static IEnumerable<string> AddedRows(ExmodPackage package, GameTableIndex index)
    {
        foreach (var row in package.Rows.Where(r => !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile)))
        {
            if (index.Rows(row.CurrentFile) is null)
            {
                continue;
            }

            foreach (var item in row.FileItems.Where(i => !index.HasRow(row.CurrentFile, i.Name)))
            {
                yield return AddedRowKey(row.CurrentFile, item.Name);
            }
        }
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
