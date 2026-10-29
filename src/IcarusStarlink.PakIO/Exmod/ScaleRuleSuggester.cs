using System.Globalization;
using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// One rule a mod's own rows already follow: "Slots ×2 on 22 rows". Turned into a Scale rule it
/// writes exactly what the mod has now, and after a game update it also changes the new rows the
/// filter catches.
/// </summary>
/// <param name="Rule">The rule (enabled, ready to add).</param>
/// <param name="RowsCovered">This mod's rows the rule writes, each to the value the mod already has.</param>
/// <param name="RowsWithField">This mod's game rows that change the field (the rule covers some or all of them).</param>
/// <param name="ExtraGameRows">Game rows the rule also changes that this mod doesn't change yet (only for a rule over the whole table).</param>
/// <param name="Text">What the rule does, in plain words.</param>
public sealed record ScaleRuleSuggestion(ScaleRule Rule, int RowsCovered, int RowsWithField, int ExtraGameRows, string Text);

/// <summary>
/// "Suggest rules from this mod": finds a field the mod changes the same way on many game rows of a
/// table (every container's StartingSlots ×2, every stack set to 999) and words it as a Scale rule,
/// so a hand-made mod can keep up with game updates. Proven, not guessed: a rule is only suggested
/// when Apply would write back exactly the value the mod already has on every row it covers, and
/// would not touch a row the mod changes another way. Only top-level number fields (a rule owns its
/// whole top-level field); rows the mod adds itself are never judged.
/// </summary>
public static class ScaleRuleSuggester
{
    /// <summary>Fewer rows than this following one pattern is a hand edit, not a rule.</summary>
    public const int MinRows = 3;

    /// <summary>A rule over the whole table is offered when the mod already changes at least this share of the rows it would change; the rest are most likely rows the game added later.</summary>
    private const double WholeTableShare = 0.75;

    /// <summary>At most this many rules per field (the real storage mod has two: ×2, and "set to 120" for the big ones).</summary>
    private const int MaxPerField = 3;

    private sealed record Sample(string Name, double Game, double Mod, bool Inherits);

    /// <summary>Rules the mod's rows follow, most rows first. Fields an existing rule already owns are skipped.</summary>
    public static IReadOnlyList<ScaleRuleSuggestion> Suggest(ExmodPackage package, GameTableIndex game)
    {
        var owned = package.ScaleRules
            .Select(r => (File: r.CurrentFile.ToUpperInvariant(), Field: r.TopField))
            .ToHashSet();
        var results = new List<ScaleRuleSuggestion>();
        foreach (var row in package.Rows)
        {
            if (game.Rows(row.CurrentFile) is not { } table)
            {
                continue;
            }

            var fields = row.FileItems.SelectMany(i => i.Fields.Keys).Where(k => k != "Name").Distinct(StringComparer.Ordinal).ToList();
            foreach (var field in fields)
            {
                if (!owned.Contains((row.CurrentFile.ToUpperInvariant(), field)))
                {
                    results.AddRange(SuggestForField(row, field, table, game));
                }
            }
        }

        return [.. results.OrderByDescending(s => s.RowsCovered)];
    }

