using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>Links to rows nothing has (BrokenLinks, LinksMissingFrom), old game rows a mod still carries, and the rows a mod adds.</summary>
public static partial class ModHealthChecker
{
    /// <summary>A link to a row nothing has: where it is (Path is "Field" or "Field[0].Element"), where it points, and the link itself.</summary>
    private sealed record BrokenLinkFound(string File, string Item, string Field, string Path, JsonObject Link, string Row, string Table, string TargetFile);

    /// <summary>
    /// Every {"RowName": X} link, at any depth of every row the mod lists (its own new rows too),
    /// to a row its target table doesn't have, that this mod doesn't add and no other Library mod
    /// does (an add-on linking to the mod it's for). The target table is the link's own
    /// DataTableName, else the one the table's Defaults give at the same place (a list's first
    /// entry stands for every entry). A link the game's own row has too is the game's, not the mod's.
    /// </summary>
    private static IEnumerable<BrokenLinkFound> BrokenLinks(ExmodPackage package, GameTableIndex index, IReadOnlySet<string>? rowsOtherModsAdd)
    {
        var modAdds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in package.Rows)
        {
            foreach (var item in row.FileItems)
            {
                modAdds.Add(AddedRowKey(row.CurrentFile, item.Name));
            }
        }

        foreach (var row in package.Rows.Where(r => !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile)))
        {
            if (index.Rows(row.CurrentFile) is null)
            {
                continue;
            }

            var defaults = index.Defaults(row.CurrentFile);
            foreach (var item in ExmodFileItem.CombineByName(row.FileItems))
            {
                var gameLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                CollectLinks(index.Row(row.CurrentFile, item.Name), gameLinks);
                foreach (var (field, value) in item.Fields)
                {
                    foreach (var (path, link, rowName, table) in Links(value, defaults?[field], field))
                    {
                        if (gameLinks.Contains(rowName) || index.CurrentFileFor(table) is not { } targetFile || index.Rows(targetFile) is null
                            || index.HasRow(targetFile, rowName) || modAdds.Contains(AddedRowKey(targetFile, rowName))
                            || rowsOtherModsAdd?.Contains(AddedRowKey(targetFile, rowName)) == true)
                        {
                            continue;
                        }

                        yield return new BrokenLinkFound(row.CurrentFile, item.Name, field, path, link, rowName, table, targetFile);
                    }
                }
            }
        }
    }

    /// <summary>The links in a value with the table each points into, walking the table's Defaults alongside it.</summary>
    private static IEnumerable<(string Path, JsonObject Link, string Row, string Table)> Links(JsonNode? node, JsonNode? defaults, string path)
    {
        switch (node)
        {
            case JsonObject o:
                if (o["RowName"] is JsonValue rowValue && rowValue.TryGetValue<string>(out var rowName)
                    && rowName.Length > 0 && !string.Equals(rowName, "None", StringComparison.OrdinalIgnoreCase)
                    && (TableName(o) ?? (defaults is JsonObject d ? TableName(d) : null)) is { } table)
                {
                    yield return (path, o, rowName, table);
                }

                foreach (var (key, child) in o)
                {
                    if (key is "RowName" or "DataTableName")
                    {
                        continue;
                    }

                    foreach (var found in Links(child, (defaults as JsonObject)?[key], $"{path}.{key}"))
                    {
                        yield return found;
                    }
                }

                break;
            case JsonArray a:
                var entryDefaults = defaults is JsonArray { Count: > 0 } d2 ? d2[0] : null;
                for (var i = 0; i < a.Count; i++)
                {
                    foreach (var found in Links(a[i], entryDefaults, $"{path}[{i}]"))
                    {
                        yield return found;
                    }
                }

                break;
        }

        static string? TableName(JsonObject o) =>
            o["DataTableName"] is JsonValue t && t.TryGetValue<string>(out var named) && named.Length > 0 && !string.Equals(named, "None", StringComparison.OrdinalIgnoreCase) ? named : null;
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

    /// <summary>
    /// The mod's links (any depth, its own new rows too) to rows neither the game, the mod itself
    /// nor <paramref name="rowsAvailable"/> (AddedRowKey keys) has: in a merge, the rows the other
    /// queued mods add. Key is the AddedRowKey of the row linked to.
    /// </summary>
    public static IEnumerable<(string File, string Item, string Path, string Row, string Table, string Key)> LinksMissingFrom(
        ExmodPackage package, GameTableIndex index, IReadOnlySet<string> rowsAvailable) =>
        BrokenLinks(package, index, rowsAvailable).Select(l => (l.File, l.Item, l.Path, l.Row, l.Table, AddedRowKey(l.TargetFile, l.Row)));
}
