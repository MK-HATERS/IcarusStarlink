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

    /// <summary>The item is placed in the world: it has a Deployable row (the mod's link, else the game's).</summary>
    private static bool IsPlacedItem(ExmodFileItem item, JsonObject? gameRow) =>
        LinkedRow(item.Fields.TryGetValue("Deployable", out var mine) ? mine : gameRow?["Deployable"]) is not null;

    /// <summary>The row a link names, or null for none ("None", not a link).</summary>
    private static string? LinkedRow(JsonNode? value) =>
        (value as JsonObject)?["RowName"] is JsonValue v && v.TryGetValue<string>(out var name) && name is { Length: > 0 } and not "None" ? name : null;

    /// <summary>The mod's item templates by the item they make (ItemStaticData), each template's own name.</summary>
    private static Dictionary<string, List<string>> TemplatesByItem(IEnumerable<ExmodFileRow> rows)
    {
        var templates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Where(r => string.Equals(GameTableIndex.TableNameOf(r.CurrentFile), ItemTemplateTable, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var template in row.FileItems)
            {
                var item = LinkedRow(template.Fields.GetValueOrDefault("ItemStaticData")) ?? template.Name;
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
                    CollectLinks(value, names);
                }
            }
        }

        return names;
    }
}
