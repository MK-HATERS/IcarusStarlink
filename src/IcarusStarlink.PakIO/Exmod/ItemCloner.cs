using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>One link of an item (D_ItemsStatic field → row in another table) and whether a new item usually wants its own copy of it.</summary>
public sealed record ItemLink(string Field, string CurrentFile, string RowName, bool SuggestOwnCopy);

/// <summary>What a game item is made of, for the "New item from an existing one" wizard.</summary>
public sealed record ItemCloneSource(
    string ItemName, string? DisplayName, IReadOnlyList<ItemLink> Links, bool HasTemplate,
    string? RecipeName, IReadOnlyList<string> RecipeBenches, string? WorkshopItemName, int? ResearchCredits, int? ReplicationCredits, string? WorkshopTree,
    string? BuffName = null, bool RecipeHasUnlock = false, string? TemplateName = null);

/// <summary>A game item for the wizard's picker.</summary>
public sealed record ItemChoice(string Name, string? DisplayName)
{
    public string Display => DisplayName is { Length: > 0 } d && d != Name ? $"{d}  ({Name})" : Name;

    /// <summary>The in-game name when there is one, else the row name (a list's first line).</summary>
    public string Title => DisplayName is { Length: > 0 } d ? d : Name;

    /// <summary>The row name under Title, or null when Title already is the row name.</summary>
    public string? Subtitle => Title != Name ? Name : null;
}

/// <summary>How an item template starts the new-item wizard (ModTemplateContent.ItemPresetFor). A null source lets the user pick.</summary>
public sealed record NewItemPreset(string? SourceItem, ObtainMethod Obtain, string? WorkshopTree = null, bool CopyBuff = false);

public enum ObtainMethod
{
    /// <summary>Just the item; the author wires it up later (or another mod does).</summary>
    None,

    /// <summary>A crafting recipe at a bench.</summary>
    Recipe,

    /// <summary>A purchase in the orbital workshop (workshop item + workshop talent in a tree).</summary>
    Workshop,
}

public sealed class ItemCloneRequest
{
    public required string SourceItem { get; init; }
    public required string NewName { get; init; }
    public required string DisplayName { get; init; }
    public string? Description { get; init; }

    /// <summary>D_ItemsStatic link fields that get the new item's own copy of the linked row; the rest keep pointing at the game's. Itemable is always copied (it holds the new name).</summary>
    public IReadOnlySet<string> OwnCopyFields { get; init; } = new HashSet<string>();

    /// <summary>A consumable with its own Consumable copy: also give it its own buff (D_ModifierStates row), named after the item.</summary>
    public bool CopyBuff { get; init; }

    public ObtainMethod Obtain { get; init; }

    /// <summary>Recipe: the bench (D_RecipeSets row, e.g. "Crafting_Bench").</summary>
    public string RecipeBench { get; init; } = "Crafting_Bench";

    /// <summary>Recipe: unlock it with the source item's blueprint (true) or have it available from the start (false).</summary>
    public bool UnlockWithSource { get; init; }

    /// <summary>Workshop: an existing workshop tree to add to (e.g. "Workshop_Envirosuits") …</summary>
    public string? WorkshopTree { get; init; }

    /// <summary>… or the name of a new workshop tab of the mod's own (used when WorkshopTree is empty).</summary>
    public string? NewWorkshopTab { get; init; }

    public int ResearchCredits { get; init; } = 50;
    public int ReplicationCredits { get; init; } = 25;
}

/// <summary>
/// "New item from an existing one" — what every new-item mod checked does by hand (Bloodragon's
/// envirosuits, BetterSupplies' consumables, Teleporters): copy a game item's D_ItemsStatic row
/// and its D_ItemTemplate, make the item's own copies of the trait rows it customises (its
/// Itemable for the name/icon, its Armour/Consumable/… for stats), keep pointing at the game's
/// rows for the rest (mesh, sounds, behaviour), rewrite every link and localization key to the new
/// name, then make it obtainable: a recipe at a bench (recipes output the D_ItemTemplate row and
/// unlock through a D_Talents blueprint) or an orbital-workshop purchase (D_WorkshopItems row +
/// a workshop talent placed in a D_TalentTrees tree, optionally a new tab of the mod's own via
/// D_TalentArchetypes — exactly how Bloodragon added its "Bloodragon Innovations" tab).
/// </summary>
public static partial class ItemCloner
{
    private const string ItemsStatic = "Items-D_ItemsStatic.json";
    private const string ItemTemplate = "Items-D_ItemTemplate.json";
    private const string Recipes = "Crafting-D_ProcessorRecipes.json";
    private const string WorkshopItems = "MetaWorkshop-D_WorkshopItems.json";
    private const string Talents = "Talents-D_Talents.json";
    private const string TalentTrees = "Talents-D_TalentTrees.json";
    private const string TalentArchetypes = "Talents-D_TalentArchetypes.json";
    private const string RecipeSets = "Crafting-D_RecipeSets.json";
    private const string ModifierStates = "Modifiers-D_ModifierStates.json";

