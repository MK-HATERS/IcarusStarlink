using System.Globalization;
using System.Text.Json.Nodes;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Exmod;

public enum ScaleOperation
{
    /// <summary>New value = game value × Value.</summary>
    Multiply,

    /// <summary>New value = Value, whatever the game had.</summary>
    Set,
}

/// <summary>
/// "Scale one field across a whole game table" — the shape of nearly every change in a real
/// quality-of-life mod (Deyvid's All-in-One, Nexus 317: stacks ×10 on 1337 items, crafting time
/// ×0.35 on 1352 recipes, armour stats ×1.5, …). A mod keeps its rules (ExmodPackage.ScaleRules)
/// and the rows they produce, so the rows can be rebuilt from current game data after an update
/// instead of going stale the way a pak of full table copies does.
/// </summary>
public sealed class ScaleRule
{
    public bool Enabled { get; set; } = true;

    /// <summary>What the rule is for, in plain words ("Stack sizes"). Display only.</summary>
    public string Label { get; set; } = "";

    /// <summary>The game table, EXMOD-style: "Traits-D_Itemable.json".</summary>
    public string CurrentFile { get; set; } = "";

    /// <summary>
    /// The field, as a path: "MaxStack"; "ArmourStats.*" (every stat in that dictionary);
    /// "StatsGranted.(Value=\"BaseFoodConsumptionPerHour_+\")" (one stat); "Inputs[].Count"
    /// (that field in every element of a list). Only numbers are changed.
    /// </summary>
    public string Field { get; set; } = "";

    public ScaleOperation Operation { get; set; } = ScaleOperation.Multiply;

    public double Value { get; set; } = 1;

    /// <summary>Only rows whose name contains this text (case-insensitive); several words separated by commas match any of them ("Storage, Crate, Cupboard"). Empty for every row.</summary>
    public string? RowFilter { get; set; }

    /// <summary>
    /// Also change rows that don't set the field themselves and use the table's default. Off for
    /// stack sizes: items that inherit MaxStack 1 are tools, weapons and armour, and ×10 would make
    /// them stack (the real All-in-One only scales the 1337 items that already stack).
    /// </summary>
    public bool IncludeInherited { get; set; } = true;

    /// <summary>
    /// Rows this rule leaves alone even though its filter catches them: the modder set the field
    /// by hand there ("Keep my value" in the editor) and Apply must not put the rule's value back.
    /// Row names ignore case. Saved as "exceptRows"; older files have none.
    /// </summary>
    public List<string> ExceptRows { get; set; } = [];

    /// <summary>The first path segment — the top-level field an EXMOD item replaces as a whole.</summary>
    public string TopField => ScaleRules.SplitPath(Field).FirstOrDefault() is { } first ? ScaleRules.StripList(first) : "";

