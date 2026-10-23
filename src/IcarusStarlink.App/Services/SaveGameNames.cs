using System.IO;
using System.Text.Json;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Names for the save editor, straight from the game's OWN extracted data tables: a flag int in a
/// save is a row INDEX into D_CharacterFlags (per character) or D_AccountFlags (per profile), and
/// a talent RowName groups under its tree via D_Talents. Loaded lazily from the app's Data folder
/// and tolerant of it being absent (fresh install, data not extracted yet) — everything degrades
/// to raw IDs/names rather than failing, since the save data itself is still perfectly editable.
/// Reading live from the user's own extraction (rather than shipping a hardcoded list) means the
/// names stay correct across game updates for free.
///
/// One instance is shared app-wide and read from more than one thread (the Saves page warms it
/// in the background, SaveModSources reads it in the background), so every lazy lookup is built
/// under one lock. Each table file is parsed at most once per <see cref="Warm"/> or lookup pass
/// (D_Talents, 2.7 MB, used to be parsed five times on every slot load).
/// </summary>
public sealed class SaveGameNames(string dataFolder)
{
    /// <summary>The extracted game data these names come from.</summary>
    public string DataFolder => dataFolder;

    private readonly Lock _gate = new();

    /// <summary>Each table read so far (null: missing or unreadable), until Warm or Invalidate drops them.</summary>
    private readonly Dictionary<string, GameTable?> _tables = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, IReadOnlySet<string>> _rowNameSets = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<string>? _characterFlagNames;
    private IReadOnlyList<string>? _accountFlagNames;
    private IReadOnlyDictionary<string, TalentInfo>? _talents;
    private IReadOnlySet<string>? _characterTalentTrees;
    private IReadOnlyDictionary<string, AccoladeInfo>? _accolades;
    private IReadOnlyDictionary<string, BestiaryCreatureInfo>? _bestiaryCreatures;
    private IReadOnlyDictionary<string, ItemInfo>? _items;
    private IReadOnlyList<string>? _mountTypeRowNames;
    private IReadOnlyDictionary<string, string>? _mountTypeIcons;
    private IReadOnlyList<string>? _dlcPackageNames;
    private IReadOnlyDictionary<string, string>? _dlcDisplayNames;
    private IReadOnlyDictionary<string, TreeInfo>? _talentTrees;
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _talentsGatedByFlag;
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _missionRewardFlags;
    private IReadOnlyDictionary<string, TalentRule>? _talentRules;
    private IReadOnlyList<FlagInfo>? _accountFlagInfos;
    private IReadOnlyList<FlagInfo>? _characterFlagInfos;
    private IReadOnlyDictionary<string, int>? _talentPointModifiers;
    private IReadOnlyList<(string Row, string Title)>? _geneticValues;
    private IReadOnlyList<(string Row, string Title)>? _lineages;
    private IReadOnlyDictionary<string, string>? _templateStaticRows;
    private IReadOnlyDictionary<string, string>? _workshopItemNames;
    private IReadOnlyDictionary<string, string>? _missionNames;

    private static readonly string TalentsTable = Path.Combine("Talents", "D_Talents.json");
    private static readonly string TalentTreesTable = Path.Combine("Talents", "D_TalentTrees.json");
    private static readonly string TalentArchetypesTable = Path.Combine("Talents", "D_TalentArchetypes.json");
    private static readonly string AccountFlagsTable = Path.Combine("Flags", "D_AccountFlags.json");
    private static readonly string CharacterFlagsTable = Path.Combine("Flags", "D_CharacterFlags.json");
    private static readonly string MountsTable = Path.Combine("AI", "D_Mounts.json");
    private static readonly string DlcTable = Path.Combine("DLC", "D_DLCPackageData.json");
    private static readonly string ItemTemplateTable = Path.Combine("Items", "D_ItemTemplate.json");
    private static readonly string ItemsStaticTable = Path.Combine("Items", "D_ItemsStatic.json");
    private static readonly string ItemableTable = Path.Combine("Traits", "D_Itemable.json");
    private static readonly string WorkshopItemsTable = Path.Combine("MetaWorkshop", "D_WorkshopItems.json");

    /// <summary>A lookup built once under the lock: two threads asking at once used to be able to both build it (or read half of one).</summary>
    private T Cached<T>(ref T? field, Func<T> load)
        where T : class
    {
        lock (_gate)
        {
            return field ??= load();
        }
    }

    public IReadOnlyList<string> CharacterFlagNames => Cached(ref _characterFlagNames, () => LoadRowNames(CharacterFlagsTable));

    public IReadOnlyList<string> AccountFlagNames => Cached(ref _accountFlagNames, () => LoadRowNames(AccountFlagsTable));

    /// <summary>Everything the save editor wants to know about each talent, keyed by the RowName the save stores. MaxRank comes from the row's own Rewards tier count (a reward-less row — blueprint/workshop unlocks, 1312 of 2227 — is a single-rank unlock).</summary>
    public IReadOnlyDictionary<string, TalentInfo> Talents => Cached(ref _talents, LoadTalents);

