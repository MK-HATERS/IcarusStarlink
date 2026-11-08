using System.Text.Json.Nodes;

namespace IcarusStarlink.PakIO.Exmod;

/// <summary>
/// Checks from what the game is known to do (KnownGameBehaviour: tested in game): rows added where the game can't use them, stats it ignores, and new items nothing hands out.
/// </summary>
public static partial class ModHealthChecker
{
    private const string ItemsStaticFile = "Items-D_ItemsStatic.json";
    private const string ItemTemplateTable = "D_ItemTemplate";

    /// <param name="checkNewItems">False for a mod converted from a prebuilt pak: its whole old tables make old game items look new.</param>
    /// <param name="rowsOtherModsAdd">Rows other Library mods add (AddedRowKey): a template another mod adds counts.</param>
    /// <param name="rowHistory">Every row the game has had: an item it had isn't the mod's new one.</param>
    private static IEnumerable<ModIssue> BehaviourIssues(ExmodPackage package, GameTableIndex game, IReadOnlyList<KnownGameBehaviour.LoggedMismatch>? logged,
        bool checkNewItems, IReadOnlySet<string>? rowsOtherModsAdd, GameRowHistory? rowHistory)
    {
        var rows = package.Rows.Where(r => !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile)).ToList();
        foreach (var row in rows)
        {
            var table = GameTableIndex.TableNameOf(row.CurrentFile);

            // Rows added to a table the game has built into its code, where it can't use them.
            if (KnownGameBehaviour.BuiltInTables.TryGetValue(table, out var builtIn) && builtIn.NewRowsBreak)
            {
                var said = logged?.FirstOrDefault(l => string.Equals(l.Table, table, StringComparison.OrdinalIgnoreCase));
                foreach (var item in ExmodFileItem.CombineByName(row.FileItems).Where(i => !game.HasRow(row.CurrentFile, i.Name)))
                {
                    yield return new ModIssue(ModIssueKind.RowsInBuiltInTable, row.CurrentFile, item.Name, null, null, null, null, null,
                        ModIssueFix.KeepMod, IsSure: false,
                        builtIn.What + (said is null ? "" : $" The game's log says so too at its last start: {said.Loaded} rows loaded, {said.BuiltIn} built in."));
                }
            }

            // Stats the game ignores where they are.
            foreach (var item in ExmodFileItem.CombineByName(row.FileItems))
            {
                foreach (var (field, value) in item.Fields)
                {
                    if (value is not JsonObject stats || stats.Count == 0 || !stats.All(kv => GameStats.IsStatKey(kv.Key)))
                    {
                        continue;
                    }

                    var placed = string.Equals(table, "D_ItemsStatic", StringComparison.OrdinalIgnoreCase) && IsPlacedItem(item, game.Row(row.CurrentFile, item.Name));
                    foreach (var (key, amount) in stats)
                    {
                        if (amount is JsonValue v && ScaleRules.TryReadNumber(v, out var n) && n != 0
                            && KnownGameBehaviour.StatWithNoEffect(table, field, GameStats.StatNameOf(key), placed) is { } why)
                        {
                            yield return new ModIssue(ModIssueKind.NoEffect, row.CurrentFile, item.Name, field, new JsonObject { [key] = amount.DeepClone() }, null, null, null,
                                ModIssueFix.KeepMod, IsSure: false, why);
                        }
                    }
                }
            }
        }

        // New items: something has to hand them out.
        var newItems = rows.Where(r => string.Equals(r.CurrentFile, ItemsStaticFile, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => ExmodFileItem.CombineByName(r.FileItems)).Where(i => !game.HasRow(ItemsStaticFile, i.Name)).ToList();
        if (!checkNewItems || newItems.Count == 0)
        {
            yield break;
        }

        var templates = TemplatesByItem(rows);
        var handedOut = HandedOutNames(rows);
        foreach (var item in newItems)
        {
            // An item some game version had is an old copy of the game's (laanp's paks carry removed
            // resource packs), not new content of the mod's: what hands it out was the game's.
            if (rowHistory?.Find(ItemsStaticFile, item.Name) is not null)
            {
                continue;
            }

            var itsTemplates = templates.GetValueOrDefault(item.Name) ?? [];
            if (itsTemplates.Count == 0 && rowsOtherModsAdd?.Contains(AddedRowKey("Items-D_ItemTemplate.json", item.Name)) == true)
            {
                continue; // another Library mod adds its template (and most likely what hands it out)
            }

            if (itsTemplates.Count == 0)
            {
                yield return new ModIssue(ModIssueKind.NothingGivesItem, ItemsStaticFile, item.Name, null, null, null, null, null,
                    ModIssueFix.KeepMod, IsSure: false,
                    "This new item has no item template (a D_ItemTemplate row naming it), and players only ever hold an item through its template: "
                    + "recipes, the workshop and loot all hand out templates. As it is, nobody can get it.");
            }
            else if (!itsTemplates.Any(handedOut.Contains) && !handedOut.Contains(item.Name))
            {
                yield return new ModIssue(ModIssueKind.NothingGivesItem, ItemsStaticFile, item.Name, null, null, null, null, null,
                    ModIssueFix.KeepMod, IsSure: false,
                    $"Nothing in this mod hands it out: no recipe, workshop item, reward or loot names its template ({string.Join(", ", itsTemplates)}). "
                    + "Unless another mod does, players can't get it.");
            }
        }
    }