    /// <summary>True when <paramref name="rowName"/> is on the rule's "except these rows" list.</summary>
    public bool Excepts(string rowName) => ExceptRows.Any(r => string.Equals(r, rowName, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the rule does, in short: "Weight ×0.5", "MaxStack = 100".</summary>
    public string Summary => Operation == ScaleOperation.Set
        ? $"{Field} = {Value.ToString("0.######", CultureInfo.InvariantCulture)}"
        : $"{Field} ×{Value.ToString("0.######", CultureInfo.InvariantCulture)}";

    /// <summary>The rule's name for messages: its label and what it does ("Lighter items (Weight ×0.5)"), or just what it does.</summary>
    public string DisplayName => Label.Trim().Length == 0 || Label.Contains(Summary, StringComparison.OrdinalIgnoreCase)
        ? (Label.Trim().Length == 0 ? Summary : Label.Trim())
        : $"{Label.Trim()} ({Summary})";

    public ScaleRule Clone()
    {
        var copy = (ScaleRule)MemberwiseClone();
        copy.ExceptRows = [.. ExceptRows];
        return copy;
    }
}

/// <summary>A value in the mod that a rule would replace on Apply: set by hand, not by the rule.</summary>
/// <param name="RuleIndexes">The rules (positions in the list given) that write this field of this row.</param>
public sealed record ScaleHandEdit(string CurrentFile, string RowName, string Field, IReadOnlyList<int> RuleIndexes, JsonNode? ModValue, JsonNode? RuleValue);

/// <summary>
/// Which fields of which rows a set of rules writes, and the value each gets — ScaleRules.Generate
/// itself, so the editor's "set by rule" tags match exactly what Apply does (filter, inherited
/// rows, exceptions, values the rule leaves unchanged).
/// </summary>
public sealed class ScaleOwnership
{
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, JsonNode?>>> _values;
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, List<int>>>> _owners;

    internal ScaleOwnership(Dictionary<string, Dictionary<string, Dictionary<string, JsonNode?>>> values, Dictionary<string, Dictionary<string, Dictionary<string, List<int>>>> owners)
    {
        _values = values;
        _owners = owners;
    }

    /// <summary>True when a rule writes this field of this row; <paramref name="rules"/> are their positions, <paramref name="value"/> what they write (don't change it).</summary>
    public bool TryGet(string currentFile, string rowName, string field, out IReadOnlyList<int> rules, out JsonNode? value)
    {
        rules = [];
        value = null;
        if (!_owners.TryGetValue(currentFile, out var byRow) || !byRow.TryGetValue(rowName, out var byField) || !byField.TryGetValue(field, out var owners))
        {
            return false;
        }

        rules = owners;
        value = _values[currentFile][rowName][field];
        return true;
    }
}

/// <summary>A rule's effect on current game data: how many rows change, and one example.</summary>
public sealed record ScalePreview(int RowCount, int ValueCount, string? Example);

/// <summary>What applying a mod's rules did.</summary>
public sealed record ScaleApplyResult(int RowsWritten, IReadOnlyList<ScalePreview> PerRule);

public static class ScaleRules
{
    /// <summary>
    /// Rebuilds the rows a mod's rules own. First the fields the PREVIOUS rules wrote (run again on
    /// current game data) and the fields the new rules will write are removed from the mod's copies
    /// of GAME rows — so turning a rule off or changing its factor or filter never leaves its old
    /// values behind — then each enabled rule is applied, in order, to current game data (a later
    /// rule on the same field compounds on an earlier one). Only those fields go: a hand edit of the
    /// same field on a row no rule covers (an Envirosuit's ArmourStats beside a rule for "Plate")
    /// stays, as do rows the mod adds itself (a new item's own Itemable) and every other field.
    /// Sets <paramref name="package"/>.ScaleRules to <paramref name="rules"/>.
    /// </summary>
    /// <param name="game">The game's tables, shared across calls (the editor keeps one); null reads them fresh.</param>
    public static ScaleApplyResult ApplyToPackage(ExmodPackage package, IReadOnlyList<ScaleRule> rules, string dataFolder, MergeReport report, GameTableIndex? game = null)
    {
        game ??= new GameTableIndex(dataFolder);
        var (previous, _) = Generate(package.ScaleRules, dataFolder, new MergeReport(), game);
        var (generated, previews) = Generate(rules, dataFolder, report, game);
        bool Owns(Dictionary<string, Dictionary<string, Dictionary<string, JsonNode?>>> written, string file, string rowName, string field) =>
            written.TryGetValue(file, out var byRow) && byRow.TryGetValue(rowName, out var fields) && fields.ContainsKey(field);

        // A row a rule now excepts holds the modder's own value: the previous rules' pass mustn't take it out.
        bool Excepted(string file, string rowName, string field) =>
            !Owns(generated, file, rowName, field)
            && rules.Any(r => string.Equals(r.CurrentFile, file, StringComparison.OrdinalIgnoreCase) && r.TopField == field && r.Excepts(rowName));

        foreach (var row in package.Rows.Where(r => previous.ContainsKey(r.CurrentFile) || generated.ContainsKey(r.CurrentFile)).ToList())
        {
            var emptied = new HashSet<ExmodFileItem>();
            foreach (var item in row.FileItems.Where(i => game.HasRow(row.CurrentFile, i.Name)))
            {
                var keys = item.Fields.Keys.Where(k => (Owns(previous, row.CurrentFile, item.Name, k) || Owns(generated, row.CurrentFile, item.Name, k))
                    && !Excepted(row.CurrentFile, item.Name, k)).ToList();
                keys.ForEach(k => item.Fields.Remove(k));
                if (keys.Count > 0 && item.Fields.Count == 0)
                {
                    emptied.Add(item);
                }
            }

            // Only what this pass emptied goes: an item or table the author left empty on purpose stays.
            row.FileItems.RemoveAll(emptied.Contains);
            if (emptied.Count > 0 && row.FileItems.Count == 0)
            {
                package.Rows.Remove(row);
            }
        }

        var written = 0;
        foreach (var (file, items) in generated)
        {
            var row = package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, file, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new ExmodFileRow { CurrentFile = file };
                // Keep the EndOfMod sentinel last, the way every real EXMOD has it.
                var end = package.Rows.FindIndex(r => ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile));
                package.Rows.Insert(end < 0 ? package.Rows.Count : end, row);
            }

            var byName = new Dictionary<string, ExmodFileItem>(StringComparer.OrdinalIgnoreCase); // row names ignore case, like the game
            foreach (var existing in row.FileItems)
            {
                byName.TryAdd(existing.Name, existing);
            }

            foreach (var (name, fields) in items)
            {
                if (!byName.TryGetValue(name, out var item))
                {
                    item = new ExmodFileItem { Name = name };
                    row.FileItems.Add(item);
                    byName[name] = item;
                }

                foreach (var (field, value) in fields)
                {
                    item.Fields[field] = value;
                }

                written++;
            }
        }

        package.ScaleRules = [.. rules.Select(r => r.Clone())];
        return new ScaleApplyResult(written, previews);
    }

