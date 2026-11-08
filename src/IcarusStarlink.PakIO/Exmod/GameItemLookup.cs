using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// What GameItemEditor.Describe asks about the whole game for one item: how many items share a
/// row, how many get a buff, which recipes make it, its workshop row. Each was a scan of a whole
/// table per item (about 18 ms an item; 812 deployables took 14.5 s for a bulk edit); here each
/// table is indexed once, on first use, for this game data with this mod applied. The counts read
/// the mod as it was when first asked: GameItemEditor.Apply calls ModChanged after a change of its
/// own moves what they read, so one lookup can serve every Apply of a save to the same mod.
/// </summary>
public sealed class GameItemLookup(GameTableIndex game, ExmodPackage? mod)
{
    private const string ItemsStatic = "Items-D_ItemsStatic.json";
    private const string Recipes = "Crafting-D_ProcessorRecipes.json";
    private const string WorkshopItems = "MetaWorkshop-D_WorkshopItems.json";

    private readonly Dictionary<string, Dictionary<string, int>> _usersByField = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _buffByConsumable = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int>? _buffUsers;
    private Dictionary<string, int>? _infoUsers;
    private Dictionary<string, List<string>>? _recipesByProduct;
    private Dictionary<string, int>? _recipeOrder;
    private Dictionary<string, string>? _workshopByProduct;
    private ScaleOwnership? _ruleOwnership;
    private bool _ruleOwnershipBuilt;

    public GameTableIndex Game { get; } = game;

    public ExmodPackage? Mod { get; } = mod;

    /// <summary>What the mod's enabled Scale rules write (ScaleRules.Ownership of Mod.ScaleRules; rule numbers are positions in that list); null when it has none. Built on first use.</summary>
    internal ScaleOwnership? RuleOwnership
    {
        get
        {
            if (!_ruleOwnershipBuilt)
            {
                _ruleOwnershipBuilt = true;
                _ruleOwnership = Mod is { } m && m.ScaleRules.Any(r => r.Enabled) ? ScaleRules.Ownership(m.ScaleRules, Game.DataFolder, Game) : null;
            }

            return _ruleOwnership;
        }
    }

    /// <summary>
    /// Forgets the counts that read the mod (users of a row, buffs, storages), after a change to the
    /// mod that moves them. What only reads the game (recipes, workshop rows) and the rule ownership
    /// stay: own copies don't change what the rules write, and a rule exception only takes out a row
    /// already handled.
    /// </summary>
    internal void ModChanged()
    {
        _usersByField.Clear();
        _buffByConsumable.Clear();
        _buffUsers = null;
        _infoUsers = null;
    }

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

    /// <summary>How many items (with the mod's changes) have this storage (D_InventoryInfo row) in their inventory row's list; an item counts once.</summary>
    public int InventoryInfoUsers(string inventoryFile, string info)
    {
        if (_infoUsers is null)
        {
            _infoUsers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var infosByRow = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in EffectiveLinks("Inventory"))
            {
                if (row is null || RowLinks.IsNone(row))
                {
                    continue;
                }

                if (!infosByRow.TryGetValue(row, out var infos))
                {
                    infosByRow[row] = infos = [.. ((GameItemEditor.EffectiveRow(Game, Mod, inventoryFile, row)?["Inventories"] as JsonArray) ?? [])
                        .Select(e => RowLinks.RowNameOf(e)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
                }

                foreach (var used in infos)
                {
                    _infoUsers[used] = _infoUsers.GetValueOrDefault(used) + 1;
                }
            }
        }

        return _infoUsers.GetValueOrDefault(info);
    }

    /// <summary>The buff a consumable row gives (with the mod's changes); null for none.</summary>
    public string? BuffOf(string consumableFile, string consumableRow)
    {
        if (!_buffByConsumable.TryGetValue(consumableRow, out var buff))
        {
            buff = RowLinks.LinkedRow((GameItemEditor.EffectiveRow(Game, Mod, consumableFile, consumableRow)?["Modifier"] as JsonObject)?["Modifier"]);
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

                foreach (var product in outputs.Select(o => RowLinks.RowNameOf((o as JsonObject)?["Element"])).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
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
                if (RowLinks.RowNameOf((node as JsonObject)?["Item"]) is { } product)
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
                overrides[item.Name] = RowLinks.RowNameOf(link);
            }
        }

        foreach (var (name, node) in Game.Rows(ItemsStatic) ?? [])
        {
            yield return overrides.Remove(name, out var overridden) ? overridden : RowLinks.RowNameOf((node as JsonObject)?[field]);
        }

        foreach (var remaining in overrides.Values)
        {
            yield return remaining;
        }
    }
}
