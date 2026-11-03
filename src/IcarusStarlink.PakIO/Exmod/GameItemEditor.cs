using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IcarusStarlink.PakIO.Exmod;

public enum EditableKind
{
    Number,
    Text,

    /// <summary>On or off (a tick box).</summary>
    Toggle,

    /// <summary>A row of another table (a drop-down): EditableField.LinkTable says which.</summary>
    Link,
}

/// <summary>Where a value sits in a row: a top-level field, then object keys / list indexes ("ArmourStats" › "(Value=\"BaseOxygenSlots_+\")").</summary>
public sealed record EditPath(string Top, IReadOnlyList<object> Steps)
{
    public string Key => Steps.Count == 0 ? Top : $"{Top}/{string.Join('/', Steps)}";
}

/// <summary>One value of a game item the Editor page shows: the game's, and what it is now (the game's, or a mod's).</summary>
/// <param name="LinkTable">For a Link: the table its rows come from ("D_RecipeSets").</param>
public sealed record EditableField(EditPath Path, string Label, string Description, EditableKind Kind, JsonNode? GameValue, JsonNode? CurrentValue, bool Inherited, string? LinkTable = null);

/// <summary>One game row an item uses, as a section of the Editor page.</summary>
/// <param name="Lists">The row's lists of amounts as they are now (a recipe's "Inputs"), by field: adding or taking out an entry rewrites the whole list from this.</param>
public sealed record EditableSection(
    string Key, string Title, string CurrentFile, string RowName, string GameRowName, int SharedBy,
    IReadOnlyList<EditableField> Fields, IReadOnlyList<string> StatGroups, IReadOnlyDictionary<string, JsonArray>? Lists = null)
{
    /// <summary>The mod already gave the item its own copy of this row.</summary>
    public bool IsOwnCopy => !string.Equals(RowName, GameRowName, StringComparison.OrdinalIgnoreCase);

    public string Table => GameTableIndex.TableNameOf(CurrentFile);
}

/// <summary>A stat for the picker: "BaseMovementSpeed_+%" shown as "Movement speed +%", a percent bonus.</summary>
public sealed record StatChoice(string Name, string Label, string Kind)
{
    public string Display => $"{Label}  ·  {Kind}";
}

public sealed record GameItemView(string ItemName, string? DisplayName, IReadOnlyList<EditableSection> Sections, IReadOnlyList<string> KeptAsGames);

/// <summary>A value to write (null = back to the game's).</summary>
public sealed record GameItemChange(string SectionKey, EditPath Path, JsonNode? Value);

/// <summary>
/// Changing the game's OWN items — the Editor page. The opposite of ItemCloner: nothing new is
/// made except, when the author asks, the item's own copy of a row it shares with others (the
/// basic Envirosuit's decay row is shared by 1,895 items; changing that row changes all of them).
/// Reads every row an item uses — its D_ItemsStatic links (name/weight, armour, eating, durability,
/// decay…), the buff it gives, the recipe that makes it and its workshop price — and writes only
/// the fields the author changed, as sparse edits of those rows (a whole top-level field when the
/// change is inside one, the EXMOD convention).
/// </summary>
public static partial class GameItemEditor
{
    private const string ItemsStatic = "Items-D_ItemsStatic.json";
    private const string Recipes = "Crafting-D_ProcessorRecipes.json";
    private const string WorkshopItems = "MetaWorkshop-D_WorkshopItems.json";
    private const string ModifierStates = "Modifiers-D_ModifierStates.json";
    private const string ItemableFile = "Traits-D_Itemable.json";
    public const string BuffSection = "Buff";
    public const string RecipeSection = "Recipe";
    public const string WorkshopSection = "Workshop";

    /// <summary>A storage of the item ("Storage:0"): a D_InventoryInfo row its D_Inventory row lists (slots live there).</summary>
    public const string StorageSection = "Storage";
    private const string InventoryField = "Inventory";

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelBoundary();

