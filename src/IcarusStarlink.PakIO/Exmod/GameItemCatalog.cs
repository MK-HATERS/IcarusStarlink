using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>A game row by file and name, compared ignoring case (row names are FNames).</summary>
public readonly record struct GameRowRef(string File, string Row)
{
    public bool Equals(GameRowRef other) =>
        string.Equals(File, other.File, StringComparison.OrdinalIgnoreCase) && string.Equals(Row, other.Row, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(File), StringComparer.OrdinalIgnoreCase.GetHashCode(Row));
}

/// <summary>
/// One game item for the Editor page's list: its names, icon, category, what it has (durability,
/// storage, a recipe…), where it's crafted, and every row the Editor would show for it.
/// </summary>
/// <param name="Has">GameItemCatalog.HasFilters keys: the D_ItemsStatic link fields it uses, plus Recipe and Workshop.</param>
/// <param name="Benches">D_RecipeSets rows of every recipe that makes it.</param>
/// <param name="Rows">Its D_ItemsStatic row, the rows that row links to, its recipes and workshop row: a mod touching any of them changes this item.</param>
public sealed record GameItemInfo(
    ItemChoice Item, string? IconPath, string Category, string? Description,
    IReadOnlySet<string> Has, IReadOnlyList<string> Benches, IReadOnlyList<GameRowRef> Rows)
{
    public string Name => Item.Name;
}

/// <summary>
/// The game's items, sorted into the categories the game itself files their icons under
/// (".../Item_Icons/Armour/ITEM_Envirosuit_1": 3,036 of 3,090 items), for the Editor
/// page's list and filters.
/// </summary>
public static class GameItemCatalog
{
    private const string ItemsStatic = "Items-D_ItemsStatic.json";
    private const string ItemableFile = "Traits-D_Itemable.json";
    private const string Recipes = "Crafting-D_ProcessorRecipes.json";
    private const string RecipeSets = "Crafting-D_RecipeSets.json";
    private const string WorkshopItems = "MetaWorkshop-D_WorkshopItems.json";
    private const string ModifierStates = "Modifiers-D_ModifierStates.json";

    public const string RecipeKey = "Recipe";
    public const string WorkshopKey = "Workshop";
    public const string OtherCategory = "Other";

