using System.Text.Json.Nodes;
using IcarusStarlink.PakIO.DataChanges;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// The extracted game data, looked up by table name ("D_Itemable" → "Traits-D_Itemable.json") and
/// read once per table. Also knows where a link points: Icarus rows link to other rows as
/// {"RowName": "X"} and leave the target table to the table's own Defaults block
/// (D_ItemsStatic's Defaults say Itemable → D_Itemable, Audio → D_ItemAudioData, AmmoType →
/// D_AmmoTypes …), so links are resolved from the game data itself, never a hand-kept list.
/// Its lazily-filled lookups are locked, so one index can be shared by more than one thread.
/// </summary>
public sealed class GameTableIndex(string dataFolder)
{
    private readonly Lock _gate = new();
    private Dictionary<string, string>? _currentFileByTable;
    private readonly Dictionary<string, (JsonObject? Rows, JsonObject? Defaults)> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _rowNames = new(StringComparer.OrdinalIgnoreCase);

    public string DataFolder { get; } = dataFolder;

    /// <summary>"D_Itemable" → "Traits-D_Itemable.json"; null when the game has no such table.</summary>
    public string? CurrentFileFor(string tableName)
    {
        lock (_gate)
        {
            return TablesByName().GetValueOrDefault(tableName);
        }
    }

    /// <summary>
    /// Like CurrentFileFor, but null when the game has a table by that name in more than one
    /// folder: then which one a mod means can't be told from the name alone.
    /// </summary>
    public string? OnlyCurrentFileFor(string tableName)
    {
        lock (_gate)
        {
            TablesByName();
            return _tableNameCounts!.GetValueOrDefault(tableName) == 1 ? _currentFileByTable!.GetValueOrDefault(tableName) : null;
        }
    }

    /// <summary>Whether there's extracted game data at all (without it, every table looks unknown).</summary>
    public bool HasAnyTable()
    {
        lock (_gate)
        {
            return TablesByName().Count > 0;
        }
    }

    /// <summary>How many folders have a table of each name (set with _currentFileByTable).</summary>
    private Dictionary<string, int>? _tableNameCounts;

    /// <summary>Under _gate.</summary>
    private Dictionary<string, string> TablesByName()
    {
        if (_currentFileByTable is null)
        {
            var groups = ScaleRules.ListTables(DataFolder)
                .GroupBy(f => Path.GetFileNameWithoutExtension(f.Split('-').Last()), StringComparer.OrdinalIgnoreCase)
                .ToList();
            _tableNameCounts = groups.ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            _currentFileByTable = groups.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        return _currentFileByTable;
    }

    /// <summary>"Traits-D_Itemable.json" → "D_Itemable".</summary>
    public static string TableNameOf(string currentFile) => Path.GetFileNameWithoutExtension(currentFile.Split('-').Last());

    public JsonObject? Rows(string currentFile) => Load(currentFile).Rows;

    public JsonObject? Defaults(string currentFile) => Load(currentFile).Defaults;

    /// <summary>The game's row, found the way the game finds it (ignoring case).</summary>
    public JsonObject? Row(string currentFile, string rowName) =>
        Rows(currentFile)?[rowName] as JsonObject ?? (GameRowName(currentFile, rowName) is { } exact ? Rows(currentFile)?[exact] as JsonObject : null);

    /// <summary>Whether the game has the row, ignoring case — Unreal row names (FNames) are case-insensitive, so "FirePit" finds "Firepit" in game.</summary>
    public bool HasRow(string currentFile, string rowName) => GameRowName(currentFile, rowName) is not null;

    /// <summary>The row's name as the game spells it ("FirePit" → "Firepit"); null when the game has no such row.</summary>
    public string? GameRowName(string currentFile, string rowName)
    {
        lock (_gate)
        {
            if (!_rowNames.TryGetValue(currentFile, out var names))
            {
                names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (key, _) in Rows(currentFile) ?? [])
                {
                    names.TryAdd(key, key);
                }

                _rowNames[currentFile] = names;
            }

            return names.GetValueOrDefault(rowName);
        }
    }

    private const string ItemTemplateFile = "Items-D_ItemTemplate.json";
    private Dictionary<string, List<string>>? _templatesByItem;