    private static readonly Dictionary<string, string> SectionTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Itemable"] = "Name, icon, weight",
        ["Armour"] = "Armour",
        ["Consumable"] = "Eating or using it",
        ["Equippable"] = "Bonuses when equipped",
        ["Durable"] = "Durability",
        ["Decayable"] = "Decay and spoiling",
        ["Deployable"] = "Placing it",
        ["Generator"] = "Generator",
        ["Processing"] = "Processing",
        ["Fillable"] = "Filling",
        ["Inventory"] = "Its inventory",
        ["Thermal"] = "Heat",
        ["ToolDamage"] = "Tool damage",
        ["FirearmData"] = "Firearm",
        ["Buildable"] = "Building",
    };

    /// <summary>
    /// The on/off fields shown as tick boxes: ones whose meaning is plain from the game data. The
    /// rest (actor pooling, client prediction, kill cam…) are engine plumbing, left as the game's.
    /// FieldTips words the ones it knows.
    /// </summary>
    private static readonly Dictionary<string, (string Label, string Description)> Toggles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EffectedByWeather"] = ("Weather damages it", "On = storms can damage it when it isn't sheltered."),
        ["bMustBeOutside"] = ("Must be outside", "On = it can only be placed outdoors."),
        ["bRequiresShelter"] = ("Needs shelter", "On = it only works indoors or under cover."),
        // Not "needs power": it's on by default, and the stone furnace keeps it on while burning fuel.
        ["RequiresEnergy"] = ("Requires energy", "On = it needs energy to work: power, or the fuel it burns (the stone furnace has this on)."),
        ["Destroyed_At_Zero"] = ("Destroyed at zero durability", "On = it's destroyed when its durability runs out."),
        ["RemoveOnly"] = ("Take out only", "On = things can only be taken out of this inventory, not put in."),
        ["bCanBeObstructed"] = ("Blocked by walls", "On = walls stop its heat (the game's own). Off = heat goes through walls in every direction: in game a large heater then warmed up to 100 m away and overheated players 35 m away."),
        ["bAllowAutoRestart"] = ("Restarts by itself", "On = it switches back on when its supply comes back. Off = once it has switched itself off for lack of supply, someone has to switch it on again."),
    };

    /// <summary>
    /// The links shown as drop-downs, a row of another table each: ones a player would want to
    /// point elsewhere. Animation, audio, mesh links and the like stay as the game's.
    /// </summary>
    private static readonly Dictionary<string, (string Label, string Description)> Links = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SpoiledItem"] = ("Spoils into", "What it turns into once it spoils."),
        ["DefaultRecipeSet"] = ("Recipe list", "The list of recipes it works from (a furnace's own list, a bench's)."),
        ["RecipeSets"] = ("Made at", "The bench, or By hand, where this recipe is crafted."),
    };

    private static readonly HashSet<string> InheritedWorthShowing = new(StringComparer.OrdinalIgnoreCase)
    {
        "Weight", "MaxStack", "Max_Durability", "SpoilTime", "RequiredMillijoules",
    };

    private static readonly Dictionary<string, (string Label, string Description)> KnownFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DisplayName"] = ("Name in game", "What players see."),
        ["Description"] = ("Description", "The text under the name."),
        ["FlavorText"] = ("Flavour text", "The extra story text."),
        ["Weight"] = ("Weight", "Lower = lighter in your inventory (0 = weightless). Higher = heavier."),
        ["MaxStack"] = ("Stack size", "How many fit in one slot. Higher = more per slot; 1 = doesn't stack."),
        ["Max_Durability"] = ("Durability", "Higher = lasts longer before it breaks. Lower = wears out sooner."),
        ["DecayTime"] = ("Decay time (seconds)", "Seconds per step of decay. Higher = lasts longer; lower = decays faster."),
        ["SpoilTime"] = ("Spoils after (seconds)", "Higher = keeps longer (10800 = 3 hours); lower = spoils sooner."),
        ["ModifierLifetime"] = ("Buff lasts (seconds)", "Higher = the buff lasts longer (900 = 15 min); lower = shorter."),
        ["ModifierEffectiveness"] = ("Buff strength (%)", "Higher = a stronger buff; 100 = as designed."),
        ["RequiredMillijoules"] = ("Crafting work", "Higher = takes longer to craft; lower = faster."),
        ["ExperienceMultiplier"] = ("Crafting XP multiplier", "Higher = more XP for crafting it; 1 = as designed."),
        // D_Generator: the Biofuel Generator makes 5000 from fuel at a ratio of 7500; raising a
        // ratio from 10 to 450 made a biofuel can last 45 times longer (tested in game).
        ["GenerationRate"] = ("Output", "How much it makes: power for a generator, water for a pump. Higher = more output."),
        ["GenerationRatio"] = ("Fuel efficiency", "How much it makes from each bit of fuel. Higher = fuel lasts longer; lower = burns fuel faster."),
        ["MaximumStoredUnits"] = ("Holds", "How much it holds (1000 = 1 litre). Higher = holds more."),
        ["StartingSlots"] = ("Slots", "How many inventory slots it has."),
    };

    /// <summary>Everything about one game item the Editor can change, as it is with <paramref name="mod"/> applied (null mod = the game alone). Null when the game has no such item.</summary>
    /// <param name="lookup">Shared by many Describe calls with the same, unchanged mod (a bulk edit): the whole-table indexes are built once. Null builds one for this call.</param>
    public static GameItemView? Describe(GameTableIndex game, ExmodPackage? mod, string itemName, GameItemLookup? lookup = null)
    {
        lookup ??= new GameItemLookup(game, mod);
        if (game.Row(ItemsStatic, itemName) is not { } gameStatic)
        {
            return null;
        }

        var effectiveStatic = Effective(game, mod, ItemsStatic, itemName) ?? gameStatic;
        var sections = new List<EditableSection>();
        var kept = new List<string>();

        foreach (var (field, value) in effectiveStatic)
        {
            if (value is not JsonObject link || link["RowName"]?.GetValue<string>() is not { } rowName || rowName == "None"
                || game.LinkTarget(ItemsStatic, field) is not { } table || game.CurrentFileFor(table) is not { } file)
            {
                continue;
            }

            // A row the game has is "the game's" (even one the mod re-pointed the item to), shared by
            // every item linking to it; a row only the mod has is this item's own copy, and its game
            // values come from the row it was copied from.
            var gameRowName = game.GameRowName(file, rowName) ?? gameStatic[field]?["RowName"]?.GetValue<string>() ?? rowName;
            var shared = game.HasRow(file, rowName) ? lookup.UsersOf(field, rowName) : 1;
            var section = BuildSection(game, mod, field, SectionTitles.GetValueOrDefault(field, Humanize(field)), file, rowName, gameRowName, Math.Max(shared, 1));

            // An inventory row only lists its storages (D_InventoryInfo): slots live one link further.
            var storages = field.Equals(InventoryField, StringComparison.OrdinalIgnoreCase)
                ? StorageSections(game, mod, lookup, file, rowName, gameRowName)
                : [];
            if (section.Fields.Count == 0 && section.StatGroups.Count == 0)
            {
                if (storages.Count == 0)
                {
                    kept.Add(shared > 1 ? $"{Humanize(field)} ({table}, shared by {shared:N0} items)" : $"{Humanize(field)} ({table})");
                }

                sections.AddRange(storages);
                continue;
            }

            sections.Add(section);
            sections.AddRange(storages);

            // The buff a consumable gives lives in its own table, linked from the consumable row.
            if (field.Equals("Consumable", StringComparison.OrdinalIgnoreCase)
                && lookup.BuffOf(file, rowName) is { } buff)
            {
                // Shared by every ITEM whose consumable gives this buff (a food row is often shared too).
                var isGameBuff = game.HasRow(ModifierStates, buff);
                var gameBuff = game.GameRowName(ModifierStates, buff) ?? BuffOf(game, null, file, gameRowName) ?? buff;
                var buffShared = isGameBuff ? lookup.BuffUsers(file, buff) : 1;
                sections.Add(BuildSection(game, mod, BuffSection, "Its buff", ModifierStates, buff, gameBuff, Math.Max(buffShared, 1)));
            }
        }

        // The recipe that makes it and its workshop price: rows of their own, found by what they produce.
        // Every recipe that makes it (wood walls, rope, glass have several: one per bench).
        // Recipes and the workshop hand out the item's D_ItemTemplate row, which is often named
        // differently (Kit_Stone_Furnace is made as "Stone_Furnace"): match through those names.
        var products = game.ProductNamesFor(itemName);
        bool Makes(JsonNode? element) => (element as JsonObject)?["RowName"] is JsonValue v && v.TryGetValue<string>(out var s) && products.Contains(s);
        var recipes = lookup.RecipesMaking(products);
        for (var i = 0; i < recipes.Count; i++)
        {
            var recipeRow = game.Row(Recipes, recipes[i]);
            var bench = recipeRow?["RecipeSets"] is JsonArray { Count: > 0 } sets ? sets[0]?["RowName"]?.GetValue<string>() : null;
            // Butchering a carcass is one recipe making several things: its ingredients are all of theirs.
            var others = (recipeRow?["Outputs"] as JsonArray)?.Count(o => !Makes(o?["Element"])) ?? 0;
            var title = (recipes.Count > 1 ? $"Recipe {i + 1} of {recipes.Count}" : "Recipe") + (bench is null ? "" : $" · {Humanize(bench)}")
                + (others > 0 ? $" · also makes {others} other thing{(others == 1 ? "" : "s")}" : "");
            sections.Add(BuildSection(game, mod, $"{RecipeSection}:{recipes[i]}", title, Recipes, recipes[i], recipes[i], 1));
        }

        if (lookup.WorkshopFor(products) is { } workshop)
        {
            sections.Add(BuildSection(game, mod, WorkshopSection, "Workshop price", WorkshopItems, workshop, workshop, 1));
        }

        var itemable = effectiveStatic["Itemable"]?["RowName"]?.GetValue<string>();
        var displayName = itemable is null ? null : NsLocText.TextOf((Effective(game, mod, ItemableFile, itemable) ?? game.Row(ItemableFile, itemable))?["DisplayName"]?.GetValue<string>());
        return new GameItemView(itemName, displayName, [.. sections.Where(s => s.Fields.Count > 0 || s.StatGroups.Count > 0)], kept);
    }

    /// <summary>
    /// Writes the changes into the mod. Sections in <paramref name="ownCopySections"/> that are shared
    /// with other items get the item's own copy first ("Decay_General_Envirosuit"), and the item is
    /// re-pointed at it, so only this item changes. Returns one line per change written.
    /// </summary>
    public static IReadOnlyList<string> Apply(GameTableIndex game, ExmodPackage mod, string itemName, IReadOnlyList<GameItemChange> changes, IReadOnlySet<string> ownCopySections)
    {
        var view = Describe(game, mod, itemName) ?? throw new ArgumentException($"'{itemName}' isn't an item in the game data.");
        var lines = new List<string>();
        foreach (var group in changes.GroupBy(c => c.SectionKey))
        {
            var section = view.Sections.FirstOrDefault(s => s.Key == group.Key) ?? throw new ArgumentException($"'{itemName}' has no '{group.Key}' section.");
            var rowName = section.RowName;
            if (ownCopySections.Contains(section.Key) && section.SharedBy > 1 && !section.IsOwnCopy)
            {
                rowName = section.Key == BuffSection ? GiveBuffOwnCopy(game, mod, itemName, section, lines)
                    : section.Key.StartsWith(StorageSection + ":", StringComparison.Ordinal) ? GiveStorageOwnCopy(game, mod, itemName, section, lines)
                    : GiveOwnCopy(game, mod, itemName, section.Key, section.CurrentFile, section.RowName, section.GameRowName, section.SharedBy, lines);

                // The item now links to other rows (the buff's copy also gives it its own consumable
                // row): sections still to come must be read again, or their changes go to the old,
                // shared row.
                view = Describe(game, mod, itemName) ?? view;
            }

            foreach (var change in group)
            {
                var field = section.Fields.FirstOrDefault(f => f.Path.Key == change.Path.Key);
                var before = field?.CurrentValue;
                SetValue(game, mod, section.CurrentFile, rowName, section.GameRowName, change.Path, change.Value);
                if (change.Path.Steps.Count == 0 && change.Value is JsonArray list)
                {
                    lines.Add($"{section.Table} › {rowName} · {FieldText(change.Path.Top, isNumber: false).Label}: now {list.Count} entr{(list.Count == 1 ? "y" : "ies")}");
                    continue;
                }

                var label = field?.Label ?? GameStats.StatLabel(change.Path.Steps.LastOrDefault() as string ?? change.Path.Top);
                lines.Add($"{section.Table} › {rowName} · {label}: {Show(before)} → {Show(change.Value ?? field?.GameValue)}");
            }
        }

        return lines;
    }

    /// <summary>A field in plain words: "MaxStack" → ("Stack size", "How many fit in one slot…"). Shared with the mod editor's Fields view.</summary>
    public static (string Label, string Description) FieldText(string field, bool isNumber) =>
        TryKnown(field, out var known) ? known
        : isNumber ? (Humanize(field), $"Higher = more {Humanize(field).ToLowerInvariant()}, lower = less. The game's value is shown beside it.")
        : (Humanize(field), "");

    /// <summary>A field's plain name and tip: this editor's own short list first, then FieldTips (the fields mods change most).</summary>
    private static bool TryKnown(string field, out (string Label, string Description) known) =>
        KnownFields.TryGetValue(field, out known) || FieldTips.TryGet(field, out known);

    private static EditableSection BuildSection(GameTableIndex game, ExmodPackage? mod, string key, string title, string file, string rowName, string gameRowName, int sharedBy)
    {
        var effective = Effective(game, mod, file, rowName) ?? new JsonObject();
        var gameRow = game.Row(file, gameRowName) ?? new JsonObject();
        var defaults = game.Defaults(file);
        var fields = new List<EditableField>();
        var statGroups = new List<string>();
        var lists = new Dictionary<string, JsonArray>(StringComparer.OrdinalIgnoreCase);

        var keys = effective.Select(kv => kv.Key).Concat(gameRow.Select(kv => kv.Key))
            // A table default only matters for the fields people change (weight, stack size…); the rest is noise like "aura range 100".
            .Concat((defaults ?? []).Where(kv => IsNumber(kv.Value) && InheritedWorthShowing.Contains(kv.Key)).Select(kv => kv.Key))
            // An on/off field or a link the Editor offers counts even where the row leaves it at the table's default.
            .Concat((defaults ?? []).Where(kv => (IsBool(kv.Value) && Toggles.ContainsKey(kv.Key)) || (kv.Value is JsonObject && Links.ContainsKey(kv.Key))).Select(kv => kv.Key))
            .Where(k => k != "Name")
            .Distinct(StringComparer.Ordinal);
        foreach (var top in keys)
        {
            var inherited = !effective.ContainsKey(top);
            var current = effective[top] ?? defaults?[top];
            var original = gameRow[top] ?? defaults?[top];
            switch (current ?? original)
            {
                case JsonValue v when IsBool(v) && Toggles.TryGetValue(top, out var toggle):
                    fields.Add(new EditableField(new EditPath(top, []), toggle.Label, toggle.Description, EditableKind.Toggle, original, current, inherited && !gameRow.ContainsKey(top)));
                    break;
                case JsonObject link when Links.TryGetValue(top, out var linkText) && link.ContainsKey("RowName"):
                    // A link object: the row name inside it changes, its table stays.
                    fields.Add(new EditableField(new EditPath(top, ["RowName"]), linkText.Label, linkText.Description, EditableKind.Link,
                        (original as JsonObject)?["RowName"], link["RowName"], false, LinkTableOf(link, defaults?[top])));
                    break;
                case JsonArray links when Links.TryGetValue(top, out var listText) && links.All(e => e is JsonObject o && o.ContainsKey("RowName")):
                    // A list of links (a recipe's benches): one drop-down per entry.
                    for (var i = 0; i < links.Count; i++)
                    {
                        var entry = (JsonObject)links[i]!;
                        fields.Add(new EditableField(new EditPath(top, [i, "RowName"]), links.Count == 1 ? listText.Label : $"{listText.Label} ({i + 1})", listText.Description, EditableKind.Link,
                            ((original as JsonArray)?.ElementAtOrDefault(i) as JsonObject)?["RowName"], entry["RowName"], false, LinkTableOf(entry, null)));
                    }

                    break;
                case JsonValue v when IsNumber(v):
                    var (label, description) = TryKnown(top, out var known) ? known : (Humanize(top), $"The game's value is shown on the right. Higher = more {Humanize(top).ToLowerInvariant()}, lower = less.");
                    fields.Add(new EditableField(new EditPath(top, []), label, description, EditableKind.Number, original, current, inherited && !gameRow.ContainsKey(top)));
                    break;
                case JsonValue v when NsLocText.TextOf(v.TryGetValue<string>(out var s) ? s : null) is { } shownText && shownText != "None":
                    var (textLabel, textDescription) = TryKnown(top, out var knownText) ? knownText : (Humanize(top), "Text players see.");
                    fields.Add(new EditableField(new EditPath(top, []), textLabel, textDescription, EditableKind.Text, original, current, false));
                    break;
                case JsonObject obj when obj.Count == 0 || obj.All(kv => GameStats.IsStatKey(kv.Key)):
                    // A stats object: one value per stat ("(Value=\"BaseOxygenSlots_+\")": 1).
                    statGroups.Add(top);
                    foreach (var (stat, _) in obj.Concat((original as JsonObject) ?? []).DistinctBy(kv => kv.Key))
                    {
                        var cur = (current as JsonObject)?[stat];
                        if (cur is not null && !IsNumber(cur))
                        {
                            continue;
                        }

                        var statLabel = GameStats.StatLabel(stat);
                        GameStats.LearnGameText(game);
                        fields.Add(new EditableField(new EditPath(top, [stat]), statLabel, GameStats.StatHint(GameStats.StatNameOf(stat)), EditableKind.Number, (original as JsonObject)?[stat], cur, false));
                    }

                    break;
                case JsonObject obj:
                    // One level in (a consumable's "Modifier": the buff's lifetime and strength), never into links.
                    foreach (var (child, childValue) in obj)
                    {
                        if (IsNumber(childValue))
                        {
                            var (childLabel, childDescription) = TryKnown(child, out var k) ? k : (Humanize(child), $"Higher = more {Humanize(child).ToLowerInvariant()}, lower = less.");
                            fields.Add(new EditableField(new EditPath(top, [child]), childLabel, childDescription, EditableKind.Number, (original as JsonObject)?[child], childValue, false));
                        }
                    }

                    break;
                case JsonArray array:
                    // Lists of amounts: a recipe's ingredients, a workshop item's costs.
                    lists[top] = (JsonArray)array.DeepClone();
                    for (var i = 0; i < array.Count; i++)
                    {
                        if (array[i] is not JsonObject entry)
                        {
                            continue;
                        }

                        var amountKey = entry.ContainsKey("Count") ? "Count" : entry.ContainsKey("Amount") ? "Amount" : null;
                        var what = (entry["Element"] ?? entry["Meta"] ?? entry["Item"])?["RowName"]?.GetValue<string>();
                        if (amountKey is null || what is null || !IsNumber(entry[amountKey]))
                        {
                            continue;
                        }

                        var (entryLabel, entryDescription) = EntryText(game, top, what);
                        fields.Add(new EditableField(new EditPath(top, [i, amountKey]), entryLabel, entryDescription, EditableKind.Number,
                            SameEntry(original as JsonArray, entry, i)?[amountKey], entry[amountKey], false));
                    }

                    break;
            }
        }

        return new EditableSection(key, title, file, rowName, gameRowName, sharedBy, fields, statGroups, lists);
    }

    private static (string Label, string Description) EntryText(GameTableIndex game, string list, string what)
    {
        var name = ItemDisplayName(game, what) ?? Humanize(what);
        return list switch
        {
            "Inputs" => ($"Needs {name}", "How many it takes to craft. Lower = cheaper; keep at least 1."),
            "Outputs" => ($"Makes {name}", "How many one craft gives. Higher = more per craft."),
            "ResearchCost" => ($"Research cost ({name})", "Paid once to unlock it in the workshop. Lower = cheaper, 0 = free."),
            "ReplicationCost" => ($"Replication cost ({name})", "Paid for each copy you take down. Lower = cheaper, 0 = free."),
            "ItemsForRepair" => ($"Repair needs {name}", "How many it takes to repair. Lower = cheaper."),
            _ => ($"{Humanize(list)}: {name}", "Higher = more, lower = less."),
        };
    }

    private static string? ItemDisplayName(GameTableIndex game, string itemName) =>
        game.Row(ItemsStatic, itemName)?["Itemable"]?["RowName"]?.GetValue<string>() is { } itemable
            ? NsLocText.TextOf(game.Row(ItemableFile, itemable)?["DisplayName"]?.GetValue<string>())
            : null;

    /// <summary>The row as the game has it with the mod's fields on top; the mod's own row when the game has none (an own copy); null when neither has it.</summary>
    private static JsonObject? Effective(GameTableIndex game, ExmodPackage? mod, string file, string rowName)
    {
        var modItem = FindItem(mod, file, rowName);
        var gameRow = game.Row(file, rowName);
        if (modItem is null)
        {
            return gameRow;
        }

        var result = (JsonObject?)gameRow?.DeepClone() ?? [];
        foreach (var (field, value) in modItem.Fields)
        {
            result[field] = value?.DeepClone();
        }

        return result;
    }

    /// <summary>Effective, for GameItemLookup.</summary>
    internal static JsonObject? EffectiveRow(GameTableIndex game, ExmodPackage? mod, string file, string rowName) => Effective(game, mod, file, rowName);

    private static ExmodFileItem? FindItem(ExmodPackage? mod, string file, string rowName) =>
        mod?.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, file, StringComparison.OrdinalIgnoreCase))
            ?.FileItems.FirstOrDefault(i => string.Equals(i.Name, rowName, StringComparison.OrdinalIgnoreCase));

    private static ExmodFileItem GetOrAddItem(ExmodPackage mod, string file, string rowName)
    {
        if (FindItem(mod, file, rowName) is { } existing)
        {
            return existing;
        }

        var row = mod.Rows.FirstOrDefault(r => string.Equals(r.CurrentFile, file, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            row = new ExmodFileRow { CurrentFile = file };
            var end = mod.Rows.FindIndex(r => ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile));
            mod.Rows.Insert(end < 0 ? mod.Rows.Count : end, row);
        }

        var item = new ExmodFileItem { Name = rowName };
        row.FileItems.Add(item);
        return item;
    }

    /// <summary>Copies a shared row under the item's name and points the item at it (its D_ItemsStatic link). Returns the copy's name.</summary>
    private static string GiveOwnCopy(GameTableIndex game, ExmodPackage mod, string itemName, string linkField, string file, string sourceRow, string gameRowName, int sharedBy, List<string> lines)
    {
        var copyName = CopyRow(game, mod, file, sourceRow, gameRowName, itemName);
        GetOrAddItem(mod, ItemsStatic, itemName).Fields[linkField] = new JsonObject { ["RowName"] = copyName };
        lines.Add($"{GameTableIndex.TableNameOf(file)} › {copyName}: {itemName}'s own copy of {gameRowName} (shared by {sharedBy:N0} items), so only it changes");
        return copyName;
    }

    /// <summary>
    /// The buff hangs off the item's consumable row, which other foods often share (Raw_Food: 16
    /// items): that row gets the item's own copy first, so re-pointing the buff changes only this item.
    /// </summary>
    private static string GiveBuffOwnCopy(GameTableIndex game, ExmodPackage mod, string itemName, EditableSection buff, List<string> lines)
    {
        var copyName = CopyRow(game, mod, ModifierStates, buff.RowName, buff.GameRowName, itemName);
        lines.Add($"{buff.Table} › {copyName}: {itemName}'s own copy of the buff {buff.GameRowName} (shared by {buff.SharedBy:N0} items)");

        var consumableFile = game.CurrentFileFor("D_Consumable") ?? "Traits-D_Consumable.json";
        var consumable = LinkOf(game, mod, itemName, "Consumable") ?? throw new ArgumentException($"'{itemName}' has no consumable row to carry the buff.");
        if (game.HasRow(consumableFile, consumable) && CountUsers(game, mod, "Consumable", consumable) > 1)
        {
            consumable = GiveOwnCopy(game, mod, itemName, "Consumable", consumableFile, consumable, game.GameRowName(consumableFile, consumable)!,
                CountUsers(game, mod, "Consumable", consumable), lines);
        }

        var modifier = (JsonObject?)(Effective(game, mod, consumableFile, consumable)?["Modifier"]?.DeepClone()) ?? [];
        modifier["Modifier"] = new JsonObject { ["RowName"] = copyName };
        GetOrAddItem(mod, consumableFile, consumable).Fields["Modifier"] = modifier;
        return copyName;
    }

    /// <summary>
    /// The item's storages: one card per D_InventoryInfo row its inventory row lists ("Storage", or
    /// "Storage · Input" when it has several). Shared by every item whose inventory lists that row
    /// (the benches' "Processor" storage: 40 items).
    /// </summary>
    private static List<EditableSection> StorageSections(GameTableIndex game, ExmodPackage? mod, GameItemLookup lookup, string inventoryFile, string inventoryRow, string gameInventoryRow)
    {
        var storages = new List<EditableSection>();
        if (Effective(game, mod, inventoryFile, inventoryRow)?["Inventories"] is not JsonArray list)
        {
            return storages;
        }

        var gameList = game.Row(inventoryFile, gameInventoryRow)?["Inventories"] as JsonArray;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is not JsonObject entry || entry["RowName"]?.GetValue<string>() is not { } info || info == "None"
                || game.CurrentFileFor(entry["DataTableName"]?.GetValue<string>() ?? "D_InventoryInfo") is not { } infoFile)
            {
                continue;
            }

            // A row only the mod has is the item's own copy: its game values come from the row it copied.
            var gameInfo = game.GameRowName(infoFile, info) ?? (gameList?.ElementAtOrDefault(i) as JsonObject)?["RowName"]?.GetValue<string>() ?? info;
            var shared = game.HasRow(infoFile, info) ? lookup.InventoryInfoUsers(inventoryFile, info) : 1;
            var id = (Effective(game, mod, infoFile, info) ?? game.Row(infoFile, gameInfo))?["InventoryID"]?["Value"]?.GetValue<string>();
            var title = list.Count == 1 ? "Storage" : $"Storage · {(id is { Length: > 0 } ? Humanize(id) : (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))}";
            var section = BuildSection(game, mod, $"{StorageSection}:{i}", title, infoFile, info, gameInfo, Math.Max(shared, 1));
            if (section.Fields.Count > 0)
            {
                storages.Add(section);
            }
        }

        return storages;
    }

    /// <summary>
    /// A storage shared with other items: copies its D_InventoryInfo row for the item, gives the item
    /// its own inventory row too when that's shared, and points the item's inventory at the copy.
    /// </summary>
    private static string GiveStorageOwnCopy(GameTableIndex game, ExmodPackage mod, string itemName, EditableSection storage, List<string> lines)
    {
        var copyName = CopyRow(game, mod, storage.CurrentFile, storage.RowName, storage.GameRowName, itemName);
        lines.Add($"{storage.Table} › {copyName}: {itemName}'s own copy of the storage {storage.GameRowName} (shared by {storage.SharedBy:N0} items)");

        var inventoryFile = game.CurrentFileFor("D_Inventory") ?? "Traits-D_Inventory.json";
        var inventory = LinkOf(game, mod, itemName, InventoryField) ?? throw new ArgumentException($"'{itemName}' has no inventory row to carry the storage.");
        if (game.HasRow(inventoryFile, inventory) && CountUsers(game, mod, InventoryField, inventory) is var users and > 1)
        {
            inventory = GiveOwnCopy(game, mod, itemName, InventoryField, inventoryFile, inventory, game.GameRowName(inventoryFile, inventory)!, users, lines);
        }

        var list = (JsonArray?)Effective(game, mod, inventoryFile, inventory)?["Inventories"]?.DeepClone() ?? [];
        var index = int.Parse(storage.Key[(StorageSection.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        if (list.ElementAtOrDefault(index) is JsonObject entry)
        {
            entry["RowName"] = copyName;
        }

        GetOrAddItem(mod, inventoryFile, inventory).Fields["Inventories"] = list;
        return copyName;
    }

    /// <summary>A full copy of a row (as the mod has it) under "&lt;game row&gt;_&lt;item&gt;"; its text keeps its words, only the keys become the copy's own.</summary>
    private static string CopyRow(GameTableIndex game, ExmodPackage mod, string file, string sourceRow, string gameRowName, string itemName)
    {
        var copyName = $"{gameRowName}_{itemName}";
        for (var i = 2; game.HasRow(file, copyName) || FindItem(mod, file, copyName) is not null; i++)
        {
            copyName = $"{gameRowName}_{itemName}_{i}";
        }

        var copy = GetOrAddItem(mod, file, copyName);
        foreach (var (field, value) in Effective(game, mod, file, sourceRow) ?? [])
        {
            if (field == "Name")
            {
                continue;
            }

            // Localized text keys are "<Row>-<Field>" and must stay unique per row.
            copy.Fields[field] = value is JsonValue v && v.TryGetValue<string>(out var text) && NsLocText.WithKey(text, $"{copyName}-{field}") is { } rekeyed
                ? rekeyed
                : value?.DeepClone();
        }

        return copyName;
    }

    /// <summary>The row an item's field links to, with the mod's changes; null for "None".</summary>
    private static string? LinkOf(GameTableIndex game, ExmodPackage? mod, string itemName, string field) =>
        Effective(game, mod, ItemsStatic, itemName)?[field]?["RowName"]?.GetValue<string>() is { } row && row != "None" ? row : null;

    /// <summary>How many items (with the mod's changes) link to this row.</summary>
    private static int CountUsers(GameTableIndex game, ExmodPackage? mod, string field, string rowName) => new GameItemLookup(game, mod).UsersOf(field, rowName);

    /// <summary>The buff a consumable row gives (with the mod's changes); null for none.</summary>
    private static string? BuffOf(GameTableIndex game, ExmodPackage? mod, string consumableFile, string consumableRow) =>
        Effective(game, mod, consumableFile, consumableRow)?["Modifier"]?["Modifier"]?["RowName"]?.GetValue<string>() is { } buff && buff != "None" ? buff : null;

    /// <summary>The entry of a list (a recipe's ingredients, a workshop item's costs) for the same thing as <paramref name="entry"/>, else the one at the same position.</summary>
    private static JsonNode? SameEntry(JsonArray? list, JsonObject entry, int index)
    {
        var what = Identity(entry);
        return list?.FirstOrDefault(e => e is JsonObject o && what is not null && string.Equals(Identity(o), what, StringComparison.OrdinalIgnoreCase))
            ?? (what is null ? list?.ElementAtOrDefault(index) : null);
    }

    private static string? Identity(JsonObject entry) => (entry["Element"] ?? entry["Meta"] ?? entry["Item"])?["RowName"]?.GetValue<string>();

    private static void SetValue(GameTableIndex game, ExmodPackage mod, string file, string rowName, string gameRowName, EditPath path, JsonNode? value)
    {
        var isGameRow = game.HasRow(file, rowName);
        var gameRow = game.Row(file, isGameRow ? rowName : gameRowName);
        var item = GetOrAddItem(mod, file, rowName);
        var gameTop = gameRow?[path.Top] ?? game.Defaults(file)?[path.Top];
        var top = (item.Fields.TryGetValue(path.Top, out var existing) ? existing : gameTop)?.DeepClone();

        if (path.Steps.Count == 0)
        {
            top = value is null ? gameTop?.DeepClone() : Encode(top ?? gameTop, value);
        }
        else
        {
            top ??= path.Steps[0] is int ? new JsonArray() : new JsonObject();
            var parent = top;
            for (var i = 0; i < path.Steps.Count - 1; i++)
            {
                parent = path.Steps[i] is int index ? parent[index]! : parent[(string)path.Steps[i]] ??= new JsonObject();
            }

            var last = path.Steps[^1];
            var gameLeaf = Navigate(gameTop, path.Steps, top);
            var leaf = value is null ? gameLeaf?.DeepClone() : value.DeepClone();
            if (last is int at)
            {
                parent[at] = leaf;
            }
            else if (leaf is null)
            {
                ((JsonObject)parent).Remove((string)last);
            }
            else
            {
                parent[(string)last] = leaf;
            }
        }

        // Back to exactly the game's value: the mod no longer needs the field (or the row). An own
        // copy keeps every field: it is a whole row the game doesn't have.
        if (isGameRow && gameRow is not null && JsonNode.DeepEquals(top, gameTop))
        {
            item.Fields.Remove(path.Top);
            if (item.Fields.Count == 0)
            {
                var row = mod.Rows.First(r => r.FileItems.Contains(item));
                row.FileItems.Remove(item);
                if (row.FileItems.Count == 0)
                {
                    mod.Rows.Remove(row);
                }
            }

            return;
        }

        item.Fields[path.Top] = top;
    }

    /// <summary>Localized text keeps the game's NSLOCTEXT table and key; only what players see changes.</summary>
    private static JsonNode Encode(JsonNode? previous, JsonNode value)
    {
        if (value is JsonValue v && v.TryGetValue<string>(out var text)
            && previous is JsonValue p && p.TryGetValue<string>(out var before) && NsLocText.TryParse(before, out var table, out var key, out _))
        {
            return NsLocText.Make(table, key, text);
        }

        return value.DeepClone();
    }

    /// <summary>The game's value at a path; a list step finds the game's entry for the same thing the mod's entry is (by ingredient, not position).</summary>
    private static JsonNode? Navigate(JsonNode? node, IReadOnlyList<object> steps, JsonNode? modTop = null)
    {
        var mine = modTop;
        foreach (var step in steps)
        {
            if (step is int index)
            {
                node = mine is JsonArray myList && myList.ElementAtOrDefault(index) is JsonObject myEntry
                    ? SameEntry(node as JsonArray, myEntry, index)
                    : (node as JsonArray)?.ElementAtOrDefault(index);
                mine = (mine as JsonArray)?.ElementAtOrDefault(index);
            }
            else
            {
                node = (node as JsonObject)?[(string)step];
                mine = (mine as JsonObject)?[(string)step];
            }
        }

        return node;
    }

    private static bool IsNumber(JsonNode? node) => ScaleRules.TryReadNumber(node, out _);

    private static bool IsBool(JsonNode? node) => node is JsonValue v && v.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False;

    /// <summary>The table a link points into: its own DataTableName, else the table default's.</summary>
    private static string? LinkTableOf(JsonObject link, JsonNode? fallback) =>
        link["DataTableName"]?.GetValue<string>() ?? (fallback as JsonObject)?["DataTableName"]?.GetValue<string>();

    private static string Show(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) ? (NsLocText.TextOf(s) ?? s) : value?.ToJsonString() ?? "(none)";

    /// <summary>"Max_Durability" → "Max durability"; "OxygenSlots" → "Oxygen slots"; "bIsClientSideOnly" → "Is client side only" (Unreal's "b" marks an on/off field).</summary>
    internal static string Humanize(string name)
    {
        if (name.Length > 1 && name[0] == 'b' && char.IsUpper(name[1]))
        {
            name = name[1..];
        }

        var words = CamelBoundary().Replace(name.Replace('_', ' '), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = string.Join(' ', words.Select((w, i) => i == 0 ? w : (w.Length > 1 && w.All(char.IsUpper) ? w : w.ToLowerInvariant())));
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