    private const string DurableTable = "D_Durable";
    private const string SeedsTable = "D_FarmingSeeds";

    /// <summary>
    /// Notes from the game's code, not tested in game (KnownGameBehaviour.FromGameCode): repair
    /// needs the mod changes on durability rows that placed items or building pieces use (a hammer
    /// repair ignores them), and seed rows it adds (the game numbers seeds by place). Kept out of
    /// Check: they are things to know, never problems.
    /// </summary>
    public static IReadOnlyList<ModIssue> GameCodeNotes(ExmodPackage package, GameTableIndex game)
    {
        var notes = new List<ModIssue>();
        var rows = package.Rows.Where(r => !ExmodSentinelFiles.IsEndOfModMarker(r.CurrentFile)).ToList();
        HashSet<string>? placedDurables = null;
        foreach (var row in rows)
        {
            var table = GameTableIndex.TableNameOf(row.CurrentFile);
            if (string.Equals(table, SeedsTable, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in ExmodFileItem.CombineByName(row.FileItems).Where(i => !game.HasRow(row.CurrentFile, i.Name)))
                {
                    notes.Add(new ModIssue(ModIssueKind.GameCodeNote, row.CurrentFile, item.Name, null, null, null, null, null,
                        ModIssueFix.KeepMod, IsSure: false, KnownGameBehaviour.SeedNumbers.Note));
                }
            }
            else if (string.Equals(table, DurableTable, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var item in ExmodFileItem.CombineByName(row.FileItems))
                {
                    var field = item.Fields.Keys.FirstOrDefault(f => string.Equals(f, KnownGameBehaviour.HammerRepairs.Field, StringComparison.OrdinalIgnoreCase));
                    if (field is null || JsonNode.DeepEquals(item.Fields[field], game.Row(row.CurrentFile, item.Name)?[KnownGameBehaviour.HammerRepairs.Field!]))
                    {
                        continue;
                    }

                    placedDurables ??= DurablesOfPlacedThings(rows, game);
                    if (placedDurables.Contains(item.Name))
                    {
                        notes.Add(new ModIssue(ModIssueKind.GameCodeNote, row.CurrentFile, item.Name, field, item.Fields[field]?.DeepClone(), null, null, null,
                            ModIssueFix.KeepMod, IsSure: false, KnownGameBehaviour.HammerRepairs.Note));
                    }
                }
            }
        }

        return notes;
    }

    /// <summary>
    /// The durability rows (D_Durable) of items placed in the world or built (a Deployable or
    /// Buildable link): the game's items, with the mod's own D_ItemsStatic rows over them.
    /// </summary>
    private static HashSet<string> DurablesOfPlacedThings(IReadOnlyList<ExmodFileRow> rows, GameTableIndex game)
    {
        var items = new Dictionary<string, (JsonNode? Durable, bool Placed)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in game.Rows(ItemsStaticFile) ?? [])
        {
            if (value is JsonObject item)
            {
                items[name] = (item["Durable"], RowLinks.LinkedRow(item["Deployable"]) is not null || RowLinks.LinkedRow(item["Buildable"]) is not null);
            }
        }

        foreach (var item in rows.Where(r => string.Equals(r.CurrentFile, ItemsStaticFile, StringComparison.OrdinalIgnoreCase))
            .SelectMany(r => ExmodFileItem.CombineByName(r.FileItems)))
        {
            var gameRow = game.Row(ItemsStaticFile, item.Name);
            JsonNode? Field(string name) => item.Fields.TryGetValue(name, out var mine) ? mine : gameRow?[name];
            items[item.Name] = (Field("Durable"), RowLinks.LinkedRow(Field("Deployable")) is not null || RowLinks.LinkedRow(Field("Buildable")) is not null);
        }

        return items.Values.Where(i => i.Placed).Select(i => RowLinks.LinkedRow(i.Durable)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The item is placed in the world: it has a Deployable row (the mod's link, else the game's).</summary>
    private static bool IsPlacedItem(ExmodFileItem item, JsonObject? gameRow) =>
        RowLinks.LinkedRow(item.Fields.TryGetValue("Deployable", out var mine) ? mine : gameRow?["Deployable"]) is not null;

    /// <summary>The mod's item templates by the item they make (ItemStaticData), each template's own name.</summary>
    private static Dictionary<string, List<string>> TemplatesByItem(IEnumerable<ExmodFileRow> rows)
    {
        var templates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => string.Equals(GameTableIndex.TableNameOf(r.CurrentFile), ItemTemplateTable, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var template in row.FileItems)
            {
                var item = RowLinks.LinkedRow(template.Fields.GetValueOrDefault("ItemStaticData")) ?? template.Name;
                if (!templates.TryGetValue(item, out var list))
                {
                    templates[item] = list = [];
                }

                list.Add(template.Name);
            }
        }

        return templates;
    }

    /// <summary>Every row name the mod's other rows link to (a recipe's outputs, a workshop item, a reward…): what it hands out, among other things.</summary>
    private static HashSet<string> HandedOutNames(IEnumerable<ExmodFileRow> rows)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => !string.Equals(r.CurrentFile, ItemsStaticFile, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(GameTableIndex.TableNameOf(r.CurrentFile), ItemTemplateTable, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var item in row.FileItems)
            {
                foreach (var value in item.Fields.Values)
                {
                    RowLinks.CollectRowNames(value, names);
                }
            }
        }

        return names;
    }
}