    /// <summary>
    /// Talent trees a CHARACTER learns in: D_TalentTrees' Archetype → D_TalentArchetypes' Model
    /// "Player" (Survival, Combat, ...), "Blueprint" (the blueprint trees) or "Solo". Checked
    /// against a real 1,000-talent character: every talent it had that the game still defines sat
    /// in one of these. The other models (Workshop, Prospect, Creature, Great_Hunt, Outpost) belong
    /// to the account or to mounts, so offering them on a character would write talents the game
    /// never reads there. Empty when the tables aren't extracted (then nothing is filtered).
    /// </summary>
    public IReadOnlySet<string> CharacterTalentTrees => Cached(ref _characterTalentTrees, LoadCharacterTalentTrees);

    /// <summary>Every accolade, keyed by the RowName Accolades.json's CompletedAccolades[].Accolade.RowName stores.</summary>
    public IReadOnlyDictionary<string, AccoladeInfo> Accolades => Cached(ref _accolades, LoadAccolades);

    /// <summary>Every trackable creature, keyed by the RowName BestiaryData.json's BestiaryTracking[].BestiaryGroup.RowName stores.</summary>
    public IReadOnlyDictionary<string, BestiaryCreatureInfo> BestiaryCreatures => Cached(ref _bestiaryCreatures, LoadBestiaryCreatures);

    /// <summary>Every item, keyed by the RowName MetaInventory.json's Items[].ItemStaticData.RowName stores (a D_ItemsStatic row). D_ItemsStatic itself carries no display name — see LoadItems' own comment for the real two-hop chain this resolves.</summary>
    public IReadOnlyDictionary<string, ItemInfo> Items => Cached(ref _items, LoadItems);

    /// <summary>Every real, valid Mounts.json MountType value — D_Mounts's own row names (37 on a current extraction), confirmed to be exactly what MountType stores. Used to offer only real choices in a picker rather than free text, the same way a save's own binary flags/talent ranks are already bounded by real game data.</summary>
    public IReadOnlyList<string> MountTypeRowNames => Cached(ref _mountTypeRowNames, () => LoadRowNames(MountsTable));

    /// <summary>D_Mounts's own "Icon" field per row (a raw "/Game/…/T_Talent_Base_Xxx.T_Talent_Base_Xxx" texture reference, the same self-named-asset shape D_Itemable's own Icon/D_BestiaryData's own Image use), keyed by RowName — what a saved mount's own MountType resolves to for its thumbnail.</summary>
    public IReadOnlyDictionary<string, string> MountTypeIcons => Cached(ref _mountTypeIcons, LoadMountTypeIcons);

