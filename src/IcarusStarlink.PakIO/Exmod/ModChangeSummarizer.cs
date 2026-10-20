using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// "What this mod changes", in a few lines a person can read: "D_Itemable · MaxStack ×10 on 1337
/// rows". A mod's raw changes can run to thousands of field edits (Deyvid's All-in-One converts to
/// about 7000), but most real mods are a handful of patterns — one field scaled by one factor, or
/// set to one value, across a table. This finds those patterns from the mod's changes against
/// current game data (ExmodBaseDiffer's output).
/// </summary>
public static partial class ModChangeSummarizer
{
    private const int MaxLines = 25;

    /// <summary>A pattern must cover this share of a field's changes to be named as THE pattern.</summary>
    private const double Dominance = 0.8;

    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex ListIndexPattern();

    [GeneratedRegex(@"^\(Value=""(.+)""\)$")]
    private static partial Regex StatKeyPattern();

    /// <summary>
    /// Summarises an EXMOD against current game data, counting only the fields its items actually
    /// set. Not ExmodBaseDiffer.DiffAgainstBase: that treats a field an item doesn't list as a
    /// removal (right for a whole table copied out of a pak, wrong for an EXMOD's sparse items),
    /// which buried "MaxStack ×10" under thousands of phantom "DisplayName changed" lines.
    /// </summary>
    public static IReadOnlyList<string> SummarizePackage(ExmodPackage package, string dataFolder)
    {
        var game = new GameTableIndex(dataFolder); // each table read once, even if the mod lists it twice
        var changes = new List<FieldChange>();
        foreach (var row in package.Rows)
        {
            if (ExmodSentinelFiles.IsEndOfModMarker(row.CurrentFile))
            {
                continue;
            }

            var baseRows = game.Rows(row.CurrentFile);
            var defaults = game.Defaults(row.CurrentFile);
            foreach (var item in row.FileItems)
            {
                var baseRow = baseRows is null ? null : game.Row(row.CurrentFile, item.Name); // ignoring case, like the game
                foreach (var (field, value) in item.Fields)
                {
                    var before = baseRow is null ? null : baseRow[field] ?? defaults?[field];
                    if (baseRow is not null && JsonNode.DeepEquals(before, value))
                    {
                        continue;
                    }

                    changes.Add(new FieldChange(row.CurrentFile, item.Name, field, before?.DeepClone(), value?.DeepClone(), ValueSemantic.Scalar, IsNewItem: baseRows is not null && baseRow is null));
                }
            }
        }

        return Summarize(changes);
    }