    /// <summary>
    /// The D_ItemTemplate rows that make this D_ItemsStatic item (their ItemStaticData.RowName),
    /// compared ignoring case. Usually one, named like the item — but 389 game templates aren't
    /// (template Stone_Furnace makes item Kit_Stone_Furnace, Envirosuit_Shengong2 makes
    /// Envirosuit_Shengong_2), and recipes and workshop items hand out the TEMPLATE name, so
    /// anything looking for "what makes this item" must go through here, not the item's name.
    /// </summary>
    public IReadOnlyList<string> TemplatesFor(string itemName)
    {
        lock (_gate)
        {
            if (_templatesByItem is null)
            {
                _templatesByItem = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var (template, value) in Rows(ItemTemplateFile) ?? [])
                {
                    var item = (value?["ItemStaticData"] as JsonObject)?["RowName"] is JsonValue v && v.TryGetValue<string>(out var s) && s is { Length: > 0 } and not "None" ? s : template;
                    if (!_templatesByItem.TryGetValue(item, out var list))
                    {
                        list = [];
                        _templatesByItem[item] = list;
                    }

                    list.Add(template);
                }
            }

            return _templatesByItem.GetValueOrDefault(itemName) ?? [];
        }
    }

    /// <summary>
    /// The names a recipe output or workshop Item uses for this item: its templates (TemplatesFor),
    /// plus the item's own name when no template is called that (older or modded data that points
    /// straight at the item). Case-insensitive, like the game.
    /// </summary>
    public IReadOnlySet<string> ProductNamesFor(string itemName)
    {
        var names = new HashSet<string>(TemplatesFor(itemName), StringComparer.OrdinalIgnoreCase);
        if (!HasRow(ItemTemplateFile, itemName))
        {
            names.Add(itemName);
        }

        return names;
    }

    /// <summary>A step in IsDecimalField's path that matches every element of a list.</summary>
    public const string AnyElement = "[]";

    /// <summary>A step in IsDecimalField's path that matches every key of an object.</summary>
    public const string AnyKey = "*";

    private readonly Dictionary<string, bool?> _decimalFields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a numeric field holds fractions anywhere in the game's table: true when any row (or
    /// the table default) has a value like 0.5 or written as 1.0, false when every value is whole,
    /// null when the game has no number there. Decided per FIELD, not per value: Weight 100 on one
    /// row doesn't make Weight a whole-number field when another row has 0.5, and a multiplier the
    /// game stores as 1 everywhere is still whole. Steps are object keys, AnyKey or AnyElement.
    /// </summary>
    public bool? IsDecimalField(string currentFile, string top, IReadOnlyList<string> steps)
    {
        var cacheKey = $"{currentFile}|{top}|{string.Join('\u001f', steps)}";
        lock (_gate)
        {
            if (_decimalFields.TryGetValue(cacheKey, out var known))
            {
                return known;
            }
        }

        bool? result = null;
        void Visit(JsonNode? node, int index)
        {
            if (result == true || node is null)
            {
                return;
            }

            if (index == steps.Count)
            {
                if (node is JsonValue value && ScaleRules.TryReadNumber(value, out var number))
                {
                    var fraction = number != Math.Floor(number)
                        || (value.TryGetValue<System.Text.Json.JsonElement>(out var element) && element.GetRawText().IndexOfAny(['.', 'e', 'E']) >= 0);
                    result = fraction || result == true;
                }

                return;
            }

            var step = steps[index];
            switch (node)
            {
                case JsonArray array when step == AnyElement:
                    foreach (var element in array)
                    {
                        Visit(element, index + 1);
                    }

                    break;
                case JsonObject obj when step == AnyKey:
                    foreach (var (_, child) in obj)
                    {
                        Visit(child, index + 1);
                    }

                    break;
                case JsonObject obj:
                    Visit(obj[step], index + 1);
                    break;
            }
        }

        foreach (var (_, row) in Rows(currentFile) ?? [])
        {
            Visit((row as JsonObject)?[top], 0);
        }

        Visit(Defaults(currentFile)?[top], 0);

        lock (_gate)
        {
            _decimalFields[cacheKey] = result;
        }

        return result;
    }

    /// <summary>IsDecimalField for a Scale-rule path ("Field", "Dict.*", "List[].Field").</summary>
    public bool? IsDecimalField(string currentFile, string scalePath)
    {
        var segments = ScaleRules.SplitPath(scalePath);
        if (segments.Length == 0)
        {
            return null;
        }

        var steps = new List<string>();
        for (var i = 0; i < segments.Length; i++)
        {
            if (i > 0)
            {
                steps.Add(ScaleRules.StripList(segments[i]));
            }

            if (segments[i].EndsWith("[]", StringComparison.Ordinal))
            {
                steps.Add(AnyElement);
            }
        }

        return IsDecimalField(currentFile, ScaleRules.StripList(segments[0]), steps);
    }

    /// <summary>The table a top-level link field points into, from the table's own Defaults ("Itemable" in D_ItemsStatic → "D_Itemable").</summary>
    public string? LinkTarget(string currentFile, string field) =>
        (Defaults(currentFile)?[field] as JsonObject)?["DataTableName"]?.GetValue<string>();

    private (JsonObject? Rows, JsonObject? Defaults) Load(string currentFile)
    {
        lock (_gate)
        {
            if (!_tables.TryGetValue(currentFile, out var table))
            {
                var file = BaseDataFileReader.ParseFile(DataFolder, currentFile, report: null);
                table = file is null ? (null, null) : (DataTableJson.RowsToKeyedObject(file, _ => { }), file["Defaults"] as JsonObject);
                _tables[currentFile] = table;
            }

            return table;
        }
    }
}