    /// <summary>Traits that hold an item's own numbers — a new item almost always wants its own copy.</summary>
    private static readonly HashSet<string> StatTraits = new(StringComparer.Ordinal)
    {
        "Itemable", "Armour", "Consumable", "Equippable", "Durable", "Generator", "Thermal", "Inventory", "Fillable", "Processing",
    };

    [GeneratedRegex("""NSLOCTEXT\("(?<table>[^"]*)",\s*"(?<key>[^"]*)",\s*"(?<text>(?:[^"\\]|\\.)*)"\)""")]
    private static partial Regex LocText();

    /// <summary>Every game item with its in-game name (from its Itemable row).</summary>
    public static IReadOnlyList<ItemChoice> ListItems(GameTableIndex game)
    {
        const string itemable = "Traits-D_Itemable.json";
        return [.. (game.Rows(ItemsStatic) ?? [])
            .Select(kv => new ItemChoice(kv.Key, kv.Value?["Itemable"]?["RowName"]?.GetValue<string>() is { } link ? LocalizedText(game.Row(itemable, link)?["DisplayName"]) : null))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Crafting benches (D_RecipeSets rows: Character = the inventory, Crafting_Bench, Machining_Bench, Kitchen_Stove …).</summary>
    public static IReadOnlyList<string> ListBenches(GameTableIndex game) =>
        [.. (game.Rows(RecipeSets)?.Select(kv => kv.Key) ?? []).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    /// <summary>Workshop trees (D_TalentTrees whose archetype's Model is "Workshop") — where a workshop item can be added.</summary>
    public static IReadOnlyList<string> ListWorkshopTrees(GameTableIndex game)
    {
        var archetypes = game.Rows(TalentArchetypes);
        return [.. (game.Rows(TalentTrees) ?? [])
            .Where(kv => kv.Value?["Archetype"]?["RowName"]?.GetValue<string>() is { } archetype
                && archetypes?[archetype]?["Model"]?["RowName"]?.GetValue<string>() == "Workshop")
            .Select(kv => kv.Key)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];
    }

    public static ItemCloneSource? Inspect(GameTableIndex game, string itemName)
    {
        if (game.Row(ItemsStatic, itemName) is not { } row)
        {
            return null;
        }

        var links = new List<ItemLink>();
        foreach (var (field, value) in row)
        {
            if (value is JsonObject link && link["RowName"]?.GetValue<string>() is { } target && target != "None"
                && game.LinkTarget(ItemsStatic, field) is { } table && game.CurrentFileFor(table) is { } file)
            {
                var suggest = StatTraits.Contains(field) || target.Contains(itemName, StringComparison.OrdinalIgnoreCase);
                links.Add(new ItemLink(field, file, target, suggest));
            }
        }

        var itemable = links.FirstOrDefault(l => l.Field == "Itemable");
        var displayName = itemable is null ? null : LocalizedText(game.Row(itemable.CurrentFile, itemable.RowName)?["DisplayName"]);

        // Recipes and workshop items hand out the item's D_ItemTemplate row, which is often named
        // differently (Kit_Stone_Furnace is made as "Stone_Furnace"): match through those names.
        var templates = game.TemplatesFor(itemName);
        var templateName = templates.FirstOrDefault(t => string.Equals(t, itemName, StringComparison.OrdinalIgnoreCase)) ?? templates.FirstOrDefault();
        var products = game.ProductNamesFor(itemName);
        var recipe = (game.Rows(Recipes) ?? []).FirstOrDefault(kv => OutputsItem(kv.Value, products));
        var benches = recipe.Value?["RecipeSets"] is JsonArray sets
            ? sets.Select(s => s?["RowName"]?.GetValue<string>()).OfType<string>().ToList()
            : [];

        var consumable = links.FirstOrDefault(l => l.Field == "Consumable");
        var buff = consumable is null ? null : game.Row(consumable.CurrentFile, consumable.RowName)?["Modifier"]?["Modifier"]?["RowName"]?.GetValue<string>();
        var unlock = recipe.Value?["Requirement"]?["RowName"]?.GetValue<string>();

        var workshop = (game.Rows(WorkshopItems) ?? []).FirstOrDefault(kv => RowNameOf(kv.Value?["Item"]) is { } item && products.Contains(item));
        var talent = workshop.Key is null ? default : (game.Rows(Talents) ?? []).FirstOrDefault(kv => string.Equals(RowNameOf(kv.Value?["ExtraData"]), workshop.Key, StringComparison.OrdinalIgnoreCase));

        return new ItemCloneSource(
            itemName, displayName, links, templateName is not null,
            recipe.Key, benches, workshop.Key,
            Credits(workshop.Value?["ResearchCost"]), Credits(workshop.Value?["ReplicationCost"]),
            talent.Value?["TalentTree"]?["RowName"]?.GetValue<string>(),
            buff is { Length: > 0 } and not "None" ? buff : null,
            unlock is { Length: > 0 } and not "None",
            templateName);
    }

    /// <summary>Every row the new item needs, as EXMOD rows (complete rows: they're new). Also used to know what to add to a mod.</summary>
    public static List<ExmodFileRow> Build(GameTableIndex game, ItemCloneRequest request, ExmodPackage? existingMod = null)
    {
        var source = Inspect(game, request.SourceItem) ?? throw new ArgumentException($"'{request.SourceItem}' isn't an item in the game data.");
        var newName = request.NewName.Trim();
        // Row names ignore case in game: "envirosuit" would collide with the game's "Envirosuit".
        if (game.HasRow(ItemsStatic, newName) || HasRow(existingMod, ItemsStatic, newName))
        {
            throw new ArgumentException($"An item called '{newName}' already exists — pick another name.");
        }

        var rows = new Dictionary<string, List<ExmodFileItem>>(StringComparer.OrdinalIgnoreCase);
        void Add(string file, string name, JsonObject fields)
        {
            if (!rows.TryGetValue(file, out var list))
            {
                list = [];
                rows[file] = list;
            }

            var item = new ExmodFileItem { Name = name };
            foreach (var (key, value) in fields)
            {
                if (key != "Name")
                {
                    item.Fields[key] = value?.DeepClone();
                }
            }

            list.Add(item);
        }

        // Itemable is always the item's own: it holds the name and description typed for it, which
        // would otherwise be silently dropped (the item would show the original's name in game).
        var ownCopyFields = new HashSet<string>(request.OwnCopyFields) { "Itemable" };

        // The buff is set on the Consumable row, so its own buff needs its own Consumable too.
        if (request.CopyBuff && source.BuffName is not null && source.Links.Any(l => l.Field == "Consumable") && !ownCopyFields.Contains("Consumable"))
        {
            throw new ArgumentException("Its own buff needs its own Consumable copy too (the buff is set on that row): tick Consumable, or untick the buff.");
        }

        // The item's own copies of the trait rows it customises.
        var staticRow = (JsonObject)game.Row(ItemsStatic, source.ItemName)!.DeepClone();
        foreach (var link in source.Links.Where(l => ownCopyFields.Contains(l.Field)))
        {
            var copyName = UniqueName(game, existingMod, link.CurrentFile, CopyName(link, source.ItemName, newName));
            var copy = (JsonObject)game.Row(link.CurrentFile, link.RowName)!.DeepClone();
            RewriteLocalization(copy, copyName, link.Field == "Itemable" ? ItemTexts(request) : null);
            if (link.Field == "Consumable" && request.CopyBuff && source.BuffName is { } buff && game.Row(ModifierStates, buff) is { } buffRow)
            {
                // The buff the item gives: its own copy so its stats can change without touching the game's food.
                var buffName = UniqueName(game, existingMod, ModifierStates, newName);
                var buffCopy = (JsonObject)buffRow.DeepClone();
                RewriteLocalization(buffCopy, buffName, new Dictionary<string, string?> { ["ModifierName"] = request.DisplayName });
                Add(ModifierStates, buffName, buffCopy);
                copy["Modifier"]!["Modifier"] = new JsonObject { ["RowName"] = buffName };
            }

            Add(link.CurrentFile, copyName, copy);
            staticRow[link.Field] = new JsonObject { ["RowName"] = copyName };
        }

        Add(ItemsStatic, newName, staticRow);

        // Recipes and workshop items hand out the D_ItemTemplate row, not the static one, so the new
        // item always gets one (named like it): a copy of the source's template (found through its
        // ItemStaticData, since many aren't named like their item), or a minimal one.
        var template = source.TemplateName is { } sourceTemplate && game.Row(ItemTemplate, sourceTemplate) is { } templateRow
            ? (JsonObject)templateRow.DeepClone()
            : [];
        ReplaceRowName(template, source.ItemName, newName);
        template["ItemStaticData"] = template["ItemStaticData"] is JsonObject staticLink
            ? new JsonObject(staticLink.Select(kv => KeyValuePair.Create(kv.Key, kv.Key == "RowName" ? JsonValue.Create(newName) : kv.Value?.DeepClone())))
            : new JsonObject { ["RowName"] = newName };
        Add(ItemTemplate, newName, template);

        switch (request.Obtain)
        {
            case ObtainMethod.Recipe:
                Add(Recipes, UniqueName(game, existingMod, Recipes, newName), BuildRecipe(game, source, request, newName));
                break;
            case ObtainMethod.Workshop:
                BuildWorkshop(game, existingMod, source, request, newName, rows, Add);
                break;
        }

        // New mod makes new things and never changes the game's own: a row named like one the game
        // already has would edit that row instead of adding one (the Editor page is for that).
        foreach (var (file, items) in rows)
        {
            if (items.FirstOrDefault(i => game.HasRow(file, i.Name)) is { } clash)
            {
                throw new ArgumentException($"'{clash.Name}' is already a row in the game's {GameTableIndex.TableNameOf(file)}, so adding it would change the game's own. Pick another name.");
            }
        }

        return [.. rows.Select(kv => new ExmodFileRow { CurrentFile = kv.Key, FileItems = kv.Value })];
    }

    /// <summary>Adds Build's rows to a mod (EndOfMod stays last).</summary>
    public static void AddToPackage(ExmodPackage package, IEnumerable<ExmodFileRow> newRows)
    {
        foreach (var newRow in newRows)
        {
            var row = package.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, newRow.CurrentFile, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                var end = package.Rows.FindIndex(r => ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile));
                package.Rows.Insert(end < 0 ? package.Rows.Count : end, newRow);
                continue;
            }

            row.FileItems.AddRange(newRow.FileItems);
        }
    }

    private static JsonObject BuildRecipe(GameTableIndex game, ItemCloneSource source, ItemCloneRequest request, string newName)
    {
        JsonObject recipe;
        if (source.RecipeName is { } sourceRecipe && game.Row(Recipes, sourceRecipe) is { } existing)
        {
            recipe = (JsonObject)existing.DeepClone();
            // The output names the source's template (maybe not its item name); the new item's own
            // template is called newName.
            foreach (var product in new[] { source.TemplateName, source.ItemName }.OfType<string>())
            {
                ReplaceRowName(recipe["Outputs"], product, newName);
            }
        }
        else
        {
            // No recipe to start from: one placeholder input the author changes in the editor.
            recipe = new JsonObject
            {
                ["Inputs"] = new JsonArray(new JsonObject
                {
                    ["Element"] = new JsonObject { ["RowName"] = "Fiber", ["DataTableName"] = "D_ItemsStatic" },
                    ["Count"] = 1,
                }),
                ["Outputs"] = new JsonArray(new JsonObject
                {
                    ["Element"] = new JsonObject { ["RowName"] = newName, ["DataTableName"] = "D_ItemTemplate" },
                    ["Count"] = 1,
                }),
            };
        }

        recipe["RecipeSets"] = new JsonArray(new JsonObject { ["RowName"] = request.RecipeBench, ["DataTableName"] = "D_RecipeSets" });
        // The Requirement is the blueprint talent that unlocks a recipe; "None" means known from the start.
        recipe["Requirement"] = new JsonObject
        {
            ["RowName"] = request.UnlockWithSource && source.RecipeName is { } r && game.Row(Recipes, r)?["Requirement"]?["RowName"]?.GetValue<string>() is { } unlock ? unlock : "None",
        };
        return recipe;
    }

    private static void BuildWorkshop(
        GameTableIndex game, ExmodPackage? existingMod, ItemCloneSource source, ItemCloneRequest request, string newName,
        Dictionary<string, List<ExmodFileItem>> rows, Action<string, string, JsonObject> add)
    {
        var workshopName = UniqueName(game, existingMod, WorkshopItems, "Meta_" + newName);
        add(WorkshopItems, workshopName, new JsonObject
        {
            ["Item"] = new JsonObject { ["RowName"] = newName },
            ["ResearchCost"] = Cost(request.ResearchCredits),
            ["ReplicationCost"] = Cost(request.ReplicationCredits),
        });

        string tree;
        if (!string.IsNullOrWhiteSpace(request.WorkshopTree))
        {
            tree = request.WorkshopTree.Trim();
        }
        else
        {
            // A tab of the mod's own: a tree and an archetype, modeled on the game's envirosuit tab.
            var tab = string.IsNullOrWhiteSpace(request.NewWorkshopTab) ? "Mod Items" : request.NewWorkshopTab.Trim();
            tree = "Workshop_Mod_" + Regex.Replace(tab, "[^A-Za-z0-9]+", "_").Trim('_');
            var alreadyAdding = rows.TryGetValue(TalentTrees, out var addedTrees) && addedTrees.Any(i => string.Equals(i.Name, tree, StringComparison.OrdinalIgnoreCase));
            if (!alreadyAdding && !game.HasRow(TalentTrees, tree) && !HasRow(existingMod, TalentTrees, tree))
            {
                var archetype = (JsonObject?)game.Row(TalentArchetypes, "Workshop_Envirosuits")?.DeepClone()
                    ?? new JsonObject { ["Model"] = new JsonObject { ["RowName"] = "Workshop" } };
                archetype["DisplayName"] = $"NSLOCTEXT(\"D_TalentArchetypes\", \"{tree}-DisplayName\", \"{Escape(tab)}\")";
                add(TalentArchetypes, tree, archetype);

                var treeRow = (JsonObject?)game.Row(TalentTrees, "Workshop_Envirosuits")?.DeepClone() ?? [];
                treeRow["Archetype"] = new JsonObject { ["RowName"] = tree };
                add(TalentTrees, tree, treeRow);
            }
        }

        // Next free column in the tree, after the game's and this mod's own talents.
        var positions = (game.Rows(Talents) ?? []).Select(kv => kv.Value as JsonObject)
            .Concat(existingMod?.Rows.Where(r => string.Equals(r.CurrentFile, Talents, StringComparison.OrdinalIgnoreCase)).SelectMany(r => r.FileItems).Select(i => new JsonObject(i.Fields.Select(f => KeyValuePair.Create(f.Key, f.Value?.DeepClone())))) ?? [])
            .Concat(rows.GetValueOrDefault(Talents)?.Select(i => new JsonObject(i.Fields.Select(f => KeyValuePair.Create(f.Key, f.Value?.DeepClone())))) ?? [])
            .Where(t => t?["TalentTree"]?["RowName"]?.GetValue<string>() == tree)
            .Select(t => (X: t!["Position"]?["X"]?.GetValue<double>() ?? 0, Y: t["Position"]?["Y"]?.GetValue<double>() ?? 0))
            .ToList();
        var x = positions.Count == 0 ? 500 : positions.Max(p => p.X) + 300;
        var y = positions.Count == 0 ? 500 : positions.Min(p => p.Y);

        add(Talents, UniqueName(game, existingMod, Talents, "Workshop_" + newName), new JsonObject
        {
            ["ExtraData"] = new JsonObject { ["RowName"] = workshopName, ["DataTableName"] = "D_WorkshopItems" },
            ["TalentTree"] = new JsonObject { ["RowName"] = tree },
            ["Position"] = new JsonObject { ["X"] = x, ["Y"] = y },
            ["Size"] = new JsonObject { ["X"] = 250, ["Y"] = 250 },
        });
    }

    private static JsonArray Cost(int credits) =>
        new(new JsonObject
        {
            ["Meta"] = new JsonObject { ["RowName"] = "Credits", ["DataTableName"] = "D_MetaCurrency" },
            ["Amount"] = credits,
        });

    private static int? Credits(JsonNode? costs) =>
        costs is JsonArray list && list.FirstOrDefault(c => c?["Meta"]?["RowName"]?.GetValue<string>() == "Credits") is { } credits
            && credits["Amount"]?.GetValue<int>() is { } amount ? amount : null;

    private static bool OutputsItem(JsonNode? recipe, IReadOnlySet<string> productNames) =>
        recipe?["Outputs"] is JsonArray outputs && outputs.Any(o => RowNameOf((o as JsonObject)?["Element"]) is { } name && productNames.Contains(name));

    private static string? RowNameOf(JsonNode? link) =>
        (link as JsonObject)?["RowName"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>Item_Envirosuit → Item_Dragon_Suit; a shared row ("IO_Undersuit") → the item's own name (Itemable keeps the game's "Item_" convention).</summary>
    private static string CopyName(ItemLink link, string sourceItem, string newName) =>
        link.RowName.Contains(sourceItem, StringComparison.OrdinalIgnoreCase)
            ? Regex.Replace(link.RowName, Regex.Escape(sourceItem), newName.Replace("$", "$$"), RegexOptions.IgnoreCase)
            : link.Field == "Itemable" ? "Item_" + newName : newName;

    private static string UniqueName(GameTableIndex game, ExmodPackage? mod, string file, string wanted)
    {
        var name = wanted;
        for (var i = 2; game.HasRow(file, name) || HasRow(mod, file, name); i++)
        {
            name = $"{wanted}_{i}";
        }

        return name;
    }

    private static bool HasRow(ExmodPackage? mod, string file, string name) =>
        mod?.Rows.Any(r => string.Equals(r.CurrentFile, file, StringComparison.OrdinalIgnoreCase)
            && r.FileItems.Any(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))) == true;

