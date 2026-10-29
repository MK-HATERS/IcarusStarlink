using System.IO;
using System.Text.Json;

namespace IcarusStarlink.App.Services;

public sealed partial class SaveGameNames
{
    // --- Talents ---

    /// <summary>What each talent needs and gives (D_Talents RequiredFlags, RequiredTalents, Rewards[].GrantedFlags), for the save check.</summary>
    public IReadOnlyDictionary<string, TalentRule> TalentRules => Cached(ref _talentRules, LoadTalentRules);

    private IReadOnlyDictionary<string, TalentRule> LoadTalentRules() => Safe<IReadOnlyDictionary<string, TalentRule>>(() =>
    {
        var rules = new Dictionary<string, TalentRule>(StringComparer.OrdinalIgnoreCase);
        var defaults = DefaultsOf(TalentsTable);
        foreach (var (name, row) in RowsOf(TalentsTable))
        {
            var requiredFlags = new List<(string Table, string Row)>();
            if (TryGet(row, defaults, "RequiredFlags", out var flags) && flags.ValueKind == JsonValueKind.Array)
            {
                foreach (var flag in flags.EnumerateArray())
                {
                    if (flag.ValueKind == JsonValueKind.Object && flag.TryGetProperty("RowName", out var r) && r.GetString() is { Length: > 0 } flagRow && flagRow != "None")
                    {
                        requiredFlags.Add((flag.TryGetProperty("DataTableName", out var t) ? t.GetString() ?? "" : "", flagRow));
                    }
                }
            }

            var granted = new List<IReadOnlyList<string>>();
            if (TryGet(row, defaults, "Rewards", out var rewards) && rewards.ValueKind == JsonValueKind.Array)
            {
                foreach (var reward in rewards.EnumerateArray())
                {
                    granted.Add([.. RowNames(reward, null, "GrantedFlags").Where(f => f != "None")]);
                }
            }

            var isReroute = TryGet(row, defaults, "TalentType", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "Reroute";
            var isDefault = TryGet(row, defaults, "bDefaultUnlocked", out var d) && d.ValueKind == JsonValueKind.True;
            rules.TryAdd(name, new TalentRule(requiredFlags, [.. RowNames(row, defaults, "RequiredTalents")], granted, isReroute || isDefault));
        }

        return rules;
    }, () => new Dictionary<string, TalentRule>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Flag row name -> the talents whose RequiredFlags name it (Caveworm Knife and Spear need
    /// GrantedBlueprint_CavewormMelee). A character can have learned those talents while the flag is
    /// off (found on the user's save: the game then shows them as unlocked), so the Unlocks tab says so.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TalentsGatedByFlag => Cached(ref _talentsGatedByFlag, LoadTalentsGatedByFlag);

    private IReadOnlyDictionary<string, IReadOnlyList<string>> LoadTalentsGatedByFlag() => Safe<IReadOnlyDictionary<string, IReadOnlyList<string>>>(() =>
    {
        var gated = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var defaults = DefaultsOf(TalentsTable);
        foreach (var (talent, row) in RowsOf(TalentsTable))
        {
            foreach (var flag in RowNames(row, defaults, "RequiredFlags"))
            {
                (gated.TryGetValue(flag, out var list) ? list : gated[flag] = []).Add(talent);
            }
        }

        return gated.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }, () => new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Mission (a profile Prospect_* talent) -> the account flags finishing it rewards: the talent's
    /// ExtraData names its D_ProspectList row, and D_AccountFlags' RewardedFromMissions names the
    /// same rows. 53 of the game's 88 mission rewards link this way (2026-10-23); the other 35 come
    /// from Great Hunt missions, which the save doesn't keep as Prospect_* talents.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MissionRewardFlags => Cached(ref _missionRewardFlags, LoadMissionRewardFlags);

    private IReadOnlyDictionary<string, IReadOnlyList<string>> LoadMissionRewardFlags() => Safe<IReadOnlyDictionary<string, IReadOnlyList<string>>>(() =>
    {
        var missionsOfProspect = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (talent, row) in RowsOf(TalentsTable))
        {
            if (talent.StartsWith("Prospect_", StringComparison.OrdinalIgnoreCase)
                && row.TryGetProperty("ExtraData", out var extra) && extra.ValueKind == JsonValueKind.Object
                && extra.TryGetProperty("RowName", out var r) && r.GetString() is { } prospect)
            {
                (missionsOfProspect.TryGetValue(prospect, out var list) ? list : missionsOfProspect[prospect] = []).Add(talent);
            }
        }

        var rewards = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var defaults = DefaultsOf(AccountFlagsTable);
        foreach (var (flag, row) in RowsOf(AccountFlagsTable))
        {
            foreach (var prospect in RowNames(row, defaults, "RewardedFromMissions"))
            {
                foreach (var mission in missionsOfProspect.GetValueOrDefault(prospect, []))
                {
                    (rewards.TryGetValue(mission, out var list) ? list : rewards[mission] = []).Add(flag);
                }
            }
        }

        return rewards.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
    }, () => new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Every talent tree: its own name ("Resources"), its archetype's ("Survival", "Tier 1",
    /// "Olympus", "Envirosuits") and the archetype's model — Player, Blueprint and Solo trees are a
    /// character's; Workshop research and Prospect (mission completions) are the account's.
    /// </summary>
    public IReadOnlyDictionary<string, TreeInfo> TalentTrees => Cached(ref _talentTrees, LoadTalentTrees);

    private IReadOnlyDictionary<string, TreeInfo> LoadTalentTrees() => Safe<IReadOnlyDictionary<string, TreeInfo>>(() =>
    {
        var trees = new Dictionary<string, TreeInfo>(StringComparer.OrdinalIgnoreCase);
        if (Table(TalentArchetypesTable) is null || Table(TalentTreesTable) is null)
        {
            return trees;
        }

        var archetypeDefaults = DefaultsOf(TalentArchetypesTable);
        var archetypes = new Dictionary<string, (string Display, string Model, int RequiredLevel, int Index)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, row) in RowsOf(TalentArchetypesTable))
        {
            var requiredLevel = TryGet(row, archetypeDefaults, "RequiredLevel", out var level) && level.ValueKind == JsonValueKind.Number && level.TryGetInt32(out var l) ? l : 0;
            archetypes.TryAdd(name, (ParseLocText(row, "DisplayName") ?? HumanizeMountType(name), RowNameOf(row, "Model") ?? "", requiredLevel, archetypes.Count));
        }

        // Blueprint trees have no name of their own (Blueprint_T2_Crafting); their archetype says
        // "Tier 2", and the bench comes from the recipes the tree's talents unlock (D_RecipeSets).
        var rows = RowsOf(TalentTreesTable).ToList();
        var benches = LoadBlueprintBenches(rows
            .Where(r => archetypes.TryGetValue(RowNameOf(r.Row, "Archetype") ?? "", out var a)
                ? a.Model.Equals("Blueprint", StringComparison.OrdinalIgnoreCase)
                : BlueprintTier(r.Name, "") is not null)
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase));

        var placed = new List<(string Name, TreeInfo Tree, (int, int, int, int, int) Key)>();
        foreach (var (name, row) in rows)
        {
            var archetype = RowNameOf(row, "Archetype") ?? "";
            var known = archetypes.TryGetValue(archetype, out var a);
            var archetypeDisplay = known ? a.Display : HumanizeMountType(archetype);
            var model = known ? a.Model : "";
            var tier = BlueprintTier(name, known ? archetype : "", known ? archetypeDisplay : "");
            var isBlueprint = known ? model.Equals("Blueprint", StringComparison.OrdinalIgnoreCase) : tier is not null;
            if (!known && tier is not null)
            {
                // A mod's tree whose archetype the tables lack: its name still says the tier.
                archetypeDisplay = $"Tier {tier}";
            }

            var display = ParseLocText(row, "DisplayName")
                ?? (isBlueprint ? benches.GetValueOrDefault(name) ?? (known ? null : BlueprintTreeWords(name)) ?? archetypeDisplay : HumanizeMountType(name));
            var key = (
                TreeModelRank(isBlueprint ? "Blueprint" : model),
                isBlueprint ? tier ?? int.MaxValue : 0,
                known ? a.RequiredLevel : int.MaxValue,
                known ? a.Index : int.MaxValue,
                placed.Count);
            placed.Add((name, new TreeInfo(display, archetypeDisplay, model, archetype), key));
        }

        var order = 0;
        foreach (var (name, tree, _) in placed.OrderBy(p => p.Key))
        {
            trees.TryAdd(name, tree with { Order = order++ });
        }

        return trees;
    }, () => new Dictionary<string, TreeInfo>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Talents, then blueprints, then solo (a character's); research, then missions (the account's); mounts and the rest after.</summary>
    private static int TreeModelRank(string model) => model.ToLowerInvariant() switch
    {
        "player" => 0,
        "blueprint" => 1,
        "solo" => 2,
        "workshop" => 3,
        "prospect" => 4,
        "" => 6,
        _ => 5,
    };

    /// <summary>
    /// A blueprint tree's tier: the archetype's own name ("Tier 2"), else the number in the tree's
    /// or archetype's row name (Blueprint_T2_Crafting, Blueprint_Tier5_Manufacturer). Null when
    /// neither says one.
    /// </summary>
    internal static int? BlueprintTier(string treeRow, string archetypeRow, string archetypeDisplay = "")
    {
        var shown = System.Text.RegularExpressions.Regex.Match(archetypeDisplay, @"^\s*Tier\s*(\d{1,3})\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (shown.Success)
        {
            return int.Parse(shown.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var row in new[] { treeRow, archetypeRow })
        {
            var match = System.Text.RegularExpressions.Regex.Match(row, @"^Blueprint_T(?:ier)?(\d{1,3})(?:_|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    /// <summary>"Blueprint_T6_Forge_Works" -> "Forge Works": what's left of a blueprint tree's row name after "Blueprint" and its tier. Null when nothing is.</summary>
    private static string? BlueprintTreeWords(string treeRow)
    {
        var words = TreeNameWords(treeRow).ToList();
        return words.Count == 0 ? null : HumanizeMountType(string.Join("_", words));
    }

    private static IEnumerable<string> TreeNameWords(string treeRow) =>
        treeRow.Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("Blueprint", StringComparison.OrdinalIgnoreCase)
                && !System.Text.RegularExpressions.Regex.IsMatch(w, @"^T(?:ier)?\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    /// <summary>
    /// Blueprint tree -> the bench its blueprints are for: the D_RecipeSets row that shares the
    /// tree name's word (Crafting -> Crafting_Bench, Machine -> Machining_Bench, Fabricator), else
    /// the bench most of the tree's recipes are made at (D_ProcessorRecipes Requirement ->
    /// RecipeSets: Tier 1's is "Character", made by hand). Empty when the tables are missing.
    /// </summary>
    private Dictionary<string, string> LoadBlueprintBenches(IReadOnlySet<string> blueprintTrees) => Safe(() =>
    {
        var benches = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (blueprintTrees.Count == 0)
        {
            return benches;
        }

        var setsTable = Path.Combine("Crafting", "D_RecipeSets.json");
        var setDefaults = DefaultsOf(setsTable);
        var sets = RowsOf(setsTable)
            .Select(r => (r.Name, Display: ParseLocText(r.Row, "RecipeSetName", setDefaults) ?? HumanizeMountType(r.Name)))
            .ToList();
        if (sets.Count == 0)
        {
            return benches;
        }

        var treeOfTalent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (talent, row) in RowsOf(TalentsTable))
        {
            if (RowNameOf(row, "TalentTree") is { } tree && blueprintTrees.Contains(tree))
            {
                treeOfTalent.TryAdd(talent, tree);
            }
        }

        var uses = new Dictionary<(string Tree, string Set), int>();
        var recipesTable = Path.Combine("Crafting", "D_ProcessorRecipes.json");
        var recipeDefaults = DefaultsOf(recipesTable);
        foreach (var (_, recipe) in RowsOf(recipesTable))
        {
            if (RowNameOf(recipe, "Requirement") is { } talent && treeOfTalent.TryGetValue(talent, out var tree))
            {
                foreach (var set in RowNames(recipe, recipeDefaults, "RecipeSets"))
                {
                    uses[(tree, set)] = uses.GetValueOrDefault((tree, set)) + 1;
                }
            }
        }

        foreach (var tree in blueprintTrees)
        {
            var words = TreeNameWords(tree).ToList();
            var scored = sets
                .Select(s => (s.Name, s.Display, Prefix: words.Count == 0 ? 0 : words.Max(w => CommonPrefixLength(w, s.Name)), Uses: uses.GetValueOrDefault((tree, s.Name))))
                .ToList();
            var named = scored.Where(s => s.Prefix >= 5).OrderByDescending(s => s.Prefix).ThenByDescending(s => s.Uses).FirstOrDefault();
            var bench = named.Name is not null ? named : scored.Where(s => s.Uses > 0).OrderByDescending(s => s.Uses).FirstOrDefault();
            if (bench.Name is not null)
            {
                // The game's "Character" set is crafting by hand, from the character's own menu.
                benches[tree] = bench.Name.Equals("Character", StringComparison.OrdinalIgnoreCase) ? $"{bench.Display} crafting" : bench.Display;
            }
        }

        return benches;
    }, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static int CommonPrefixLength(string a, string b)
    {
        var length = 0;
        while (length < a.Length && length < b.Length && char.ToLowerInvariant(a[length]) == char.ToLowerInvariant(b[length]))
        {
            length++;
        }

        return length;
    }

    private static readonly HashSet<string> CharacterTalentModels = new(StringComparer.OrdinalIgnoreCase) { "Player", "Blueprint", "Solo" };

    private IReadOnlySet<string> LoadCharacterTalentTrees() => Safe<IReadOnlySet<string>>(() =>
    {
        // Missing tables: no filter, every talent shows (the old behaviour).
        var characterArchetypes = RowsOf(TalentArchetypesTable)
            .Where(r => RowNameOf(r.Row, "Model") is { } model && CharacterTalentModels.Contains(model))
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RowsOf(TalentTreesTable)
            .Where(r => RowNameOf(r.Row, "Archetype") is { } archetype && characterArchetypes.Contains(archetype))
            .Select(r => r.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }, () => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private IReadOnlyDictionary<string, TalentInfo> LoadTalents() => Safe<IReadOnlyDictionary<string, TalentInfo>>(() =>
    {
        var talents = new Dictionary<string, TalentInfo>(StringComparer.OrdinalIgnoreCase);
        var defaults = DefaultsOf(TalentsTable);
        var workshopNames = WorkshopItemNames;
        var workshopCosts = LoadWorkshopCosts();
        var missionNames = MissionNames;
        var itemableNames = LoadItemableNames();
        var order = -1;
        foreach (var (talentName, row) in RowsOf(TalentsTable))
        {
            order++;
            // "Reroute" (92 rows) and "MutuallyExclusive" (6 rows) are invisible tree-plumbing
            // nodes — 0x0 size, no rewards, no display name — that route lines between real
            // talents in the game's own tree UI. They are not talents a player can have, so
            // they never belong in an editor's list. (A save that somehow carries one still
            // shows it, via the fallback path — nothing in the save is ever hidden.)
            if (TryGet(row, defaults, "TalentType", out var talentType) && talentType.ValueKind == JsonValueKind.String
                && talentType.GetString() is "Reroute" or "MutuallyExclusive")
            {
                continue;
            }

            var tree = row.TryGetProperty("TalentTree", out var treeRef) && treeRef.ValueKind == JsonValueKind.Object && treeRef.TryGetProperty("RowName", out var treeName)
                ? treeName.GetString() ?? ""
                : "";
            var maxRank = row.TryGetProperty("Rewards", out var rewards) && rewards.ValueKind == JsonValueKind.Array && rewards.GetArrayLength() > 0
                ? rewards.GetArrayLength()
                : 1;

            // 49 real talents (Stone Axe, basic stacks, ...) are unlocked from the start and
            // NEVER written into a save — without this bit the editor would show them "locked".
            var defaultUnlocked = TryGet(row, defaults, "bDefaultUnlocked", out var b) && b.ValueKind == JsonValueKind.True;

            // Workshop research rows carry no name of their own (all 329): the name is the
            // item they research, ExtraData -> D_WorkshopItems -> Item.
            // Mission completions (Prospect_*) are named after the mission, ExtraData -> D_ProspectList.
            var extraRowName = RowNameOf(row, "ExtraData");
            var workshopName = extraRowName is not null && workshopNames.TryGetValue(extraRowName, out var itemName) ? itemName : null;
            var missionName = extraRowName is not null && missionNames.TryGetValue(extraRowName, out var mission) ? mission : null;
            var cost = extraRowName is not null ? workshopCosts.GetValueOrDefault(extraRowName) : null;
            // Blueprint talents: ExtraData -> D_Itemable, the item they let you craft.
            var itemableName = extraRowName is not null ? itemableNames.GetValueOrDefault(extraRowName) : null;

            talents.TryAdd(talentName, new TalentInfo(
                ParseLocText(row, "DisplayName", defaults) ?? workshopName ?? missionName ?? itemableName ?? talentName,
                ParseLocText(row, "Description", defaults) ?? "",
                tree,
                maxRank,
                defaultUnlocked,
                cost,
                order));
        }

        return talents;
    }, () => new Dictionary<string, TalentInfo>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Character flag -> the talent whose rewards grant it (D_Talents Rewards[].GrantedFlags).</summary>
    private Dictionary<string, string> LoadTalentGrantedFlags() => Safe(() =>
    {
        // With the table's Defaults, like TalentRules: a row that leaves Rewards out takes the Defaults'.
        var grantedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaults = DefaultsOf(TalentsTable);
        foreach (var (name, row) in RowsOf(TalentsTable))
        {
            if (!TryGet(row, defaults, "Rewards", out var rewards) || rewards.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var talentName = ParseLocText(row, "DisplayName", defaults) ?? name;
            foreach (var reward in rewards.EnumerateArray())
            {
                foreach (var flag in RowNames(reward, null, "GrantedFlags"))
                {
                    grantedBy.TryAdd(flag, talentName);
                }
            }
        }

        return grantedBy;
    }, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Account flag -> the bonus talent points it gives ("GrantedTalent_Nullsector_Story" -> 3).</summary>
    public IReadOnlyDictionary<string, int> TalentPointModifiers => Cached(ref _talentPointModifiers, LoadTalentPointModifiers);

    /// <summary>Account flag -> the bonus talent points it gives (D_PlayerTalentModifiers' TalentPointModifier, keyed by its RequiredFlags).</summary>
    private IReadOnlyDictionary<string, int> LoadTalentPointModifiers() => Safe<IReadOnlyDictionary<string, int>>(() =>
    {
        var points = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var table = Path.Combine("Talents", "D_PlayerTalentModifiers.json");
        var defaults = DefaultsOf(table);
        foreach (var (_, row) in RowsOf(table, keepNameless: true))
        {
            var modifier = TryGet(row, defaults, "TalentPointModifier", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
            foreach (var flag in RowNames(row, defaults, "RequiredFlags"))
            {
                points[flag] = points.GetValueOrDefault(flag) + modifier;
            }
        }

        return points;
    }, () => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
}
