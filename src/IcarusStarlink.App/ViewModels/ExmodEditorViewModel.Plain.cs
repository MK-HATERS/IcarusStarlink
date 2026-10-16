using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.PakIO.Exmod;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The plain side of the mod editor (shared with the Editor page): the item list grouped by the
/// part of the game it changes, with in-game names and how many values differ from the game, the
/// stat picker for stats fields, and "Open in Editor".
/// </summary>
public sealed partial class ExmodEditorViewModel
{
    private GameItemNames? _itemNames;
    private IReadOnlyList<StatChoice>? _statChoices;
    private bool _gameDataMissing;

    /// <summary>Headings for the list, by table ("D_Armour" → "Armour"); anything else reads from the table's own name.</summary>
    private static readonly Dictionary<string, string> GroupNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["D_ItemsStatic"] = "Items",
        ["D_ItemTemplate"] = "Item templates",
        ["D_Itemable"] = "Names, weights, stacks",
        ["D_ProcessorRecipes"] = "Recipes",
        ["D_ModifierStates"] = "Buffs and effects",
        ["D_WorkshopItems"] = "Workshop items",
        ["D_Consumable"] = "Eating or using",
        ["D_Equippable"] = "Bonuses when equipped",
        ["D_Decayable"] = "Decay and spoiling",
        ["D_Durable"] = "Durability",
        ["D_ToolDamage"] = "Tool damage",
        ["D_InventoryInfo"] = "Inventories",
        ["D_Talents"] = "Talents and workshop tabs",
    };

    /// <summary>A list row with its group, in-game name and change count.</summary>
    private EditorItemViewModel MakeItem(string currentFile, string rowName, ExmodFileItem? item = null)
    {
        var table = GameTableIndex.TableNameOf(currentFile);
        var names = ItemNames();
        var gameItem = names?.ItemFor(currentFile, rowName);
        var title = (gameItem is null ? null : names!.DisplayName(gameItem)) ?? rowName;
        item ??= FindItem(currentFile, rowName);
        var isNew = HasGameTable(currentFile) && !GameTables.HasRow(currentFile, rowName);
        return new EditorItemViewModel(currentFile, rowName)
        {
            Group = GroupNames.TryGetValue(table, out var g) ? g : (table.StartsWith("D_", StringComparison.Ordinal) ? Spaced(table[2..]) : table),
            Title = title,
            GameItem = gameItem,
            IsNewRow = isNew,
            ChangeCount = isNew || item is null ? 0 : CountChanges(currentFile, rowName, item),
        };
    }

    /// <summary>Values the row changes compared with the game's own row.</summary>
    private int CountChanges(string currentFile, string rowName, ExmodFileItem item) =>
        item.Fields.Sum(kv => ChangedValues(kv.Key, kv.Value, BaseValueFor(currentFile, rowName, kv.Key, item)));

    /// <summary>Refreshes one list row's badge after its fields changed.</summary>
    private void Recount(EditorItemViewModel row)
    {
        if (!row.IsNewRow && FindItem(row.CurrentFile, row.ItemName) is { } item)
        {
            row.ChangeCount = CountChanges(row.CurrentFile, row.ItemName, item);
        }
    }

    /// <summary>
    /// The game's own value for a field: read from the game's row (a field the row leaves out has
    /// the table's default). Not from the diff cache, which only holds fields that differ — so an
    /// unchanged field there looked "changed" and an edited one lost its game value on reselect.
    /// Without the table (no game data), the old diff-or-current fallback.
    /// </summary>
    private JsonNode? BaseValueFor(string currentFile, string rowName, string field, ExmodFileItem item)
    {
        if (!HasGameTable(currentFile))
        {
            return _baseValuesByKey.TryGetValue((currentFile, rowName, field), out var cached) ? cached : item.Fields.GetValueOrDefault(field);
        }

        return GameTables.Row(currentFile, rowName) is { } row ? row[field] ?? GameTables.Defaults(currentFile)?[field] : null;
    }

    private bool HasGameTable(string currentFile) =>
        !_gameDataMissing && !string.Equals(currentFile, "EndOfMod", StringComparison.OrdinalIgnoreCase) && Directory.Exists(_dataFolder) && GameTables.Rows(currentFile) is not null;

    private readonly Dictionary<(string File, string Field), FieldChoices?> _fieldChoices = [];

    /// <summary>
    /// What a field may hold, from the game's own tables (null: free entry): a link field's target
    /// rows (the game's, plus rows this mod adds there, plus "None"), or the few values the game
    /// uses for a text field. Read once per table and field.
    /// </summary>
    private FieldChoices? ChoicesFor(string currentFile, string field, JsonNode? value)
    {
        if (_gameDataMissing || !HasGameTable(currentFile))
        {
            return null;
        }

        if (value is JsonObject { } link && link["RowName"] is JsonValue)
        {
            var table = link["DataTableName"] is JsonValue t && t.TryGetValue<string>(out var named) && named.Length > 0 ? named : GameTables.LinkTarget(currentFile, field);
            if (table is null || GameTables.CurrentFileFor(table) is not { } targetFile || GameTables.Rows(targetFile) is not { } rows)
            {
                return null;
            }

            var own = _package?.Rows.Where(r => string.Equals(r.CurrentFile, targetFile, StringComparison.OrdinalIgnoreCase)).SelectMany(r => r.FileItems.Select(i => i.Name)) ?? [];
            return new FieldChoices(table, [.. new[] { "None" }.Concat(rows.Select(kv => kv.Key)).Concat(own)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n == "None" ? 0 : 1).ThenBy(n => n, StringComparer.OrdinalIgnoreCase)]);
        }

        if (_fieldChoices.TryGetValue((currentFile, field), out var cached))
        {
            return cached;
        }

        // A text field the game only ever sets to a few identifier-like values is a choice.
        FieldChoices? choices = null;
        if (value is JsonValue v && v.TryGetValue<string>(out _))
        {
            var values = (GameTables.Rows(currentFile) ?? []).Select(kv => kv.Value?[field])
                .Append(GameTables.Defaults(currentFile)?[field])
                .OfType<JsonValue>().Select(x => x.TryGetValue<string>(out var s) ? s : null).OfType<string>()
                .Distinct(StringComparer.Ordinal).ToList();
            if (values.Count is > 1 and <= 40 && values.All(s => s.Length is > 0 and < 60 && !s.Contains('/') && !s.Contains(' ') && NsLocText.TextOf(s) is null))
            {
                choices = new FieldChoices(null, [.. values.OrderBy(s => s, StringComparer.OrdinalIgnoreCase)]);
            }
        }

        _fieldChoices[(currentFile, field)] = choices;
        return choices;
    }

    /// <summary>The game's stats for "Add a stat" on stats fields; null without game data.</summary>
    private IReadOnlyList<StatChoice>? StatChoicesForFields()
    {
        if (_statChoices is null && !_gameDataMissing)
        {
            if (!Directory.Exists(_dataFolder))
            {
                _gameDataMissing = true;
                return null;
            }

            _statChoices = GameItemEditor.StatChoices(GameTables);
        }

        return _statChoices;
    }

    private GameItemNames? ItemNames()
    {
        if (_itemNames is null && !_gameDataMissing)
        {
            if (!Directory.Exists(_dataFolder) || GameTables.CurrentFileFor("D_ItemsStatic") is null)
            {
                _gameDataMissing = true;
                return null;
            }

            _itemNames = new GameItemNames(GameTables);
        }

        return _itemNames;
    }

    /// <summary>The selected row's game item, on the Editor page (where its other parts are too).</summary>
    [RelayCommand]
    private void OpenInGameEditor()
    {
        if (SelectedItem?.GameItem is not { } gameItem)
        {
            StatusMessage = SelectedItem is null ? "Select an item first." : "No single game item uses this row (a recipe, or a row many items share), so there's nothing to open in Editor.";
            return;
        }

        WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("editor"));
        WeakReferenceMessenger.Default.Send(new OpenGameItemMessage(gameItem));
        Application.Current?.MainWindow?.Activate();
    }

    /// <summary>Values a field changes: one per stat for a stats list (added, changed or left out), else one if it differs at all.</summary>
    private static int ChangedValues(string field, JsonNode? value, JsonNode? game)
    {
        if (GameItemEditor.IsStatsObject(field, value) && value is JsonObject mine)
        {
            var theirs = game as JsonObject ?? [];
            return mine.Count(kv => !JsonNode.DeepEquals(kv.Value, theirs[kv.Key])) + theirs.Count(kv => !mine.ContainsKey(kv.Key));
        }

        return JsonNode.DeepEquals(value, game) ? 0 : 1;
    }

    /// <summary>The Editor page, for changing the game's own items the easy way.</summary>
    [RelayCommand]
    private void GoToGameEditor()
    {
        WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("editor"));
        if (SelectedItem?.GameItem is { } gameItem)
        {
            WeakReferenceMessenger.Default.Send(new OpenGameItemMessage(gameItem));
        }

        Application.Current?.MainWindow?.Activate();
    }

    private static string Spaced(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ").Replace('_', ' ');
}