    /// <summary>What one rule would change, without touching any mod.</summary>
    public static ScalePreview Preview(ScaleRule rule, string dataFolder, GameTableIndex? game = null)
    {
        var copy = rule.Clone();
        copy.Enabled = true;
        return Generate([copy], dataFolder, new MergeReport(), game).Previews[0];
    }

    /// <summary>
    /// Runs the enabled rules against current game data. Returns, per table, per row name, the new
    /// value of each top-level field the rules touched (whole value, ready to drop into an EXMOD
    /// item), plus one preview per rule (disabled rules preview as zero).
    /// </summary>
    public static (Dictionary<string, Dictionary<string, Dictionary<string, JsonNode?>>> Generated, IReadOnlyList<ScalePreview> Previews) Generate(
        IReadOnlyList<ScaleRule> rules, string dataFolder, MergeReport report, GameTableIndex? game = null) =>
        GenerateCore(rules, dataFolder, report, game, owners: null);

    /// <summary>Which fields of which rows the enabled rules write, by which rule, and the value (see ScaleOwnership).</summary>
    public static ScaleOwnership Ownership(IReadOnlyList<ScaleRule> rules, string dataFolder, GameTableIndex? game = null)
    {
        var owners = new Dictionary<string, Dictionary<string, Dictionary<string, List<int>>>>(StringComparer.OrdinalIgnoreCase);
        var (generated, _) = GenerateCore(rules, dataFolder, new MergeReport(), game, owners);
        return new ScaleOwnership(generated, owners);
    }

    /// <summary>
    /// Values in the mod's copies of game rows that applying <paramref name="rules"/> would replace,
    /// and that are the modder's own: not what the rules write anyway, not what the mod's current
    /// (last applied) rules wrote, and not the game's own value. Rows a rule excepts aren't listed
    /// (the rule leaves them alone). Asked about before Apply so a hand edit never vanishes quietly.
    /// After a game update changes a value a rule scales, that row can show here too: the mod still
    /// holds the rule's result on the old game value.
    /// </summary>
    public static IReadOnlyList<ScaleHandEdit> FindHandEdits(ExmodPackage package, IReadOnlyList<ScaleRule> rules, string dataFolder, GameTableIndex? game = null)
    {
        game ??= new GameTableIndex(dataFolder);
        var now = Ownership(rules, dataFolder, game);
        var before = Ownership(package.ScaleRules, dataFolder, game);
        var found = new List<ScaleHandEdit>();
        foreach (var row in package.Rows)
        {
            foreach (var item in row.FileItems)
            {
                foreach (var (field, value) in item.Fields)
                {
                    if (!now.TryGet(row.CurrentFile, item.Name, field, out var owners, out var ruleValue) || SameValue(value, ruleValue))
                    {
                        continue;
                    }

                    if (before.TryGet(row.CurrentFile, item.Name, field, out _, out var written) && SameValue(value, written))
                    {
                        continue;
                    }

                    var gameValue = game.Row(row.CurrentFile, item.Name)?[field] ?? game.Defaults(row.CurrentFile)?[field];
                    if (SameValue(value, gameValue))
                    {
                        continue;
                    }

                    found.Add(new ScaleHandEdit(row.CurrentFile, item.Name, field, owners, value, ruleValue));
                }
            }
        }

        return found;
    }