    public static IReadOnlyList<string> Summarize(IReadOnlyList<FieldChange> changes)
    {
        var groups = new Dictionary<(string Table, string Path), Group>();
        var newRows = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var change in changes)
        {
            var table = TableName(change.CurrentFile);
            if (change.IsNewItem)
            {
                if (!newRows.TryGetValue(table, out var names))
                {
                    names = new HashSet<string>(StringComparer.Ordinal);
                    newRows[table] = names;
                }

                names.Add(change.ItemName);
                continue;
            }

            var before = Flatten(change.OriginalValue, change.FieldName);
            var after = Flatten(change.NewValue, change.FieldName);
            foreach (var path in before.Keys.Union(after.Keys))
            {
                before.TryGetValue(path, out var b);
                after.TryGetValue(path, out var a);
                if (JsonNode.DeepEquals(b, a))
                {
                    continue;
                }

                var key = (table, Normalize(path));
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new Group();
                    groups[key] = group;
                }

                group.Rows.Add(change.ItemName);
                if (TryNumber(a, out var an))
                {
                    // A value the mod adds (the game row had none) still counts toward "set to".
                    group.SetTo[Format(an)] = group.SetTo.GetValueOrDefault(Format(an)) + 1;
                }

                if (TryNumber(b, out var bn) && TryNumber(a, out an))
                {
                    group.Numeric++;
                    if (bn != 0)
                    {
                        // Two decimals: 0.665/0.667/0.668 are one "×0.67" after a mod's own rounding.
                        var ratio = Math.Round(an / bn, 2, MidpointRounding.AwayFromZero);
                        group.Ratios[ratio] = group.Ratios.GetValueOrDefault(ratio) + 1;
                    }
                }
                else
                {
                    group.Other++;
                }
            }
        }

        var lines = new List<(int Weight, string Text)>();

        // Several stats under one parent sharing one factor read as one line: "ArmourStats (8 stats)
        // ×1.5". Stats with a different factor stay on their own lines.
        var withRatio = groups
            .Where(kv => kv.Key.Path.Contains('.'))
            .Select(kv => (Entry: kv, Ratio: DominantRatio(kv.Value)))
            .Where(x => x.Ratio is not null)
            .ToList();
        foreach (var sameFactor in withRatio
            .GroupBy(x => (x.Entry.Key.Table, Parent: x.Entry.Key.Path[..x.Entry.Key.Path.IndexOf('.')], Ratio: x.Ratio!.Value), x => x.Entry)
            .Where(g => g.Count() >= 3)
            .ToList())
        {
            var (table, parent, ratio) = sameFactor.Key;
            var rows = sameFactor.SelectMany(kv => kv.Value.Rows).ToHashSet(StringComparer.Ordinal).Count;
            lines.Add((rows, $"{table} · {parent} ({sameFactor.Count()} stats) ×{Format(ratio)} on {Rows(rows)}"));
            foreach (var kv in sameFactor)
            {
                groups.Remove(kv.Key);
            }
        }

        foreach (var ((table, path), group) in groups)
        {
            var rows = group.Rows.Count;
            string text;
            if (DominantRatio(group) is { } ratio && ratio != 0)
            {
                text = $"{table} · {path} ×{Format(ratio)} on {Rows(rows)}";
            }
            else if (DominantValue(group) is { } value)
            {
                text = $"{table} · {path} set to {value} on {Rows(rows)}";
            }
            else
            {
                var common = group.Ratios.Count > 0 ? group.Ratios.MaxBy(kv => kv.Value).Key : (double?)null;
                text = $"{table} · {path} changed on {Rows(rows)}" + (common is { } c && group.Ratios[c] > 1 ? $" (×{Format(c)} most often)" : "");
            }

            lines.Add((rows, text));
        }

        foreach (var (table, names) in newRows)
        {
            lines.Add((names.Count, $"{table} · adds {names.Count} new row(s), e.g. {names.OrderBy(n => n, StringComparer.Ordinal).First()}"));
        }

        var ordered = lines.OrderByDescending(l => l.Weight).ThenBy(l => l.Text, StringComparer.Ordinal).Select(l => l.Text).ToList();
        if (ordered.Count > MaxLines)
        {
            var rest = ordered.Count - MaxLines;
            ordered = [.. ordered.Take(MaxLines), $"…and {rest} smaller change(s); see the full list below."];
        }

        return ordered;
    }

    private sealed class Group
    {
        public HashSet<string> Rows { get; } = new(StringComparer.Ordinal);
        public Dictionary<double, int> Ratios { get; } = [];
        public Dictionary<string, int> SetTo { get; } = new(StringComparer.Ordinal);
        public int Numeric { get; set; }
        public int Other { get; set; }
        public int Total => Numeric + Other;
    }

    private static double? DominantRatio(Group group)
    {
        if (group.Ratios.Count == 0 || group.Numeric < group.Total * Dominance)
        {
            return null;
        }

        var (ratio, count) = group.Ratios.MaxBy(kv => kv.Value);
        // A single changed value always "has a ratio" — only call it a scale when it spans 2+ values.
        return count >= Math.Max(2, group.Total * Dominance) ? ratio : null;
    }

    private static string? DominantValue(Group group)
    {
        if (group.SetTo.Count == 0)
        {
            return null;
        }

        var (value, count) = group.SetTo.MaxBy(kv => kv.Value);
        return count >= group.Total * Dominance ? value : null;
    }

    private static string TableName(string currentFile) => GameTableIndex.TableNameOf(currentFile);

    /// <summary>List indexes collapse ("Inputs[0].Count" → "Inputs[].Count"); stat keys read plainly ("(Value=\"BaseColdResistance_%\")" → "BaseColdResistance_%").</summary>
    private static string Normalize(string path) =>
        string.Join('.', ListIndexPattern().Replace(path, "[]").Split('.').Select(segment => StatKeyPattern().Match(segment) is { Success: true } m ? m.Groups[1].Value : segment));

    private static Dictionary<string, JsonNode?> Flatten(JsonNode? node, string prefix)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        void Walk(JsonNode? n, string path)
        {
            switch (n)
            {
                case JsonObject obj:
                    foreach (var (k, v) in obj)
                    {
                        Walk(v, $"{path}.{k}");
                    }

                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        Walk(arr[i], $"{path}[{i}]");
                    }

                    break;
                default:
                    result[path] = n;
                    break;
            }
        }

        Walk(node, prefix);
        return result;
    }

    private static bool TryNumber(JsonNode? node, out double value) => ScaleRules.TryReadNumber(node, out value);

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Rows(int count) => count == 1 ? "1 row" : $"{count} rows";
}
