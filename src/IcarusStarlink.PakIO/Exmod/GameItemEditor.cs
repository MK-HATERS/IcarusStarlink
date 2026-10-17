using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace IcarusStarlink.PakIO.Exmod;

public enum EditableKind
{
    Number,
    Text,
}

/// <summary>Where a value sits in a row: a top-level field, then object keys / list indexes ("ArmourStats" › "(Value=\"BaseOxygenSlots_+\")").</summary>
public sealed record EditPath(string Top, IReadOnlyList<object> Steps)
{
    public string Key => Steps.Count == 0 ? Top : $"{Top}/{string.Join('/', Steps)}";
}

/// <summary>One value of a game item the Editor page shows: the game's, and what it is now (the game's, or a mod's).</summary>
public sealed record EditableField(EditPath Path, string Label, string Description, EditableKind Kind, JsonNode? GameValue, JsonNode? CurrentValue, bool Inherited);

/// <summary>One game row an item uses, as a section of the Editor page.</summary>
public sealed record EditableSection(
    string Key, string Title, string CurrentFile, string RowName, string GameRowName, int SharedBy,
    IReadOnlyList<EditableField> Fields, IReadOnlyList<string> StatGroups)
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

    [GeneratedRegex("""^\(Value="(?<stat>.+)"\)$""")]
    private static partial Regex StatKey();

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
    };

    /// <summary>Everything about one game item the Editor can change, as it is with <paramref name="mod"/> applied (null mod = the game alone). Null when the game has no such item.</summary>
    public static GameItemView? Describe(GameTableIndex game, ExmodPackage? mod, string itemName)
    {
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
            var shared = game.HasRow(file, rowName) ? CountUsers(game, mod, field, rowName) : 1;
            var section = BuildSection(game, mod, field, SectionTitles.GetValueOrDefault(field, Humanize(field)), file, rowName, gameRowName, Math.Max(shared, 1));
            if (section.Fields.Count == 0 && section.StatGroups.Count == 0)
            {
                kept.Add(shared > 1 ? $"{Humanize(field)} ({table}, shared by {shared:N0} items)" : $"{Humanize(field)} ({table})");
                continue;
            }

            sections.Add(section);

            // The buff a consumable gives lives in its own table, linked from the consumable row.
            if (field.Equals("Consumable", StringComparison.OrdinalIgnoreCase)
                && BuffOf(game, mod, file, rowName) is { } buff)
            {
                // Shared by every ITEM whose consumable gives this buff (a food row is often shared too).
                var isGameBuff = game.HasRow(ModifierStates, buff);
                var gameBuff = game.GameRowName(ModifierStates, buff) ?? BuffOf(game, null, file, gameRowName) ?? buff;
                var buffShared = isGameBuff ? CountBuffUsers(game, mod, file, buff) : 1;
                sections.Add(BuildSection(game, mod, BuffSection, "Its buff", ModifierStates, buff, gameBuff, Math.Max(buffShared, 1)));
            }
        }

        // The recipe that makes it and its workshop price: rows of their own, found by what they produce.
        // Every recipe that makes it (wood walls, rope, glass have several: one per bench).
        // Recipes and the workshop hand out the item's D_ItemTemplate row, which is often named
        // differently (Kit_Stone_Furnace is made as "Stone_Furnace"): match through those names.
        var products = game.ProductNamesFor(itemName);
        bool Makes(JsonNode? element) => (element as JsonObject)?["RowName"] is JsonValue v && v.TryGetValue<string>(out var s) && products.Contains(s);
        var recipes = (game.Rows(Recipes) ?? []).Where(kv => kv.Value?["Outputs"] is JsonArray outputs
                && outputs.Any(o => Makes(o?["Element"])))
            .Select(kv => kv.Key).ToList();
        for (var i = 0; i < recipes.Count; i++)
        {
            var recipeRow = game.Row(Recipes, recipes[i]);
            var bench = recipeRow?["RecipeSets"]?[0]?["RowName"]?.GetValue<string>();
            // Butchering a carcass is one recipe making several things: its ingredients are all of theirs.
            var others = (recipeRow?["Outputs"] as JsonArray)?.Count(o => !Makes(o?["Element"])) ?? 0;
            var title = (recipes.Count > 1 ? $"Recipe {i + 1} of {recipes.Count}" : "Recipe") + (bench is null ? "" : $" · {Humanize(bench)}")
                + (others > 0 ? $" · also makes {others} other thing{(others == 1 ? "" : "s")}" : "");
            sections.Add(BuildSection(game, mod, $"{RecipeSection}:{recipes[i]}", title, Recipes, recipes[i], recipes[i], 1));
        }

        if ((game.Rows(WorkshopItems) ?? []).FirstOrDefault(kv => Makes(kv.Value?["Item"])) is { Key: { } workshop })
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
                rowName = section.Key == BuffSection
                    ? GiveBuffOwnCopy(game, mod, itemName, section, lines)
                    : GiveOwnCopy(game, mod, itemName, section.Key, section.CurrentFile, section.RowName, section.GameRowName, section.SharedBy, lines);
            }

            foreach (var change in group)
            {
                var field = section.Fields.FirstOrDefault(f => f.Path.Key == change.Path.Key);
                var before = field?.CurrentValue;
                SetValue(game, mod, section.CurrentFile, rowName, section.GameRowName, change.Path, change.Value);
                var label = field?.Label ?? StatLabel(change.Path.Steps.LastOrDefault() as string ?? change.Path.Top);
                lines.Add($"{section.Table} › {rowName} · {label}: {Show(before)} → {Show(change.Value ?? field?.GameValue)}");
            }
        }

        return lines;
    }

    /// <summary>
    /// "BaseOxygenSlots_+" → "Oxygen slots"; "BaseMovementSpeed_+%" → "Movement speed +%"; "BaseColdResistance_%" → "Cold resistance %".
    /// The game has both a flat and a percent version of many stats (BaseMovementSpeed_+ and _+%),
    /// so the suffix keeps them apart.
    /// </summary>
    public static string StatLabel(string stat)
    {
        var name = StatKey().Match(stat) is { Success: true } m ? m.Groups["stat"].Value : stat;
        var suffix = name.EndsWith("_+%", StringComparison.Ordinal) ? " +%" : name.EndsWith("_%", StringComparison.Ordinal) ? " %" : "";
        name = name.TrimEnd('+', '%', '_');
        if (name.StartsWith("Base", StringComparison.Ordinal) && name.Length > 4 && char.IsUpper(name[4]))
        {
            name = name[4..];
        }

        return Humanize(name) + suffix;
    }

    /// <summary>A field in plain words: "MaxStack" → ("Stack size", "How many fit in one slot…"). Shared with the mod editor's Fields view.</summary>
    public static (string Label, string Description) FieldText(string field, bool isNumber) =>
        KnownFields.TryGetValue(field, out var known) ? known
        : isNumber ? (Humanize(field), $"Higher = more {Humanize(field).ToLowerInvariant()}, lower = less. The game's value is shown beside it.")
        : (Humanize(field), "");

    /// <summary>A stats object: every key is a stat ("(Value=\"BaseOxygenSlots_+\")": 1). An empty object counts when its name says so ("ArmourStats").</summary>
    public static bool IsStatsObject(string field, JsonNode? value) =>
        value is JsonObject obj && (obj.Count == 0 ? field.EndsWith("Stats", StringComparison.OrdinalIgnoreCase) : obj.All(kv => StatKey().IsMatch(kv.Key)));

    /// <summary>The stat name inside a stats key: "(Value=\"BaseOxygenSlots_+\")" → "BaseOxygenSlots_+".</summary>
    public static string StatNameOf(string key) => StatKey().Match(key) is { Success: true } m ? m.Groups["stat"].Value : key;

    /// <summary>Plain description of one stat for a row, from its label.</summary>
    public static string StatHint(string statName) => StatDescription(StatLabel(statName));

    /// <summary>What kind of number a stat is, in words, for the stat picker.</summary>
    public static string StatKind(string statName) =>
        statName.EndsWith("_+%", StringComparison.Ordinal) ? "percent bonus (10 = +10%)"
        : statName.EndsWith("_%", StringComparison.Ordinal) ? "percent (10 = 10%)"
        : "flat amount (10 = +10)";

    /// <summary>The stats an item can grant (the game's "Base…" stats), for the stat picker.</summary>
    public static IReadOnlyList<StatChoice> StatChoices(GameTableIndex game) =>
        game.CurrentFileFor("D_Stats") is { } stats
            ? [.. (game.Rows(stats) ?? []).Select(kv => kv.Key)
                .Where(n => n.StartsWith("Base", StringComparison.Ordinal))
                .Select(n => new StatChoice(n, StatLabel(n), StatKind(n)))
                .OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)]
            : [];

    /// <summary>Whether a key is a stat key ("(Value=\"BaseOxygenSlots_+\")").</summary>
    public static bool IsStatKey(string key) => StatKey().IsMatch(key);

    /// <summary>
    /// Stat values are whole numbers in the game (an int per stat: all 15,333 stat values in the
    /// game's tables are), so 5.5 would lose its fraction or not load. Returns the first stat
    /// anywhere under node with a fractional value ("Cold resistance: 5.5"), or null.
    /// </summary>
    public static string? FractionalStat(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (IsStatKey(key) && value is JsonValue v && ScaleRules.TryReadNumber(v, out var n) && n != Math.Floor(n))
                    {
                        return $"{StatLabel(key)}: {n.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                    }

                    if (FractionalStat(value) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;
            case JsonArray array:
                return array.Select(FractionalStat).FirstOrDefault(found => found is not null);
            default:
                return null;
        }
    }

    /// <summary>The key a stat has in a stats object: "BaseColdResistance_%" → "(Value=\"BaseColdResistance_%\")".</summary>
    public static string StatKeyFor(string statName) => StatKey().IsMatch(statName) ? statName : $"(Value=\"{statName}\")";

    private static EditableSection BuildSection(GameTableIndex game, ExmodPackage? mod, string key, string title, string file, string rowName, string gameRowName, int sharedBy)
    {
        var effective = Effective(game, mod, file, rowName) ?? new JsonObject();
        var gameRow = game.Row(file, gameRowName) ?? new JsonObject();
        var defaults = game.Defaults(file);
        var fields = new List<EditableField>();
        var statGroups = new List<string>();

        var keys = effective.Select(kv => kv.Key).Concat(gameRow.Select(kv => kv.Key))
            // A table default only matters for the fields people change (weight, stack size…); the rest is noise like "aura range 100".
            .Concat((defaults ?? []).Where(kv => IsNumber(kv.Value) && InheritedWorthShowing.Contains(kv.Key)).Select(kv => kv.Key))
            .Where(k => k != "Name")
            .Distinct(StringComparer.Ordinal);
        foreach (var top in keys)
        {
            var inherited = !effective.ContainsKey(top);
            var current = effective[top] ?? defaults?[top];
            var original = gameRow[top] ?? defaults?[top];
            switch (current ?? original)
            {
                case JsonValue v when IsNumber(v):
                    var (label, description) = KnownFields.TryGetValue(top, out var known) ? known : (Humanize(top), $"The game's value is shown on the right. Higher = more {Humanize(top).ToLowerInvariant()}, lower = less.");
                    fields.Add(new EditableField(new EditPath(top, []), label, description, EditableKind.Number, original, current, inherited && !gameRow.ContainsKey(top)));
                    break;
                case JsonValue v when NsLocText.TextOf(v.TryGetValue<string>(out var s) ? s : null) is { } shownText && shownText != "None":
                    var (textLabel, textDescription) = KnownFields.TryGetValue(top, out var knownText) ? knownText : (Humanize(top), "Text players see.");
                    fields.Add(new EditableField(new EditPath(top, []), textLabel, textDescription, EditableKind.Text, original, current, false));
                    break;
                case JsonObject obj when obj.Count == 0 || obj.All(kv => StatKey().IsMatch(kv.Key)):
                    // A stats object: one value per stat ("(Value=\"BaseOxygenSlots_+\")": 1).
                    statGroups.Add(top);
                    foreach (var (stat, _) in obj.Concat((original as JsonObject) ?? []).DistinctBy(kv => kv.Key))
                    {
                        var cur = (current as JsonObject)?[stat];
                        if (cur is not null && !IsNumber(cur))
                        {
                            continue;
                        }

                        var statLabel = StatLabel(stat);
                        fields.Add(new EditableField(new EditPath(top, [stat]), statLabel, StatDescription(statLabel), EditableKind.Number, (original as JsonObject)?[stat], cur, false));
                    }

                    break;
                case JsonObject obj:
                    // One level in (a consumable's "Modifier": the buff's lifetime and strength), never into links.
                    foreach (var (child, childValue) in obj)
                    {
                        if (IsNumber(childValue))
                        {
                            var (childLabel, childDescription) = KnownFields.TryGetValue(child, out var k) ? k : (Humanize(child), $"Higher = more {Humanize(child).ToLowerInvariant()}, lower = less.");
                            fields.Add(new EditableField(new EditPath(top, [child]), childLabel, childDescription, EditableKind.Number, (original as JsonObject)?[child], childValue, false));
                        }
                    }

                    break;
                case JsonArray array:
                    // Lists of amounts: a recipe's ingredients, a workshop item's costs.
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

        return new EditableSection(key, title, file, rowName, gameRowName, sharedBy, fields, statGroups);
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

    private static string StatDescription(string label) =>
        label.EndsWith(" +%", StringComparison.Ordinal)
            ? $"Percent bonus (10 = +10%). Higher = more {label[..^3].ToLowerInvariant()}, lower = less; 0 = no bonus."
            : label.EndsWith(" %", StringComparison.Ordinal)
                ? $"Percent (10 = 10%). Higher = more {label[..^2].ToLowerInvariant()}, lower = less; 0 = none."
                : $"Flat amount. Higher = more {label.ToLowerInvariant()}, lower = less; 0 = no bonus.";

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

    /// <summary>Every item's link for a field, with the mod's changes on top (including items only the mod has).</summary>
    private static IEnumerable<string?> EffectiveLinks(GameTableIndex game, ExmodPackage? mod, string field)
    {
        var overrides = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in mod?.Rows.Where(r => string.Equals(r.CurrentFile, ItemsStatic, StringComparison.OrdinalIgnoreCase)).SelectMany(r => r.FileItems) ?? [])
        {
            if (item.Fields.TryGetValue(field, out var link))
            {
                overrides[item.Name] = link?["RowName"]?.GetValue<string>();
            }
        }

        foreach (var (name, node) in game.Rows(ItemsStatic) ?? [])
        {
            yield return overrides.Remove(name, out var overridden) ? overridden : node?[field]?["RowName"]?.GetValue<string>();
        }

        foreach (var remaining in overrides.Values)
        {
            yield return remaining;
        }
    }

    /// <summary>How many items (with the mod's changes) link to this row.</summary>
    private static int CountUsers(GameTableIndex game, ExmodPackage? mod, string field, string rowName) =>
        EffectiveLinks(game, mod, field).Count(link => string.Equals(link, rowName, StringComparison.OrdinalIgnoreCase));

    /// <summary>How many items get this buff, through whichever consumable rows carry it.</summary>
    private static int CountBuffUsers(GameTableIndex game, ExmodPackage? mod, string consumableFile, string buff)
    {
        var buffByRow = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        return EffectiveLinks(game, mod, "Consumable").Count(row => row is not null
            && string.Equals(buffByRow.TryGetValue(row, out var known) ? known : buffByRow[row] = BuffOf(game, mod, consumableFile, row), buff, StringComparison.OrdinalIgnoreCase));
    }

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

    private static string Show(JsonNode? value) =>
        value is JsonValue v && v.TryGetValue<string>(out var s) ? (NsLocText.TextOf(s) ?? s) : value?.ToJsonString() ?? "(none)";

    /// <summary>"Max_Durability" → "Max durability"; "OxygenSlots" → "Oxygen slots".</summary>
    private static string Humanize(string name)
    {
        var words = CamelBoundary().Replace(name.Replace('_', ' '), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var text = string.Join(' ', words.Select((w, i) => i == 0 ? w : (w.Length > 1 && w.All(char.IsUpper) ? w : w.ToLowerInvariant())));
        return text.Length == 0 ? name : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
