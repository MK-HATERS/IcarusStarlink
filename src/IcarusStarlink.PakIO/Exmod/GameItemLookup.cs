using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// What GameItemEditor.Describe asks about the whole game for one item: how many items share a
/// row, how many get a buff, which recipes make it, its workshop row. Each was a scan of a whole
/// table per item (about 18 ms an item; 812 deployables took 14.5 s for a bulk edit); here each
/// table is indexed once, on first use, for this game data with this mod applied. Only valid
/// while the mod is unchanged: Apply builds a new one after each change it makes.
/// </summary>
public sealed class GameItemLookup(GameTableIndex game, ExmodPackage? mod)
{
    private const string ItemsStatic = "Items-D_ItemsStatic.json";
    private const string Recipes = "Crafting-D_ProcessorRecipes.json";
    private const string WorkshopItems = "MetaWorkshop-D_WorkshopItems.json";

    private readonly Dictionary<string, Dictionary<string, int>> _usersByField = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _buffByConsumable = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int>? _buffUsers;
    private Dictionary<string, List<string>>? _recipesByProduct;
    private Dictionary<string, int>? _recipeOrder;
    private Dictionary<string, string>? _workshopByProduct;

    public GameTableIndex Game { get; } = game;

    public ExmodPackage? Mod { get; } = mod;

    /// <summary>How many items (with the mod's changes) link to this row through this D_ItemsStatic field.</summary>
    public int UsersOf(string field, string rowName)
    {
        if (!_usersByField.TryGetValue(field, out var counts))
        {
            counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var link in EffectiveLinks(field))
            {
                if (link is not null)
                {
                    counts[link] = counts.GetValueOrDefault(link) + 1;
                }
            }

            _usersByField[field] = counts;
        }

        return counts.GetValueOrDefault(rowName);
    }

    /// <summary>How many items get this buff, through whichever consumable rows carry it.</summary>
    public int BuffUsers(string consumableFile, string buff)
    {
        if (_buffUsers is null)
        {
            _buffUsers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in EffectiveLinks("Consumable"))
            {
                if (row is not null && BuffOf(consumableFile, row) is { } given)
                {
                    _buffUsers[given] = _buffUsers.GetValueOrDefault(given) + 1;
                }
            }
        }

        return _buffUsers.GetValueOrDefault(buff);
    }

    /// <summary>The buff a consumable row gives (with the mod's changes); null for none.</summary>
    public string? BuffOf(string consumableFile, string consumableRow)
    {
        if (!_buffByConsumable.TryGetValue(consumableRow, out var buff))
        {
            buff = GameItemEditor.EffectiveRow(Game, Mod, consumableFile, consumableRow)?["Modifier"]?["Modifier"]?["RowName"]?.GetValue<string>() is { } b && b != "None" ? b : null;
            _buffByConsumable[consumableRow] = buff;
        }

        return buff;
    }

    /// <summary>The game's recipes that make any of these product names (an item's D_ItemTemplate names), in table order.</summary>
    public IReadOnlyList<string> RecipesMaking(IReadOnlySet<string> products)
    {
        if (_recipesByProduct is null)
        {
            _recipesByProduct = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _recipeOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (recipe, node) in Game.Rows(Recipes) ?? [])
            {
                _recipeOrder.TryAdd(recipe, _recipeOrder.Count);
                if (node?["Outputs"] is not JsonArray outputs)
                {
                    continue;
                }

                foreach (var product in outputs.Select(o => RowNameOf(o?["Element"])).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!_recipesByProduct.TryGetValue(product, out var list))
                    {
                        _recipesByProduct[product] = list = [];
                    }

                    list.Add(recipe);
                }
            }
        }

        return [.. products.SelectMany(p => _recipesByProduct.GetValueOrDefault(p) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(r => _recipeOrder!.GetValueOrDefault(r))];
    }

    /// <summary>The first workshop row (table order) handing out any of these product names; null for none.</summary>
    public string? WorkshopFor(IReadOnlySet<string> products)
    {
        if (_workshopByProduct is null)
        {
            _workshopByProduct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (workshop, node) in Game.Rows(WorkshopItems) ?? [])
            {
                if (RowNameOf(node?["Item"]) is { } product)
                {
                    _workshopByProduct.TryAdd(product, workshop);
                }
            }
        }

        var found = products.Select(p => _workshopByProduct.GetValueOrDefault(p)).OfType<string>().ToList();
        if (found.Count <= 1)
        {
            return found.FirstOrDefault();
        }

        var order = (Game.Rows(WorkshopItems) ?? []).Select(kv => kv.Key).ToList();
        return found.OrderBy(w => order.FindIndex(k => string.Equals(k, w, StringComparison.OrdinalIgnoreCase))).First();
    }

    /// <summary>Every item's link for a field, with the mod's changes on top (including items only the mod has).</summary>
    internal IEnumerable<string?> EffectiveLinks(string field)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Mod?.Rows.Where(r => string.Equals(r.CurrentFile, ItemsStatic, StringComparison.OrdinalIgnoreCase)).SelectMany(r => r.FileItems) ?? [])
        {
            if (item.Fields.TryGetValue(field, out var link))
            {
                overrides[item.Name] = link?["RowName"]?.GetValue<string>();
            }
        }

        foreach (var (name, node) in Game.Rows(ItemsStatic) ?? [])
        {
            yield return overrides.Remove(name, out var overridden) ? overridden : node?[field]?["RowName"]?.GetValue<string>();
        }

        foreach (var remaining in overrides.Values)
        {
            yield return remaining;
        }
    }

    private static string? RowNameOf(JsonNode? link) =>
        (link as JsonObject)?["RowName"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
