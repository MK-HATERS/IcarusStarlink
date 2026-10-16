using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// "Which item is this row?" in the game's own words: D_Armour › Undersuit_Larkwell_Alpha is the
/// Larkwell envirosuit's armour. An item's rows are linked from its D_ItemsStatic row, so the
/// reverse lookup is built once from D_ItemsStatic's links (table from its Defaults).
/// </summary>
public sealed class GameItemNames(GameTableIndex game)
{
    private const string ItemsStatic = "Items-D_ItemsStatic.json";
    private const string ItemableFile = "Traits-D_Itemable.json";
    private Dictionary<(string Table, string Row), string?>? _itemByRow;

    /// <summary>The game item (its D_ItemsStatic name) that uses this row; null when none or several do (a recipe, a shared decay rule).</summary>
    public string? ItemFor(string currentFile, string rowName)
    {
        var table = GameTableIndex.TableNameOf(currentFile);
        if (string.Equals(table, "D_ItemsStatic", StringComparison.OrdinalIgnoreCase) || string.Equals(table, "D_ItemTemplate", StringComparison.OrdinalIgnoreCase))
        {
            return game.GameRowName(ItemsStatic, rowName);
        }

        _itemByRow ??= Build();
        return _itemByRow.GetValueOrDefault((table.ToUpperInvariant(), rowName.ToUpperInvariant()));
    }

    public string? DisplayName(string itemName) =>
        game.Row(ItemsStatic, itemName)?["Itemable"]?["RowName"]?.GetValue<string>() is { } itemable
            ? NsLocText.TextOf(game.Row(ItemableFile, itemable)?["DisplayName"]?.GetValue<string>())?.Trim() is { Length: > 0 } name ? name : null
            : null;

    private Dictionary<(string, string), string?> Build()
    {
        var map = new Dictionary<(string, string), string?>();
        foreach (var (item, node) in game.Rows(ItemsStatic) ?? [])
        {
            if (node is not JsonObject row)
            {
                continue;
            }

            foreach (var (field, value) in row)
            {
                if (value is JsonObject link && link["RowName"]?.GetValue<string>() is { } target && target != "None"
                    && game.LinkTarget(ItemsStatic, field) is { } table)
                {
                    // A row several items share (a decay rule) belongs to none of them by name.
                    var key = (table.ToUpperInvariant(), target.ToUpperInvariant());
                    map[key] = map.TryGetValue(key, out var existing) && existing != item ? null : item;
                }
            }
        }

        return map;
    }
}