    /// <summary>Icon folder → category. Several small folders join a bigger one (Voxels are the ores: Stone, Copper_Ore).</summary>
    private static readonly Dictionary<string, string> CategoryByFolder = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Armour"] = "Armour",
        ["Weapons"] = "Weapons",
        ["LegendaryWeapons"] = "Weapons",
        ["Projectiles"] = "Ammo",
        ["Attachments"] = "Attachments",
        ["Tools"] = "Tools",
        ["Fish"] = "Fish & fishing",
        ["Fishing"] = "Fish & fishing",
        ["Consumeables"] = "Consumables",
        ["Consumables"] = "Consumables",
        ["Carcasses"] = "Carcasses",
        ["Resources"] = "Resources",
        ["ResourcePack"] = "Resources",
        ["Voxels"] = "Resources",
        ["Seeds"] = "Resources",
        ["Deployables"] = "Deployables",
        ["Buildables"] = "Building pieces",
        ["UseableItems"] = "Usable items",
        ["Bags"] = "Usable items",
        ["Modules"] = "Modules",
        ["Vehicles"] = "Vehicles",
        ["Dropship"] = "Vehicles",
        ["Props"] = "Decorations",
        ["QuestItems"] = "Quest items",
    };

    /// <summary>The order categories are offered in: gear first, then what's eaten or gathered, then what's placed; Other last.</summary>
    public static readonly IReadOnlyList<string> CategoryOrder =
    [
        "Armour", "Weapons", "Ammo", "Attachments", "Tools", "Fish & fishing", "Consumables", "Carcasses", "Resources",
        "Deployables", "Building pieces", "Usable items", "Modules", "Vehicles", "Decorations", "Quest items", OtherCategory,
    ];

    /// <summary>"Has…" filters: a D_ItemsStatic link field (the Editor shows a card for each), or a recipe / workshop price.</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> HasFilters =
    [
        (RecipeKey, "A crafting recipe"),
        (WorkshopKey, "A workshop price"),
        ("Durable", "Durability"),
        ("Inventory", "Storage slots"),
        ("Consumable", "Eaten or used (food, medicine)"),
        ("Armour", "Armour stats"),
        ("Equippable", "Bonuses when equipped"),
        ("Decayable", "Decay or spoiling"),
        ("Processing", "Processing (a bench or furnace)"),
        ("Generator", "Makes power or water"),
        ("Fillable", "Filling (water, oxygen, fuel)"),
        ("Thermal", "Heat"),
        ("FirearmData", "Firearm stats"),
    ];

    /// <summary>The category an icon path is filed under; Other for no icon or one outside Item_Icons.</summary>
    public static string CategoryOf(string? iconPath)
    {
        const string marker = "/Item_Icons/";
        var at = iconPath?.IndexOf(marker, StringComparison.OrdinalIgnoreCase) ?? -1;
        if (at < 0)
        {
            return OtherCategory;
        }

        var rest = iconPath![(at + marker.Length)..];
        var slash = rest.IndexOf('/');
        return slash > 0 && CategoryByFolder.TryGetValue(rest[..slash], out var category) ? category : OtherCategory;
    }

    /// <summary>A bench's in-game name ("Crafting_Bench" → "Crafting Bench"; "Character" → "By hand (Character)").</summary>
    public static string BenchName(GameTableIndex game, string bench)
    {
        var name = Text(game.Row(RecipeSets, bench)?["RecipeSetName"]) ?? GameItemEditor.Humanize(bench);
        return string.Equals(bench, "Character", StringComparison.OrdinalIgnoreCase) ? $"By hand ({name})" : name;
    }

    /// <summary>
    /// The rows a link can point at, for the Editor's drop-downs: "None" (nothing) first, then each
    /// row by its in-game name where it has one (an item template by its item's name, a bench by
    /// its own), else its row name.
    /// </summary>
    public static IReadOnlyList<(string Name, string Display)> LinkChoices(GameTableIndex game, string table)
    {
        if (game.CurrentFileFor(table) is not { } file)
        {
            return [];
        }

        var choices = new List<(string Name, string Display)>();
        foreach (var (name, node) in game.Rows(file) ?? [])
        {
            var title = table switch
            {
                "D_RecipeSets" => BenchName(game, name),
                "D_ItemTemplate" => ItemTitle(game, RowNameOf(node?["ItemStaticData"]) ?? name),
                "D_ItemsStatic" => ItemTitle(game, name),
                _ => null,
            };
            choices.Add((name, title is { Length: > 0 } t && t != name ? $"{t}  ({name})" : name));
        }

        return [("None", "(nothing)"), .. choices.OrderBy(c => c.Display, StringComparer.OrdinalIgnoreCase)];
    }

    private static string? ItemTitle(GameTableIndex game, string itemName) =>
        RowNameOf(game.Row(ItemsStatic, itemName)?["Itemable"]) is { } itemable ? Text(game.Row(ItemableFile, itemable)?["DisplayName"]) : null;

    /// <summary>Every D_ItemsStatic item, sorted by row name, with what the list and its filters need.</summary>
    public static IReadOnlyList<GameItemInfo> List(GameTableIndex game)
    {
        // Recipe outputs and workshop items name the item's D_ItemTemplate row: index them by that name once.
        var recipesByProduct = new Dictionary<string, List<(string Recipe, IReadOnlyList<string> Benches)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (recipe, node) in game.Rows(Recipes) ?? [])
        {
            if (node?["Outputs"] is not JsonArray outputs)
            {
                continue;
            }

            IReadOnlyList<string> benches = node["RecipeSets"] is JsonArray sets
                ? [.. sets.Select(s => RowNameOf(s)).OfType<string>()]
                : [];
            foreach (var product in outputs.Select(o => RowNameOf(o?["Element"])).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!recipesByProduct.TryGetValue(product, out var list))
                {
                    recipesByProduct[product] = list = [];
                }

                list.Add((recipe, benches));
            }
        }

        var workshopByProduct = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (workshop, node) in game.Rows(WorkshopItems) ?? [])
        {
            if (RowNameOf(node?["Item"]) is { } product)
            {
                workshopByProduct.TryAdd(product, workshop);
            }
        }

        var fileByField = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? FileOf(string field)
        {
            if (!fileByField.TryGetValue(field, out var file))
            {
                fileByField[field] = file = game.LinkTarget(ItemsStatic, field) is { } table ? game.CurrentFileFor(table) : null;
            }

            return file;
        }

        var items = new List<GameItemInfo>();
        foreach (var (name, node) in game.Rows(ItemsStatic) ?? [])
        {
            if (node is not JsonObject row)
            {
                continue;
            }

            var has = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new List<GameRowRef> { new(ItemsStatic, name) };
            JsonObject? itemable = null;
            foreach (var (field, value) in row)
            {
                if (RowNameOf(value) is not { } link || FileOf(field) is not { } file)
                {
                    continue;
                }

                has.Add(field);
                rows.Add(new GameRowRef(file, link));
                if (field.Equals("Itemable", StringComparison.OrdinalIgnoreCase))
                {
                    itemable = game.Row(file, link);
                }
                else if (field.Equals("Consumable", StringComparison.OrdinalIgnoreCase)
                    && RowNameOf(game.Row(file, link)?["Modifier"]?["Modifier"]) is { } buff)
                {
                    rows.Add(new GameRowRef(ModifierStates, buff)); // the Editor's "Its buff" card
                }
                else if (field.Equals("Inventory", StringComparison.OrdinalIgnoreCase) && game.Row(file, link)?["Inventories"] is JsonArray storages)
                {
                    // The Editor's "Storage" cards: the D_InventoryInfo rows the inventory row lists.
                    foreach (var storage in storages.OfType<JsonObject>())
                    {
                        if (RowNameOf(storage) is { } info && game.CurrentFileFor(storage["DataTableName"]?.GetValue<string>() ?? "D_InventoryInfo") is { } infoFile)
                        {
                            rows.Add(new GameRowRef(infoFile, info));
                        }
                    }
                }
            }

            var benches = new List<string>();
            foreach (var product in game.ProductNamesFor(name))
            {
                foreach (var (recipe, recipeBenches) in recipesByProduct.GetValueOrDefault(product) ?? [])
                {
                    has.Add(RecipeKey);
                    rows.Add(new GameRowRef(Recipes, recipe));
                    benches.AddRange(recipeBenches);
                }

                if (workshopByProduct.TryGetValue(product, out var workshop))
                {
                    has.Add(WorkshopKey);
                    rows.Add(new GameRowRef(WorkshopItems, workshop));
                }
            }

            var iconPath = itemable?["Icon"] is JsonValue icon && icon.TryGetValue<string>(out var path) && path is { Length: > 0 } and not "None" ? path : null;
            items.Add(new GameItemInfo(
                new ItemChoice(name, Text(itemable?["DisplayName"])), iconPath, CategoryOf(iconPath), Text(itemable?["Description"]),
                has, [.. benches.Distinct(StringComparer.OrdinalIgnoreCase)], [.. rows.Distinct()]));
        }

        return [.. items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The items a mod changes: those with any of their rows (GameItemInfo.Rows) in the mod, or their own copy of one (re-pointed in D_ItemsStatic, which is one of their rows too).</summary>
    public static IReadOnlySet<string> ItemsChangedBy(IEnumerable<GameItemInfo> items, ExmodPackage? mod)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (mod is null)
        {
            return changed;
        }

        var modRows = mod.Rows.SelectMany(r => r.FileItems.Select(i => new GameRowRef(r.CurrentFile, i.Name))).ToHashSet();
        if (modRows.Count == 0)
        {
            return changed;
        }

        foreach (var item in items)
        {
            if (item.Rows.Any(modRows.Contains))
            {
                changed.Add(item.Name);
            }
        }

        return changed;
    }

    private static string? RowNameOf(JsonNode? link) =>
        (link as JsonObject)?["RowName"] is JsonValue v && v.TryGetValue<string>(out var s) && s is { Length: > 0 } and not "None" ? s : null;

    private static string? Text(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? NsLocText.TextOf(s) : null;
}