    private static IEnumerable<ScaleRuleSuggestion> SuggestForField(ExmodFileRow modRow, string field, JsonObject table, GameTableIndex game)
    {
        var file = modRow.CurrentFile;
        var defaults = game.Defaults(file);
        var decimalField = game.IsDecimalField(file, field);

        // What the mod sets this field to on each game row, by the game's own row name.
        var modValues = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        var samples = new List<Sample>();
        foreach (var item in modRow.FileItems)
        {
            if (game.GameRowName(file, item.Name) is not { } gameName || !item.Fields.TryGetValue(field, out var modNode))
            {
                continue;
            }

            modValues.TryAdd(gameName, modNode);
            var own = (table[gameName] as JsonObject)?[field];
            if (ScaleRules.TryReadNumber(modNode, out var mod) && ScaleRules.TryReadNumber(own ?? defaults?[field], out var gameValue) && mod != gameValue)
            {
                samples.Add(new Sample(gameName, gameValue, mod, own is null));
            }
        }

        if (samples.Count < MinRows)
        {
            yield break;
        }

        var rowsWithField = samples.Count;
        var candidates = Candidates(samples);
        var remaining = samples.ToList();
        for (var made = 0; made < MaxPerField && remaining.Count >= MinRows; made++)
        {
            // The pattern that explains most of the rows not explained yet.
            var best = candidates
                .Select(c => (c.Operation, c.Value, Rows: remaining.Where(s => Reproduces(s, c.Operation, c.Value, decimalField)).ToList()))
                .OrderByDescending(c => c.Rows.Count)
                .ThenBy(c => c.Operation == ScaleOperation.Multiply ? 0 : 1)
                .FirstOrDefault();
            if (best.Rows is null || best.Rows.Count < MinRows)
            {
                yield break;
            }

            remaining.RemoveAll(best.Rows.Contains);
            if (Build(file, field, best.Operation, best.Value, best.Rows, table, defaults, decimalField, modValues, game, rowsWithField) is { } suggestion)
            {
                yield return suggestion;
            }
        }
    }

