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
    private EditorItemViewModel MakeItem(string currentFile, string rowName)
    {
        var table = GameTableIndex.TableNameOf(currentFile);
        var names = ItemNames();
        var gameItem = names?.ItemFor(currentFile, rowName);
        var title = (gameItem is null ? null : names!.DisplayName(gameItem)) ?? rowName;
        var item = FindItem(currentFile, rowName);
        var changes = item?.Fields.Sum(kv => ChangedValues(kv.Key, kv.Value, _baseValuesByKey.TryGetValue((currentFile, rowName, kv.Key), out var baseValue) ? baseValue : null)) ?? 0;
        return new EditorItemViewModel(currentFile, rowName)
        {
            Group = GroupNames.TryGetValue(table, out var g) ? g : (table.StartsWith("D_", StringComparison.Ordinal) ? Spaced(table[2..]) : table),
            Title = title,
            GameItem = gameItem,
            ChangeCount = changes,
        };
    }

    /// <summary>The game's stats for "Add a stat" on stats fields; null without game data.</summary>
    private IReadOnlyList<StatChoice>? StatChoicesForFields()
    {
        if (_statChoices is null && !_gameDataMissing && Directory.Exists(_dataFolder))
        {
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

    private static string Spaced(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z0-9])(?=[A-Z])", " ").Replace('_', ' ');
}