    /// <summary>Every {"RowName": old} under node becomes new (the template's own item reference, a recipe's output).</summary>
    private static void ReplaceRowName(JsonNode? node, string oldName, string newName)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["RowName"] is JsonValue v && v.TryGetValue<string>(out var current) && string.Equals(current, oldName, StringComparison.OrdinalIgnoreCase))
                {
                    obj["RowName"] = newName;
                }

                foreach (var (_, child) in obj.ToList())
                {
                    ReplaceRowName(child, oldName, newName);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    ReplaceRowName(child, oldName, newName);
                }

                break;
        }
    }

    /// <summary>
    /// NSLOCTEXT keys follow "&lt;Row&gt;-&lt;Field&gt;" and must be unique per row, so a copied row gets keys
    /// under its new name; on the item's Itemable the display name and description become the new item's.
    /// </summary>
    private static Dictionary<string, string?> ItemTexts(ItemCloneRequest request) =>
        new() { ["DisplayName"] = request.DisplayName, ["Description"] = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description };

    private static void RewriteLocalization(JsonObject row, string newRow, IReadOnlyDictionary<string, string?>? texts)
    {
        foreach (var (field, value) in row.ToList())
        {
            if (value is not JsonValue text || !text.TryGetValue<string>(out var s) || LocText().Match(s) is not { Success: true } m)
            {
                continue;
            }

            var key = m.Groups["key"].Value;
            var dash = key.LastIndexOf('-');
            var newKey = dash > 0 ? $"{newRow}-{key[(dash + 1)..]}" : $"{newRow}-{field}";
            var newText = texts?.GetValueOrDefault(field) is { } replacement ? Escape(replacement) : m.Groups["text"].Value;
            row[field] = $"NSLOCTEXT(\"{m.Groups["table"].Value}\", \"{newKey}\", \"{newText}\")";
        }
    }

    /// <summary>What players see (escapes undone: Larkwell "Haulier", not Larkwell \"Haulier\").</summary>
    private static string? LocalizedText(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? NsLocText.TextOf(s) : null;

    private static string Escape(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