    /// <summary>Two JSON values hold the same thing: numbers compare by value (100 = 100.0, a long = a double), objects and lists part by part.</summary>
    public static bool SameValue(JsonNode? a, JsonNode? b)
    {
        if (TryGetNumber(a, out var x, out _) && TryGetNumber(b, out var y, out _))
        {
            return Math.Abs(x - y) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
        }

        switch (a, b)
        {
            case (JsonObject oa, JsonObject ob):
                return oa.Count == ob.Count && oa.All(kv => ob.TryGetPropertyValue(kv.Key, out var other) && SameValue(kv.Value, other));
            case (JsonArray la, JsonArray lb):
                return la.Count == lb.Count && la.Zip(lb).All(p => SameValue(p.First, p.Second));
            default:
                return JsonNode.DeepEquals(a, b);
        }
    }

    private static (Dictionary<string, Dictionary<string, Dictionary<string, JsonNode?>>> Generated, IReadOnlyList<ScalePreview> Previews) GenerateCore(
        IReadOnlyList<ScaleRule> rules, string dataFolder, MergeReport report, GameTableIndex? game,
        Dictionary<string, Dictionary<string, Dictionary<string, List<int>>>>? owners)
    {
        game ??= new GameTableIndex(dataFolder);
        var generated = new Dictionary<string, Dictionary<string, Dictionary<string, JsonNode?>>>(StringComparer.OrdinalIgnoreCase);
        var previews = new List<ScalePreview>();

        for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            if (!rule.Enabled || rule.CurrentFile.Length == 0 || rule.TopField.Length == 0)
            {
                previews.Add(new ScalePreview(0, 0, null));
                continue;
            }

            var table = (Rows: game.Rows(rule.CurrentFile), Defaults: game.Defaults(rule.CurrentFile));
            if (table.Rows is null)
            {
                report.AddWarning($"No matching base file for '{rule.CurrentFile}' in the game data.");
                previews.Add(new ScalePreview(0, 0, null));
                continue;
            }

            var byRow = generated.TryGetValue(rule.CurrentFile, out var existing) ? existing : null;
            var path = SplitPath(rule.Field);
            var top = StripList(path[0]);
            // Whole or not is decided for the FIELD across the game's table, not per value: a
            // Weight of 100 in a field where another row has 0.5 is still a real number.
            var decimalField = game.IsDecimalField(rule.CurrentFile, rule.Field);
            int rowCount = 0, valueCount = 0;
            string? example = null;

            foreach (var (rowName, rowNode) in table.Rows)
            {
                if (!MatchesFilter(rowName, rule.RowFilter) || rule.Excepts(rowName))
                {
                    continue;
                }

                // Start from what earlier rules produced for this row, else the game's own value
                // (the row's, or the table default it inherits).
                var own = (rowNode as JsonObject)?[top];
                if (own is null && !rule.IncludeInherited)
                {
                    continue;
                }

                var current = byRow is not null && byRow.TryGetValue(rowName, out var fields) && fields.TryGetValue(top, out var produced)
                    ? produced?.DeepClone()
                    : (own ?? table.Defaults?[top])?.DeepClone();
                if (current is null)
                {
                    continue;
                }

                var changed = 0;
                string? firstChange = null;
                var updated = ApplyAt(current, path, 0, rule, decimalField, ref changed, ref firstChange);
                if (changed == 0)
                {
                    continue;
                }

                // Row names ignore case, like the game: a mod's "item_fiber" is the game's Item_Fiber.
                generated.TryAdd(rule.CurrentFile, new Dictionary<string, Dictionary<string, JsonNode?>>(StringComparer.OrdinalIgnoreCase));
                byRow = generated[rule.CurrentFile];
                if (!byRow.TryGetValue(rowName, out var rowFields))
                {
                    rowFields = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
                    byRow[rowName] = rowFields;
                }

                rowFields[top] = updated;
                if (owners is not null)
                {
                    owners.TryAdd(rule.CurrentFile, new Dictionary<string, Dictionary<string, List<int>>>(StringComparer.OrdinalIgnoreCase));
                    var ownersByRow = owners[rule.CurrentFile];
                    if (!ownersByRow.TryGetValue(rowName, out var ownersByField))
                    {
                        ownersByField = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                        ownersByRow[rowName] = ownersByField;
                    }

                    if (!ownersByField.TryGetValue(top, out var list))
                    {
                        list = [];
                        ownersByField[top] = list;
                    }

                    list.Add(ruleIndex);
                }

                rowCount++;
                valueCount += changed;
                example ??= $"{rowName}: {firstChange}";
            }

            previews.Add(new ScalePreview(rowCount, valueCount, example));
        }

