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
            _currentFileByTable ??= ScaleRules.ListTables(DataFolder)
                .GroupBy(f => Path.GetFileNameWithoutExtension(f.Split('-').Last()), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            return _currentFileByTable.GetValueOrDefault(tableName);
        }
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