    /// <summary>
    /// Forgets every table read so far, so the next lookup reads the data folder again. Called
    /// after Refresh game data — without it, names (or the empty lists from before the data was
    /// first extracted) stayed cached until the app restarted.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            // Dropped, not disposed: a background reader may still be walking one.
            _tables.Clear();
            _rowNameSets.Clear();
            _characterFlagNames = null;
            _accountFlagNames = null;
            _talents = null;
            _characterTalentTrees = null;
            _accolades = null;
            _bestiaryCreatures = null;
            _items = null;
            _mountTypeRowNames = null;
            _mountTypeIcons = null;
            _dlcPackageNames = null;
            _dlcDisplayNames = null;
            _accountFlagInfos = null;
            _characterFlagInfos = null;
            _talentTrees = null;
            _talentsGatedByFlag = null;
            _missionRewardFlags = null;
            _talentRules = null;
            _talentPointModifiers = null;
            _geneticValues = null;
            _lineages = null;
            _templateStaticRows = null;
            _workshopItemNames = null;
            _missionNames = null;
        }
    }

    /// <summary>
    /// Builds every lookup now, then lets go of the parsed tables (every lookup holds what it
    /// needs). Call off the UI thread: the Saves page does before it builds its rows, which used
    /// to fill these one by one on the UI thread.
    /// </summary>
    public void Warm()
    {
        lock (_gate)
        {
            _ = CharacterFlagNames;
            _ = AccountFlagNames;
            _ = Talents;
            _ = CharacterTalentTrees;
            _ = Accolades;
            _ = BestiaryCreatures;
            _ = Items;
            _ = MountTypeRowNames;
            _ = MountTypeIcons;
            _ = DlcPackageNames;
            _ = DlcDisplayNames;
            _ = TalentTrees;
            _ = TalentsGatedByFlag;
            _ = MissionRewardFlags;
            _ = TalentRules;
            _ = AccountFlagInfos;
            _ = CharacterFlagInfos;
            _ = TalentPointModifiers;
            _ = GeneticValues;
            _ = Lineages;
            _ = TemplateStaticRows;
            _ = RowNameSet(TalentsTable);
            _ = RowNameSet(ItemsStaticTable);
            _tables.Clear();
        }
    }

    // --- The table reader every lookup goes through ---

    /// <summary>A parsed table and its Defaults row (what a row that leaves a field out gets).</summary>
    private sealed record GameTable(JsonDocument Document, JsonElement? Defaults)
    {
        public JsonElement Rows => Document.RootElement.GetProperty("Rows");
    }

    /// <summary>The table at a path under the data folder, parsed once; null when it's missing or unreadable.</summary>
    private GameTable? Table(string relativePath)
    {
        lock (_gate)
        {
            if (_tables.TryGetValue(relativePath, out var cached))
            {
                return cached;
            }

            GameTable? table = null;
            try
            {
                var path = Path.Combine(dataFolder, relativePath);
                if (File.Exists(path))
                {
                    var document = JsonDocument.Parse(File.ReadAllText(path));
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.TryGetProperty("Rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
                    {
                        table = new GameTable(document, DefaultsOf(document));
                    }
                    else
                    {
                        document.Dispose();
                    }
                }
            }
            catch (Exception)
            {
                // Missing/corrupt table: every lookup built from it degrades to raw names.
            }

            _tables[relativePath] = table;
            return table;
        }
    }

    /// <summary>
    /// Every row of a table with its Name, in table order. A row without a Name is skipped, or
    /// named "?" with <paramref name="keepNameless"/> — the flag tables are indexed by position,
    /// so dropping one there would shift every flag after it.
    /// </summary>
    private IEnumerable<(string Name, JsonElement Row)> RowsOf(string relativePath, bool keepNameless = false)
    {
        if (Table(relativePath) is not { } table)
        {
            yield break;
        }

        foreach (var row in table.Rows.EnumerateArray())
        {
            if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { } name)
            {
                yield return (name, row);
            }
            else if (keepNameless)
            {
                yield return ("?", row);
            }
        }
    }

    private JsonElement? DefaultsOf(string relativePath) => Table(relativePath)?.Defaults;

    /// <summary>
    /// A lookup built from the tables, or an empty one when a table's shape surprises it (a game
    /// update): the same "degrade, never fail" rule every loader here had its own try/catch for.
    /// </summary>
    private static T Safe<T>(Func<T> build, Func<T> empty)
    {
        try
        {
            return build();
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException or JsonException)
        {
            return empty();
        }
    }

    /// <summary>
    /// The row names of a table under the data folder ("Talents/D_Talents.json"), for telling the
    /// game's own rows from a mod's (SaveModSources, on a background thread). Empty when the
    /// table is missing.
    /// </summary>
    public IReadOnlySet<string> RowNameSet(string relativePath)
    {
        var key = relativePath.Replace('/', Path.DirectorySeparatorChar);
        lock (_gate)
        {
            if (!_rowNameSets.TryGetValue(key, out var set))
            {
                set = Safe<IReadOnlySet<string>>(
                    () => RowsOf(key).Select(r => r.Name).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase),
                    () => new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                _rowNameSets[key] = set;
            }

            return set;
        }
    }

    /// <summary>D_ItemTemplate row -> the D_ItemsStatic row it uses ("LegendaryWeapon_ApeClub" -> "LegendaryWeapon_Ape_Club").</summary>
    public IReadOnlyDictionary<string, string> TemplateStaticRows => Cached(ref _templateStaticRows, () => Safe<IReadOnlyDictionary<string, string>>(() =>
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, row) in RowsOf(ItemTemplateTable))
        {
            if (row.TryGetProperty("ItemStaticData", out var st) && st.ValueKind == JsonValueKind.Object
                && st.TryGetProperty("RowName", out var r) && r.GetString() is { Length: > 0 } staticRow)
            {
                map[name] = staticRow;
            }
        }

        return map;
    }, () => new Dictionary<string, string>()));

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
        foreach (var (talent, row) in RowsOf(TalentsTable))
        {
            foreach (var flag in RowNames(row, null, "RequiredFlags"))
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

        var archetypes = new Dictionary<string, (string Display, string Model)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, row) in RowsOf(TalentArchetypesTable))
        {
            archetypes[name] = (ParseLocText(row, "DisplayName") ?? HumanizeMountType(name), RowNameOf(row, "Model") ?? "");
        }

        foreach (var (name, row) in RowsOf(TalentTreesTable))
        {
            var archetype = RowNameOf(row, "Archetype") ?? "";
            var (archetypeDisplay, model) = archetypes.GetValueOrDefault(archetype, (HumanizeMountType(archetype), ""));
            trees[name] = new TreeInfo(ParseLocText(row, "DisplayName") ?? HumanizeMountType(name), archetypeDisplay, model, archetype);
        }

        return trees;
    }, () => new Dictionary<string, TreeInfo>(StringComparer.OrdinalIgnoreCase));

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
        foreach (var (talentName, row) in RowsOf(TalentsTable))
        {
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
                cost));
        }

        return talents;
    }, () => new Dictionary<string, TalentInfo>(StringComparer.OrdinalIgnoreCase));

    /// <summary>Character flag -> the talent whose rewards grant it (D_Talents Rewards[].GrantedFlags).</summary>
    private Dictionary<string, string> LoadTalentGrantedFlags() => Safe(() =>
    {
        var grantedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, row) in RowsOf(TalentsTable))
        {
            if (!row.TryGetProperty("Rewards", out var rewards) || rewards.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var talentName = ParseLocText(row, "DisplayName") ?? name;
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
        foreach (var (_, row) in RowsOf(Path.Combine("Talents", "D_PlayerTalentModifiers.json"), keepNameless: true))
        {
            var modifier = row.TryGetProperty("TalentPointModifier", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
            foreach (var flag in RowNames(row, null, "RequiredFlags"))
            {
                points[flag] = points.GetValueOrDefault(flag) + modifier;
            }
        }

        return points;
    }, () => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

    // --- Flags ---

    /// <summary>What each account flag is for (by row index), from D_AccountFlags' own fields — researched 2026-10-23.</summary>
    public IReadOnlyList<FlagInfo> AccountFlagInfos => Cached(ref _accountFlagInfos, LoadAccountFlagInfos);

    /// <summary>What each character flag is for (by row index): every one turned out to be set by a talent, deprecated, tooltip-only, test or never granted — researched 2026-10-23.</summary>
    public IReadOnlyList<FlagInfo> CharacterFlagInfos => Cached(ref _characterFlagInfos, LoadCharacterFlagInfos);

    public FlagInfo AccountFlagInfo(int id) => id >= 0 && id < AccountFlagInfos.Count ? AccountFlagInfos[id] : new FlagInfo($"Flag {id}", $"Flag {id}", "Not in the game's flag table (from a mod, or removed).", FlagKind.Unknown);

    public FlagInfo CharacterFlagInfo(int id) => id >= 0 && id < CharacterFlagInfos.Count ? CharacterFlagInfos[id] : new FlagInfo($"Flag {id}", $"Flag {id}", "Not in the game's flag table (from a mod, or removed).", FlagKind.Unknown);

    public string CharacterFlagName(int id) => id >= 0 && id < CharacterFlagNames.Count ? CharacterFlagNames[id] : $"Flag {id}";

    public string AccountFlagName(int id) => id >= 0 && id < AccountFlagNames.Count ? AccountFlagNames[id] : $"Flag {id}";

    private static readonly string[] FlagPrefixes = ["GrantedBlueprint_", "GrantedWorkshop_", "GrantedTalent_", "Talent_", "Mission_"];

    private static string FlagDisplay(string rowName)
    {
        foreach (var prefix in FlagPrefixes)
        {
            if (rowName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && rowName.Length > prefix.Length)
            {
                return HumanizeMountType(rowName[prefix.Length..]);
            }
        }

        return HumanizeMountType(rowName);
    }

    private IReadOnlyList<FlagInfo> LoadAccountFlagInfos() => Safe<IReadOnlyList<FlagInfo>>(() =>
    {
        var result = new List<FlagInfo>();
        if (Table(AccountFlagsTable) is not { } table)
        {
            return result;
        }

        var missions = MissionNames;
        var talentPoints = TalentPointModifiers;
        var templateNames = LoadItemTemplateNames();
        var workshopNames = WorkshopItemNames;
        var defaults = table.Defaults;
        foreach (var (name, row) in RowsOf(AccountFlagsTable, keepNameless: true))
        {
            var unlocks = RowNames(row, defaults, "BlueprintUnlocks").Select(r => templateNames.GetValueOrDefault(r, HumanizeMountType(r)))
                .Concat(RowNames(row, defaults, "WorkshopUnlocks").Select(r => workshopNames.GetValueOrDefault(r, HumanizeMountType(r))))
                .Distinct()
                .ToList();
            var from = RowNames(row, defaults, "RewardedFromMissions").Select(r => missions.GetValueOrDefault(r, HumanizeMountType(r))).Distinct().ToList();
            var fromText = from.Count == 0 ? "" : $" · get it by finishing the mission {string.Join(" or ", from)}";
            var points = talentPoints.GetValueOrDefault(name);

            FlagInfo info;
            if (points != 0)
            {
                info = new FlagInfo(name, FlagDisplay(name), $"+{points} talent points{fromText}", FlagKind.TalentPoints);
            }
            else if (unlocks.Count > 0)
            {
                info = new FlagInfo(name, FlagDisplay(name), $"Unlocks {string.Join(", ", unlocks)}{fromText}", FlagKind.Reward);
            }
            else if (name.StartsWith("Has_Seen", StringComparison.OrdinalIgnoreCase))
            {
                info = new FlagInfo(name, FlagDisplay(name), "A pop-up the game has already shown. Turning it off shows it again.", FlagKind.Prompt);
            }
            else
            {
                info = new FlagInfo(name, FlagDisplay(name), "A one-off marker the game sets itself (a level boost claimed, a refund done, an upgrade). Changing it may make the game repeat or skip it.", FlagKind.Marker);
            }

            result.Add(info);
        }

        return result;
    }, () => []);

    private IReadOnlyList<FlagInfo> LoadCharacterFlagInfos() => Safe<IReadOnlyList<FlagInfo>>(() =>
    {
        var result = new List<FlagInfo>();
        if (Table(CharacterFlagsTable) is not { } table)
        {
            return result;
        }

        var grantedBy = LoadTalentGrantedFlags();
        var defaults = table.Defaults;
        foreach (var (name, row) in RowsOf(CharacterFlagsTable, keepNameless: true))
        {
            var description = ParseLocText(row, "Description", defaults);
            var deprecated = row.TryGetProperty("Metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("bIsDeprecated", out var dep) && dep.ValueKind == JsonValueKind.True;

            var info = grantedBy.TryGetValue(name, out var talent)
                ? new FlagInfo(name, FlagDisplay(name), $"From the talent {talent}: learning or unlearning the talent sets it.", FlagKind.FromTalent)
                : deprecated
                    ? new FlagInfo(name, FlagDisplay(name), "No longer used by the game (deprecated).", FlagKind.Unused)
                    : name.Contains("TooltipOnly", StringComparison.OrdinalIgnoreCase)
                        ? new FlagInfo(name, FlagDisplay(name), (description is { Length: > 0 } d ? d + ". " : "") + "Only shown in a mission's reward tooltip; nothing in the game reads it.", FlagKind.Unused)
                        : name is "TestDataFlag" or "TutorialTestFlag" or "RefundOnly_Repairable"
                            ? new FlagInfo(name, FlagDisplay(name), "A test or internal flag; nothing a player uses.", FlagKind.Unused)
                            : name.StartsWith("Talent_", StringComparison.OrdinalIgnoreCase)
                                ? new FlagInfo(name, FlagDisplay(name), "No talent grants this any more.", FlagKind.Unused)
                                : new FlagInfo(name, FlagDisplay(name), description is { Length: > 0 } text ? text : "Set by the game itself (a story step or an upgrade).", FlagKind.Marker);
            result.Add(info);
        }

        return result;
    }, () => []);

    /// <summary>D_ProspectList row -> its drop name ("ARCWOOD: Outpost"), for mission names.</summary>
    private IReadOnlyDictionary<string, string> MissionNames => Cached(ref _missionNames, () => Safe<IReadOnlyDictionary<string, string>>(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, row) in RowsOf(Path.Combine("Prospects", "D_ProspectList.json")))
        {
            if (ParseLocText(row, "DropName") is { Length: > 0 } drop)
            {
                names[rowName] = drop;
            }
        }

        return names;
    }, () => new Dictionary<string, string>()));

    // --- Items and workshop ---

    /// <summary>D_Itemable row -> its display name: blueprint talents name their item this way (ExtraData -> D_Itemable), "Caveworm_Knife" -> "Caveworm Knife".</summary>
    private Dictionary<string, string> LoadItemableNames() => Safe(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaults = DefaultsOf(ItemableTable);
        foreach (var (rowName, row) in RowsOf(ItemableTable))
        {
            if (ParseLocText(row, "DisplayName", defaults) is { Length: > 0 } display)
            {
                names[rowName] = display;
            }
        }

        return names;
    }, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>D_ItemTemplate row -> the item's display name (via D_ItemsStatic -> D_Itemable).</summary>
    private Dictionary<string, string> LoadItemTemplateNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (templateName, staticRow) in TemplateStaticRows)
        {
            if (Items.TryGetValue(staticRow, out var info))
            {
                names[templateName] = info.DisplayName;
            }
        }

        return names;
    }

    /// <summary>The game's own names for its currencies (D_MetaCurrency rows).</summary>
    private static readonly Dictionary<string, string> CurrencyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Credits"] = "Ren", ["Exotic1"] = "Exotics", ["Exotic_Red"] = "Red Exotics", ["Biomass"] = "Biomass",
        ["Exotic_Uranium"] = "Uranium", ["Licence"] = "Licences", ["Refund"] = "Respec Points", ["Biomass_Converter"] = "Flux",
    };

    /// <summary>D_WorkshopItems row -> "50 Ren" (its ResearchCost).</summary>
    private Dictionary<string, string> LoadWorkshopCosts() => Safe(() =>
    {
        var costs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (workshopRow, row) in RowsOf(WorkshopItemsTable))
        {
            if (row.TryGetProperty("ResearchCost", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                var parts = list.EnumerateArray()
                    .Select(c => (Meta: c.TryGetProperty("Meta", out var m) && m.TryGetProperty("RowName", out var mr) ? mr.GetString() ?? "?" : "?",
                                  Amount: c.TryGetProperty("Amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0))
                    .Where(c => c.Amount > 0)
                    .Select(c => $"{c.Amount:N0} {CurrencyNames.GetValueOrDefault(c.Meta, c.Meta)}");
                var text = string.Join(" + ", parts);
                if (text.Length > 0)
                {
                    costs[workshopRow] = text;
                }
            }
        }

        return costs;
    }, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>D_WorkshopItems row -> the display name of the item it researches (D_ItemTemplate -> D_ItemsStatic -> D_Itemable), for workshop talents. Empty when the tables are missing.</summary>
    private IReadOnlyDictionary<string, string> WorkshopItemNames => Cached(ref _workshopItemNames, () => Safe<IReadOnlyDictionary<string, string>>(() =>
    {
        // The workshop's Item is usually a D_ItemTemplate row; its ItemStaticData names the
        // D_ItemsStatic row Items is keyed by. Some share the static row's name already.
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var templateToStatic = TemplateStaticRows;
        foreach (var (workshopRow, row) in RowsOf(WorkshopItemsTable))
        {
            if (RowNameOf(row, "Item") is { } itemRowName)
            {
                var staticRow = templateToStatic.GetValueOrDefault(itemRowName, itemRowName);
                if (Items.TryGetValue(staticRow, out var info) || Items.TryGetValue(itemRowName, out info))
                {
                    names[workshopRow] = info.DisplayName;
                }
            }
        }

        return names;
    }, () => new Dictionary<string, string>()));

    /// <summary>
    /// D_ItemsStatic (what a MetaInventory item's ItemStaticData.RowName points at) is a pure
    /// ECS/trait-composite table — each row just references sub-tables by RowName for whichever
    /// traits it has (Meshable, Buildable, Itemable, ...), and carries NO display name of its own.
    /// Confirmed by tracing a real item end to end: the name lives in D_Itemable (the trait table
    /// for "can be held in an inventory"), keyed by the ROW'S OWN Itemable.RowName reference, not
    /// by the item's own top-level Name. So this is a genuine two-hop resolution — unlike every
    /// other lookup in this class — built once here rather than repeated at every call site. A
    /// D_ItemsStatic row with no Itemable trait (a pure building/world-only piece, never held) has
    /// no entry here at all; callers fall back to the raw RowName for those.
    /// </summary>
    private IReadOnlyDictionary<string, ItemInfo> LoadItems() => Safe<IReadOnlyDictionary<string, ItemInfo>>(() =>
    {
        var items = new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase);
        var itemableInfo = new Dictionary<string, (string DisplayName, int Weight, int MaxStack, string? IconPath)>(StringComparer.OrdinalIgnoreCase);
        var defaults = DefaultsOf(ItemableTable);
        foreach (var (itemableRowName, row) in RowsOf(ItemableTable))
        {
            var weight = TryGet(row, defaults, "Weight", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
            var maxStack = TryGet(row, defaults, "MaxStack", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 1;
            var iconPath = TryGet(row, defaults, "Icon", out var icon) && icon.ValueKind == JsonValueKind.String && icon.GetString() is { Length: > 0 } ip && ip != "None" ? ip : null;
            itemableInfo[itemableRowName] = (ParseLocText(row, "DisplayName", defaults) ?? itemableRowName, weight, maxStack, iconPath);
        }

        if (itemableInfo.Count == 0)
        {
            return items;
        }

        foreach (var (rowName, row) in RowsOf(ItemsStaticTable))
        {
            if (RowNameOf(row, "Itemable") is { } itemableRowName && itemableRowName != "None"
                && itemableInfo.TryGetValue(itemableRowName, out var info))
            {
                items.TryAdd(rowName, new ItemInfo(info.DisplayName, info.Weight, info.MaxStack, info.IconPath));
            }
        }

        return items;
    }, () => new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase));

    // --- Creatures: accolades, bestiary, mounts ---

    private IReadOnlyDictionary<string, AccoladeInfo> LoadAccolades() => Safe<IReadOnlyDictionary<string, AccoladeInfo>>(() =>
    {
        var accolades = new Dictionary<string, AccoladeInfo>(StringComparer.OrdinalIgnoreCase);
        var table = Path.Combine("Accolades", "D_Accolades.json");
        var defaults = DefaultsOf(table);
        foreach (var (rowName, row) in RowsOf(table))
        {
            var category = TryGet(row, defaults, "Category", out var categoryRef) && categoryRef.ValueKind == JsonValueKind.Object && categoryRef.TryGetProperty("Value", out var categoryValue)
                ? categoryValue.GetString() ?? ""
                : "";
            var goalCount = TryGet(row, defaults, "GoalCount", out var goal) && goal.ValueKind == JsonValueKind.Number ? goal.GetInt32() : 0;

            accolades.TryAdd(rowName, new AccoladeInfo(
                ParseLocText(row, "DisplayName", defaults) ?? rowName,
                ParseLocText(row, "Description", defaults) ?? "",
                category,
                goalCount));
        }

        return accolades;
    }, () => new Dictionary<string, AccoladeInfo>(StringComparer.OrdinalIgnoreCase));

    /// <summary>D_Atmospheres rows have no display names; these are the biomes' plain names.</summary>
    private static string BiomeGroup(string? biome) => biome switch
    {
        null or "" or "Unknown" => "Other",
        "Conifer" => "Forest",
        "DesertELY" => "Desert",
        "Jungle_Light" => "Jungle",
        "Workshop" => "Workshop creatures",
        _ when biome.StartsWith("Arena_", StringComparison.OrdinalIgnoreCase) => "Great Hunt arenas",
        _ => HumanizeMountType(biome),
    };

    private IReadOnlyDictionary<string, BestiaryCreatureInfo> LoadBestiaryCreatures() => Safe<IReadOnlyDictionary<string, BestiaryCreatureInfo>>(() =>
    {
        var creatures = new Dictionary<string, BestiaryCreatureInfo>(StringComparer.OrdinalIgnoreCase);
        var table = Path.Combine("Bestiary", "D_BestiaryData.json");
        var defaults = DefaultsOf(table);
        foreach (var (rowName, row) in RowsOf(table))
        {
            var pointsRequired = TryGet(row, defaults, "TotalPointsRequired", out var points) && points.ValueKind == JsonValueKind.Number ? points.GetInt32() : 0;
            var isBoss = TryGet(row, defaults, "bIsBoss", out var boss) && boss.ValueKind == JsonValueKind.True;
            var imagePath = TryGet(row, defaults, "Image", out var image) && image.ValueKind == JsonValueKind.String && image.GetString() is { Length: > 0 } img && img != "None" ? img : null;

            var biome = RowNames(row, defaults, "Biomes").FirstOrDefault();
            creatures.TryAdd(rowName, new BestiaryCreatureInfo(
                ParseLocText(row, "CreatureName", defaults) ?? rowName,
                pointsRequired,
                isBoss,
                imagePath,
                BiomeGroup(biome)));
        }

        return creatures;
    }, () => new Dictionary<string, BestiaryCreatureInfo>(StringComparer.OrdinalIgnoreCase));

    /// <summary>D_GeneticValues in order: "Vitality" shown as "Vigor", ...</summary>
    public IReadOnlyList<(string Row, string Title)> GeneticValues => Cached(ref _geneticValues, () => LoadTitles(Path.Combine("AI", "D_GeneticValues.json")));

    /// <summary>D_GeneticLineages in order: "Wild", "Brave", ... "Alpha".</summary>
    public IReadOnlyList<(string Row, string Title)> Lineages => Cached(ref _lineages, () => LoadTitles(Path.Combine("AI", "D_GeneticLineages.json")));

    private IReadOnlyList<(string Row, string Title)> LoadTitles(string relativePath) => Safe<IReadOnlyList<(string Row, string Title)>>(() =>
    {
        var defaults = DefaultsOf(relativePath);
        return [.. RowsOf(relativePath).Select(r => (r.Name, ParseLocText(r.Row, "Title", defaults) is { Length: > 0 } t ? t : HumanizeMountType(r.Name)))];
    }, () => []);

    private IReadOnlyDictionary<string, string> LoadMountTypeIcons() => Safe<IReadOnlyDictionary<string, string>>(() =>
    {
        // Missing/corrupt table just means mounts show with no thumbnail — the type picker
        // itself (MountTypeRowNames) degrades the exact same way already.
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, row) in RowsOf(MountsTable))
        {
            if (row.TryGetProperty("Icon", out var icon) && icon.ValueKind == JsonValueKind.String && icon.GetString() is { Length: > 0 } iconPath)
            {
                icons[rowName] = iconPath;
            }
        }

        return icons;
    }, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>D_Mounts rows carry no display-name field at all (confirmed directly against the real table) — a light PascalCase/underscore split is the best available "Woolly Mammoth" instead of "WoollyMammoth".</summary>
    public static string HumanizeMountType(string rowName) =>
        System.Text.RegularExpressions.Regex.Replace(rowName.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");

    // --- DLC ---

    /// <summary>
    /// D_DLCPackageData's rows in order: flags_&lt;SteamID&gt;.dat holds 0-based indexes into it —
    /// the game's cached list of the DLC this account owns (OfflinePackageFlagsSubsystem in the
    /// game's code; researched 2026-10-23). It was shown as character flags before.
    /// </summary>
    public IReadOnlyList<string> DlcPackageNames => Cached(ref _dlcPackageNames, () => LoadRowNames(DlcTable));

    /// <summary>The D_DLCPackageData row for a flags-file index ("Dangerous_Horizons"); null when the table doesn't have it.</summary>
    public string? DlcPackageRowName(int id) => id >= 0 && id < DlcPackageNames.Count ? DlcPackageNames[id] : null;

    /// <summary>"Dangerous Horizons", "Supporters Edition" for a flags-file index (the row's own DLCName); "DLC 31" when the table doesn't have it.</summary>
    public string DlcPackageName(int id) => id >= 0 && id < DlcPackageNames.Count ? DlcDisplayName(DlcPackageNames[id]) : $"DLC {id}";

    private IReadOnlyDictionary<string, string> DlcDisplayNames => Cached(ref _dlcDisplayNames, LoadDlcDisplayNames);

    /// <summary>A D_DLCPackageData row's own name: "Deluxe_Edition_Envirosuit" -> "Supporters Edition".</summary>
    public string DlcDisplayName(string rowName) => DlcDisplayNames.GetValueOrDefault(rowName) ?? HumanizeMountType(rowName);

    /// <summary>"the Supporters Edition", "the Dangerous Horizons DLC": what a talent or research row is for.</summary>
    public string DlcPackLabel(string rowName)
    {
        var name = DlcDisplayName(rowName);
        return name.Contains("Supporter", StringComparison.OrdinalIgnoreCase) || name.Contains("Edition", StringComparison.OrdinalIgnoreCase)
            ? $"the {name}"
            : $"the {name} DLC";
    }

    private IReadOnlyDictionary<string, string> LoadDlcDisplayNames() => Safe<IReadOnlyDictionary<string, string>>(() =>
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaults = DefaultsOf(DlcTable);
        foreach (var (rowName, row) in RowsOf(DlcTable))
        {
            if (ParseLocText(row, "DLCName", defaults)?.Trim() is { Length: > 0 } display)
            {
                names[rowName] = display;
            }
        }

        return names;
    }, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    // --- Shared row helpers ---

    /// <summary>Every row's Name in table order, "?" for a nameless one (flag ids are row positions).</summary>
    private IReadOnlyList<string> LoadRowNames(string relativePath) =>
        Safe<IReadOnlyList<string>>(() => [.. RowsOf(relativePath, keepNameless: true).Select(r => r.Name)], () => []);

    private static IEnumerable<string> RowNames(JsonElement row, JsonElement? defaults, string property) =>
        TryGet(row, defaults, property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("RowName", out var r) ? r.GetString() : null).OfType<string>()
            : [];

    /// <summary>A {"RowName": "X", ...} reference field's row name; null when it isn't one.</summary>
    private static string? RowNameOf(JsonElement row, string property) =>
        row.TryGetProperty(property, out var reference) && reference.ValueKind == JsonValueKind.Object
            && reference.TryGetProperty("RowName", out var r) ? r.GetString() : null;

    /// <summary>
    /// A row's value, or the table's own Defaults value when the row leaves it out — what the game
    /// does. Reading only the row showed seven creatures as "/ 0" (D_BestiaryData's Defaults sets
    /// TotalPointsRequired = 1000; Bear_Cub, Kea, Roat… don't repeat it).
    /// </summary>
    private static bool TryGet(JsonElement row, JsonElement? defaults, string property, out JsonElement value)
    {
        if (row.TryGetProperty(property, out value))
        {
            return true;
        }

        if (defaults is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty(property, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static JsonElement? DefaultsOf(JsonDocument document) =>
        document.RootElement.TryGetProperty("Defaults", out var defaults) && defaults.ValueKind == JsonValueKind.Object ? defaults : null;

    /// <summary>The tables wrap localized text as NSLOCTEXT("Table", "Key", "The English text") — the last quoted argument is the display string.</summary>
    private static string? ParseLocText(JsonElement row, string property, JsonElement? defaults = null)
    {
        if (!TryGet(row, defaults, property, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } raw)
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(raw, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\)\\s*$");
        // Every escape, not just \": accolades showed "Rock \'n\' Roll" (2026-10-23).
        return match.Success
            ? System.Text.RegularExpressions.Regex.Replace(match.Groups[1].Value, @"\\(.)", m => m.Groups[1].Value switch { "n" => "\n", "r" => "", "t" => " ", var c => c })
            : raw;
    }
}

/// <param name="MaxRank">The most ranks this talent can hold, from its own Rewards tiers — what the editor caps rank controls at.</param>
/// <param name="IsDefaultUnlocked">The row's own bDefaultUnlocked — the game grants it from the start WITHOUT writing it into the save, so the editor must show it as unlocked even at rank 0.</param>
/// <param name="Cost">Workshop research only: "50 Ren" (D_WorkshopItems ResearchCost).</param>
public sealed record TalentInfo(string DisplayName, string Description, string Tree, int MaxRank, bool IsDefaultUnlocked, string? Cost = null);

/// <param name="Display">The tree's own name ("Resources").</param>
/// <param name="ArchetypeDisplay">Its archetype's ("Survival", "Tier 1", "Olympus", "Envirosuits").</param>
/// <param name="Model">Player, Blueprint, Solo (a character's), Workshop or Prospect (the account's).</param>
/// <param name="ArchetypeRow">Its D_TalentArchetypes row ("Creature_Raptor"): a mount's talents are the trees of its archetype.</param>
public sealed record TreeInfo(string Display, string ArchetypeDisplay, string Model, string ArchetypeRow = "")
{
    /// <summary>
    /// "Survival · Resources" for a character's trees; just "Armors" / "Elysium" for workshop and
    /// mission trees, whose own names only repeat the archetype's ("Workshop Armors", "Prospect Elysium").
    /// </summary>
    public string Group => Model is "Workshop" or "Prospect" || string.Equals(Display, ArchetypeDisplay, StringComparison.OrdinalIgnoreCase)
        ? ArchetypeDisplay
        : $"{ArchetypeDisplay} · {Display}";
}

public sealed record AccoladeInfo(string DisplayName, string Description, string Category, int GoalCount);

/// <param name="RequiredFlags">(table, row): D_AccountFlags, D_CharacterFlags or D_DLCPackageData.</param>
/// <param name="GrantedFlagsByRank">The character flags each rank gives (Rewards[i].GrantedFlags).</param>
/// <param name="IsFreeToReach">A Reroute node or unlocked from the start: counts as met when it's another talent's prerequisite.</param>
public sealed record TalentRule(
    IReadOnlyList<(string Table, string Row)> RequiredFlags, IReadOnlyList<string> RequiredTalents,
    IReadOnlyList<IReadOnlyList<string>> GrantedFlagsByRank, bool IsFreeToReach);

/// <summary>What a flag is for, on the Saves page's Unlocks tab.</summary>
public enum FlagKind
{
    /// <summary>A mission's reward: unlocks blueprints or workshop items. Worth editing.</summary>
    Reward,

    /// <summary>Gives bonus talent points (D_PlayerTalentModifiers). Worth editing.</summary>
    TalentPoints,

    /// <summary>A one-off marker or story step the game sets itself.</summary>
    Marker,

    /// <summary>A "has seen" pop-up.</summary>
    Prompt,

    /// <summary>Set by a learned talent: shown read-only.</summary>
    FromTalent,

    /// <summary>Deprecated, tooltip-only, test, or granted by nothing: hidden unless asked for.</summary>
    Unused,

    /// <summary>Not in the game's table at all.</summary>
    Unknown,
}

/// <param name="Detail">What it does, where it comes from — the row's second line.</param>
public sealed record FlagInfo(string RowName, string Display, string Detail, FlagKind Kind);

/// <param name="PointsRequired">The row's own TotalPointsRequired — what the editor treats as "maxed out" for a Set to max action.</param>
/// <param name="ImagePath">The row's own raw "Image" field (a "/Game/…/T_Bestiary_Xxx.T_Bestiary_Xxx" texture reference) — null for a row that never carried one, or when the table itself is missing.</param>
/// <param name="Group">Where it lives, from its first D_BestiaryData Biomes row: "Forest", "Arctic", "Great Hunt arenas".</param>
public sealed record BestiaryCreatureInfo(string DisplayName, int PointsRequired, bool IsBoss, string? ImagePath, string Group = "");

/// <param name="IconPath">The Itemable row's own raw "Icon" field (a "/Game/…/ITEM_Xxx.ITEM_Xxx" texture reference) — null for a row that never carried one, or when the table itself is missing.</param>
public sealed record ItemInfo(string DisplayName, int Weight, int MaxStack, string? IconPath);