        return (generated, previews);
    }

    /// <summary>Applies the rule at path[index..] inside node; returns the (possibly replaced) node.</summary>
    private static JsonNode? ApplyAt(JsonNode? node, string[] path, int index, ScaleRule rule, bool? decimalField, ref int changed, ref string? firstChange)
    {
        var segment = path[index];
        var isList = segment.EndsWith("[]", StringComparison.Ordinal);

        // index 0 names the top-level field itself, which `node` already is.
        if (index == 0)
        {
            return isList ? ApplyToList(node, path, index, rule, decimalField, ref changed, ref firstChange) : ApplyNext(node, path, index, rule, decimalField, ref changed, ref firstChange);
        }

        if (node is not JsonObject obj)
        {
            return node;
        }

        var key = StripList(segment);
        var keys = key == "*" ? obj.Select(kv => kv.Key).ToList() : obj.ContainsKey(key) ? [key] : [];
        foreach (var k in keys)
        {
            var child = obj[k];
            var replaced = isList ? ApplyToList(child, path, index, rule, decimalField, ref changed, ref firstChange) : ApplyNext(child, path, index, rule, decimalField, ref changed, ref firstChange);
            if (!ReferenceEquals(replaced, child))
            {
                obj[k] = replaced;
            }
        }

        return node;
    }

    private static JsonNode? ApplyToList(JsonNode? node, string[] path, int index, ScaleRule rule, bool? decimalField, ref int changed, ref string? firstChange)
    {
        if (node is not JsonArray array)
        {
            return node;
        }

        for (var i = 0; i < array.Count; i++)
        {
            var element = array[i];
            var replaced = ApplyNext(element, path, index, rule, decimalField, ref changed, ref firstChange);
            if (!ReferenceEquals(replaced, element))
            {
                array[i] = replaced;
            }
        }

        return node;
    }

    private static JsonNode? ApplyNext(JsonNode? node, string[] path, int index, ScaleRule rule, bool? decimalField, ref int changed, ref string? firstChange)
    {
        if (index + 1 < path.Length)
        {
            return ApplyAt(node, path, index + 1, rule, decimalField, ref changed, ref firstChange);
        }

        if (!TryGetNumber(node, out var before, out var valueIsInteger))
        {
            return node;
        }

        // The field's own kind when the game has numbers there; the value's only for a field the
        // game doesn't fill (then a value written "100" is whole, one made in code is real).
        var isInteger = decimalField is { } isDecimal ? !isDecimal : valueIsInteger;

        var (resultNumber, whole) = Compute(before, rule.Operation, rule.Value, isInteger);
        JsonNode result = whole ? JsonValue.Create((long)resultNumber) : JsonValue.Create(resultNumber);
        if (resultNumber == before)
        {
            return node;
        }

        changed++;
        firstChange ??= $"{Format(before)} → {Format(resultNumber)}";
        return result;
    }

    /// <summary>
    /// The number a rule makes of one game value, and whether it is written as a whole number.
    /// Whole-number fields stay whole numbers. Scaling never rounds a real amount down to nothing
    /// (an ingredient count of 1 × 0.4 stays 1, not free) unless the factor is 0. Shared with
    /// ScaleRuleSuggester, so a suggested rule is judged by exactly what Apply would write.
    /// </summary>
    internal static (double Value, bool Whole) Compute(double before, ScaleOperation operation, double value, bool isInteger)
    {
        var after = operation == ScaleOperation.Set ? value : before * value;
        if (isInteger && (operation == ScaleOperation.Multiply || after == Math.Round(after)))
        {
            var rounded = (long)Math.Clamp(Math.Round(after, MidpointRounding.AwayFromZero), int.MinValue, int.MaxValue);
            if (rounded == 0 && before != 0 && after != 0)
            {
                rounded = Math.Sign(after);
            }

            return (rounded, true);
        }

        return (Math.Round(after, 6), false);
    }

    /// <summary>
    /// A JSON number as a double, whatever it is held as: values parsed from a file convert freely,
    /// but values built in code (JsonValue.Create(5)) only answer to their own CLR type, so each
    /// numeric type is tried. Booleans and strings are not numbers.
    /// </summary>
    public static bool TryReadNumber(JsonNode? node, out double value) => TryGetNumber(node, out value, out _);

    /// <summary>TryReadNumber, plus whether the number is written whole in the file ("100", not "100.0").</summary>
    internal static bool TryReadNumber(JsonNode? node, out double value, out bool isInteger) => TryGetNumber(node, out value, out isInteger);

    private static bool TryGetNumber(JsonNode? node, out double value, out bool isInteger)
    {
        value = 0;
        isInteger = false;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<long>(out var l))
        {
            (value, isInteger) = (l, true);
            return true;
        }

        if (jsonValue.TryGetValue<int>(out var i))
        {
            (value, isInteger) = (i, true);
            return true;
        }

        if (jsonValue.TryGetValue<short>(out var sh))
        {
            (value, isInteger) = (sh, true);
            return true;
        }

        if (jsonValue.TryGetValue<double>(out var d))
        {
            value = d;
            // Only a number as written in the game's file can say it's whole ("100"); a double made
            // in code (an earlier rule's 0.5 × 2 = 1.0) serializes as "1" but is still a real number.
            isInteger = d == Math.Floor(d)
                && jsonValue.TryGetValue<System.Text.Json.JsonElement>(out var element)
                && !element.GetRawText().Contains('.');
            return true;
        }

        if (jsonValue.TryGetValue<float>(out var f))
        {
            value = f;
            return true;
        }

        if (jsonValue.TryGetValue<decimal>(out var m))
        {
            value = (double)m;
            isInteger = m == decimal.Truncate(m);
            return true;
        }

        return false;
    }

    /// <summary>No filter matches everything; otherwise the row name must contain any one of the comma-separated words.</summary>
    public static bool MatchesFilter(string rowName, string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return true;
        }

        var terms = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length == 0 || terms.Any(t => rowName.Contains(t, StringComparison.OrdinalIgnoreCase));
    }

    private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Splits "ArmourStats.(Value=\"BaseColdResistance_%\")" on dots outside parentheses/quotes.</summary>
    public static string[] SplitPath(string path)
    {
        var segments = new List<string>();
        var depth = 0;
        var inQuotes = false;
        var start = 0;
        for (var i = 0; i < path.Length; i++)
        {
            var c = path[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes && c == '(')
            {
                depth++;
            }
            else if (!inQuotes && c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }
            else if (!inQuotes && depth == 0 && c == '.')
            {
                segments.Add(path[start..i].Trim());
                start = i + 1;
            }
        }

        segments.Add(path[start..].Trim());
        return [.. segments.Where(s => s.Length > 0)];
    }

    public static string StripList(string segment) => segment.EndsWith("[]", StringComparison.Ordinal) ? segment[..^2] : segment;

    /// <summary>
    /// Field paths in a game table that hold numbers, for the Scale tool's field box: plain numeric
    /// fields ("Weight"), every key of a numeric dictionary plus a "*" for all of them
    /// ("ArmourStats.*"), and numeric fields of list elements ("Inputs[].Count"). Most common first.
    /// </summary>
    public static IReadOnlyList<string> ListNumericFields(string dataFolder, string currentFile)
    {
        var file = BaseDataFileReader.ParseFile(dataFolder, currentFile, report: null);
        if (file is null)
        {
            return [];
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        void Count(string path) => counts[path] = counts.GetValueOrDefault(path) + 1;

        var rows = (file["Rows"] as JsonArray ?? []).OfType<JsonObject>();
        var defaults = file["Defaults"] as JsonObject;
        foreach (var row in defaults is null ? rows : rows.Append(defaults))
        {
            foreach (var (field, value) in row)
            {
                if (field == "Name")
                {
                    continue;
                }

                if (TryGetNumber(value, out _, out _))
                {
                    Count(field);
                }
                else if (value is JsonObject dict && dict.Any(kv => TryGetNumber(kv.Value, out _, out _)))
                {
                    Count($"{field}.*");
                    foreach (var (key, inner) in dict)
                    {
                        if (TryGetNumber(inner, out _, out _))
                        {
                            Count($"{field}.{key}");
                        }
                    }
                }
                else if (value is JsonArray list)
                {
                    foreach (var element in list.OfType<JsonObject>())
                    {
                        foreach (var (key, inner) in element)
                        {
                            if (TryGetNumber(inner, out _, out _))
                            {
                                Count($"{field}[].{key}");
                            }
                        }
                    }
                }
            }
        }

        return [.. counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key)];
    }

    /// <summary>Every game table, EXMOD-style ("Traits-D_Itemable.json"), for the Scale tool's table box.</summary>
    public static IReadOnlyList<string> ListTables(string dataFolder)
    {
        if (!Directory.Exists(dataFolder))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(dataFolder, "D_*.json", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dataFolder, f).Replace('\\', '/').Replace('/', '-'))
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)];
    }
}
