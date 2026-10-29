using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>Applying chosen fixes to a package in memory (Apply, ApplySure).</summary>
public static partial class ModHealthChecker
{
    /// <summary>Applies the fixes to the package in memory; returns how many were applied. The caller backs the mod up and writes it.</summary>
    public static int Apply(ExmodPackage package, IEnumerable<(ModIssue Issue, ModIssueFix Fix)> fixes)
    {
        // Field fixes first, through an index (a wholesale-copied mod has a thousand-plus fixes over
        // hundreds of rows); row renames and removals last, since they change the names indexed.
        var changing = fixes.Where(f => f.Fix != ModIssueFix.KeepMod)
            .OrderBy(f => f.Fix switch
            {
                ModIssueFix.RenameRow or ModIssueFix.RemoveRow => 1,
                ModIssueFix.RenameTable => 2,
                _ => 0,
            })
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
            if (fix == ModIssueFix.RenameTable)
            {
                if (issue.SuggestedRow is { } table && MoveTable(package, issue.CurrentFile, table))
                {
                    applied++;
                }

                continue;
            }

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
                    // Only the copies that set it: a copy that leaves it out keeps leaving it out.
                    items.Where(i => i.Fields.ContainsKey(field)).ToList().ForEach(i => i.Fields[field] = issue.MergedValue.DeepClone());
                    break;
                case ModIssueFix.RenameRow when issue.SuggestedRow is { } newName:
                    items.ForEach(i => i.Name = newName);
                    break;
                case ModIssueFix.RenameField when issue.FieldName is { } field && issue.SuggestedRow is { } newField:
                    items.ForEach(i =>
                    {
                        // A copy of the row that already has the right name keeps its value; the misspelt one goes.
                        if (i.Fields.Remove(field, out var value))
                        {
                            i.Fields.TryAdd(newField, value);
                        }
                    });
                    break;
                case ModIssueFix.OwnTextKey when issue.FieldName is { } field && issue.SuggestedRow is { } key:
                    items.ForEach(i =>
                    {
                        if (i.Fields.GetValueOrDefault(field) is JsonValue text && text.TryGetValue<string>(out var value) && NsLocText.WithKey(value, key) is { } rekeyed)
                        {
                            i.Fields[field] = JsonValue.Create(rekeyed);
                        }
                    });
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

    /// <summary>Applies every automatic fix (sure problems and remembered choices, never tidying alone); returns how many.</summary>
    public static int ApplySure(ExmodPackage package, ModHealthReport report) =>
        Apply(package, report.Issues.Where(i => i.IsAutomatic).Select(i => (i, i.DefaultFix)).ToList());
}