    /// <summary>
    /// Factors (rounded to read well) and set-to values the samples point to, the most common first
    /// and only those (a mod that copies whole rows has thousands of one-off values; trying each
    /// against every row made a big mod slow to check).
    /// </summary>
    private static List<(ScaleOperation Operation, double Value)> Candidates(List<Sample> samples)
    {
        var list = new List<(ScaleOperation, double)>();
        foreach (var s in samples)
        {
            if (s.Game != 0)
            {
                // Whole-number rounding blurs a small value's ratio (7 × 1.5 is written 11), so the
                // coarse rounding is a candidate too.
                var ratio = s.Mod / s.Game;
                list.Add((ScaleOperation.Multiply, Math.Round(ratio, 1)));
                list.Add((ScaleOperation.Multiply, Math.Round(ratio, 2)));
                list.Add((ScaleOperation.Multiply, Math.Round(ratio, 4)));
            }

            list.Add((ScaleOperation.Set, s.Mod));
        }

        return [.. list
            .Where(c => !(c.Item1 == ScaleOperation.Multiply && (c.Item2 == 1 || c.Item2 == 0)))
            .GroupBy(c => c)
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count())
            .Take(24)
            .Select(g => g.Key)];
    }

    private static bool IsInteger(double gameValue, bool? decimalField) => decimalField is { } isDecimal ? !isDecimal : gameValue == Math.Floor(gameValue);

    /// <summary>Whether the rule turns this row's game value into exactly the mod's value.</summary>
    private static bool Reproduces(Sample s, ScaleOperation operation, double value, bool? decimalField) =>
        Same(ScaleRules.Compute(s.Game, operation, value, IsInteger(s.Game, decimalField)).Value, s.Mod);

    private static bool Same(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(b));

    private static ScaleRuleSuggestion? Build(string file, string field, ScaleOperation operation, double value, List<Sample> group, JsonObject table, JsonObject? defaults,
        bool? decimalField, Dictionary<string, JsonNode?> modValues, GameTableIndex game, int rowsWithField)
    {
        var includeInherited = group.Any(s => s.Inherits);
        var groupNames = group.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Every game row the rule would change with no filter, and whether the mod has that row's
        // field at another value (a rule there would overwrite the mod's own edit: never allowed).
        var wouldChange = new List<string>();
        var clashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, node) in table)
        {
            var own = (node as JsonObject)?[field];
            if ((own is null && !includeInherited) || !ScaleRules.TryReadNumber(own ?? defaults?[field], out var before))
            {
                continue;
            }

            var after = ScaleRules.Compute(before, operation, value, IsInteger(before, decimalField)).Value;
            if (Same(after, before))
            {
                continue;
            }

            wouldChange.Add(name);
            if (!groupNames.Contains(name) && modValues.TryGetValue(name, out var modNode)
                && !(ScaleRules.TryReadNumber(modNode, out var modNumber) && Same(modNumber, after)))
            {
                clashes.Add(name);
            }
        }

        string? filter = null;
        var covered = groupNames;
        var extra = wouldChange.Count(n => !groupNames.Contains(n));
        if (clashes.Count > 0 || group.Count < WholeTableShare * wouldChange.Count)
        {
            // Narrow it by name, the way Scale rules filter: a word may only match rows this
            // pattern explains, so the rule never changes a row the mod leaves alone or changes
            // another way. Short words first, so new game rows named alike are caught too.
            var outside = wouldChange.Where(n => !groupNames.Contains(n)).ToList();
            var words = groupNames
                .SelectMany(n => n.Split('_', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3 && !w.All(char.IsDigit)).Append(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(w => !outside.Any(o => o.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .Select(w => (Word: w, Matches: groupNames.Where(n => n.Contains(w, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase)))
                .ToList();

            var picked = new List<string>();
            covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var next = words
                    .Select(w => (w.Word, New: w.Matches.Count(m => !covered.Contains(m))))
                    .Where(w => w.New > 0)
                    .OrderByDescending(w => w.New)
                    .ThenBy(w => w.Word.Length)
                    .ThenBy(w => w.Word, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (next.Word is null)
                {
                    break;
                }

                picked.Add(next.Word);
                covered.UnionWith(words.First(w => w.Word == next.Word).Matches);
            }

            if (covered.Count < MinRows)
            {
                return null;
            }

            filter = string.Join(", ", picked);
            extra = 0;
        }

        var rule = new ScaleRule
        {
            Label = LabelFor(field, operation, value),
            CurrentFile = file,
            Field = field,
            Operation = operation,
            Value = value,
            RowFilter = filter,
            IncludeInherited = includeInherited,
        };

        // Proof: what Apply would really write. Every covered row gets the mod's own value, and
        // nothing the mod changes another way is touched.
        var (generated, _) = ScaleRules.Generate([rule], game.DataFolder, new MergeReport(), game);
        var written = generated.TryGetValue(file, out var byRow) ? byRow : new Dictionary<string, Dictionary<string, JsonNode?>>();
        foreach (var (name, fields) in written)
        {
            var mine = modValues.TryGetValue(name, out var modNode) ? modNode : null;
            if (mine is not null && !(ScaleRules.TryReadNumber(mine, out var m) && ScaleRules.TryReadNumber(fields[field], out var w) && Same(m, w)))
            {
                return null;
            }

            if (mine is null && filter is not null)
            {
                return null;
            }
        }

        var coveredCount = written.Keys.Count(covered.Contains);
        if (coveredCount < MinRows)
        {
            return null;
        }

        var tableName = GameTableIndex.TableNameOf(file);
        var text = $"{rule.Label} on {coveredCount} of this mod's {rowsWithField} changed rows in {tableName}"
            + (filter is null ? "" : $" (rows named like: {Shorten(filter)})")
            + (extra > 0 ? $". It also changes {extra} game row(s) this mod doesn't change yet, likely added by a game update." : ".");
        return new ScaleRuleSuggestion(rule, coveredCount, rowsWithField, extra, text);
    }

    private static string LabelFor(string field, ScaleOperation operation, double value)
    {
        var label = GameItemEditor.FieldText(field, isNumber: true).Label;
        return operation == ScaleOperation.Set
            ? $"{label} set to {EditorValueText.FormatNumber(value)}"
            : $"{label} ×{value.ToString("0.####", CultureInfo.InvariantCulture)}";
    }

    private static string Shorten(string filter) => filter.Length <= 80 ? filter : filter[..77].TrimEnd(',', ' ') + "…";
}
