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
/// </summary>
public sealed class SaveGameNames(string dataFolder)
{
    /// <summary>The extracted game data these names come from.</summary>
    public string DataFolder => dataFolder;

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
    private IReadOnlyDictionary<string, TreeInfo>? _talentTrees;
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _talentsGatedByFlag;
    private IReadOnlyDictionary<string, IReadOnlyList<string>>? _missionRewardFlags;
    private IReadOnlyList<FlagInfo>? _accountFlagInfos;
    private IReadOnlyList<FlagInfo>? _characterFlagInfos;

    public IReadOnlyList<string> CharacterFlagNames => _characterFlagNames ??= LoadRowNames(Path.Combine("Flags", "D_CharacterFlags.json"));

    public IReadOnlyList<string> AccountFlagNames => _accountFlagNames ??= LoadRowNames(Path.Combine("Flags", "D_AccountFlags.json"));

    /// <summary>Everything the save editor wants to know about each talent, keyed by the RowName the save stores. MaxRank comes from the row's own Rewards tier count (a reward-less row — blueprint/workshop unlocks, 1312 of 2227 — is a single-rank unlock).</summary>
    public IReadOnlyDictionary<string, TalentInfo> Talents => _talents ??= LoadTalents();

    /// <summary>
    /// Talent trees a CHARACTER learns in: D_TalentTrees' Archetype → D_TalentArchetypes' Model
    /// "Player" (Survival, Combat, ...), "Blueprint" (the blueprint trees) or "Solo". Checked
    /// against a real 1,000-talent character: every talent it had that the game still defines sat
    /// in one of these. The other models (Workshop, Prospect, Creature, Great_Hunt, Outpost) belong
    /// to the account or to mounts, so offering them on a character would write talents the game
    /// never reads there. Empty when the tables aren't extracted (then nothing is filtered).
    /// </summary>
    public IReadOnlySet<string> CharacterTalentTrees => _characterTalentTrees ??= LoadCharacterTalentTrees();

    /// <summary>Every accolade, keyed by the RowName Accolades.json's CompletedAccolades[].Accolade.RowName stores.</summary>
    public IReadOnlyDictionary<string, AccoladeInfo> Accolades => _accolades ??= LoadAccolades();

    /// <summary>Every trackable creature, keyed by the RowName BestiaryData.json's BestiaryTracking[].BestiaryGroup.RowName stores.</summary>
    public IReadOnlyDictionary<string, BestiaryCreatureInfo> BestiaryCreatures => _bestiaryCreatures ??= LoadBestiaryCreatures();

    /// <summary>Every item, keyed by the RowName MetaInventory.json's Items[].ItemStaticData.RowName stores (a D_ItemsStatic row). D_ItemsStatic itself carries no display name — see LoadItems' own comment for the real two-hop chain this resolves.</summary>
    public IReadOnlyDictionary<string, ItemInfo> Items => _items ??= LoadItems();

    /// <summary>Every real, valid Mounts.json MountType value — D_Mounts's own row names (37 on a current extraction), confirmed to be exactly what MountType stores. Used to offer only real choices in a picker rather than free text, the same way a save's own binary flags/talent ranks are already bounded by real game data.</summary>
    public IReadOnlyList<string> MountTypeRowNames => _mountTypeRowNames ??= LoadRowNames(Path.Combine("AI", "D_Mounts.json"));

    /// <summary>D_Mounts's own "Icon" field per row (a raw "/Game/…/T_Talent_Base_Xxx.T_Talent_Base_Xxx" texture reference, the same self-named-asset shape D_Itemable's own Icon/D_BestiaryData's own Image use), keyed by RowName — what a saved mount's own MountType resolves to for its thumbnail. A separate lazy load from MountTypeRowNames above (a second pass over the same small file) rather than folding into it, since that one's callers only ever wanted plain row names and a picker never needed icons.</summary>
    public IReadOnlyDictionary<string, string> MountTypeIcons => _mountTypeIcons ??= LoadMountTypeIcons();

    /// <summary>
    /// Forgets every table read so far, so the next lookup reads the data folder again. Called
    /// after Refresh game data — without it, names (or the empty lists from before the data was
    /// first extracted) stayed cached until the app restarted.
    /// </summary>
    public void Invalidate()
    {
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
        _accountFlagInfos = null;
        _characterFlagInfos = null;
        _talentTrees = null;
        _talentsGatedByFlag = null;
        _missionRewardFlags = null;
    }

    /// <summary>
    /// Flag row name -> the talents whose RequiredFlags name it (Caveworm Knife and Spear need
    /// GrantedBlueprint_CavewormMelee). A character can have learned those talents while the flag is
    /// off (found on the user's save: the game then shows them as unlocked), so the Unlocks tab says so.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> TalentsGatedByFlag => _talentsGatedByFlag ??= LoadTalentsGatedByFlag();

    /// <summary>
    /// Mission (a profile Prospect_* talent) -> the account flags finishing it rewards: the talent's
    /// ExtraData names its D_ProspectList row, and D_AccountFlags' RewardedFromMissions names the
    /// same rows. 53 of the game's 88 mission rewards link this way (2026-10-23); the other 35 come
    /// from Great Hunt missions, which the save doesn't keep as Prospect_* talents.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> MissionRewardFlags => _missionRewardFlags ??= LoadMissionRewardFlags();

    private IReadOnlyDictionary<string, IReadOnlyList<string>> LoadMissionRewardFlags()
    {
        var rewards = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var talentsPath = Path.Combine(dataFolder, "Talents", "D_Talents.json");
            var flagsPath = Path.Combine(dataFolder, "Flags", "D_AccountFlags.json");
            if (!File.Exists(talentsPath) || !File.Exists(flagsPath))
            {
                return new Dictionary<string, IReadOnlyList<string>>();
            }

            var missionsOfProspect = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            using (var talents = JsonDocument.Parse(File.ReadAllText(talentsPath)))
            {
                foreach (var row in talents.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    if (row.TryGetProperty("Name", out var n) && n.GetString() is { } talent
                        && talent.StartsWith("Prospect_", StringComparison.OrdinalIgnoreCase)
                        && row.TryGetProperty("ExtraData", out var extra) && extra.ValueKind == JsonValueKind.Object
                        && extra.TryGetProperty("RowName", out var r) && r.GetString() is { } prospect)
                    {
                        (missionsOfProspect.TryGetValue(prospect, out var list) ? list : missionsOfProspect[prospect] = []).Add(talent);
                    }
                }
            }

            using var flags = JsonDocument.Parse(File.ReadAllText(flagsPath));
            var defaults = DefaultsOf(flags);
            foreach (var row in flags.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out var n) || n.GetString() is not { } flag)
                {
                    continue;
                }

                foreach (var prospect in RowNames(row, defaults, "RewardedFromMissions"))
                {
                    foreach (var mission in missionsOfProspect.GetValueOrDefault(prospect, []))
                    {
                        (rewards.TryGetValue(mission, out var list) ? list : rewards[mission] = []).Add(flag);
                    }
                }
            }
        }
        catch (Exception)
        {
            rewards.Clear();
        }

        return rewards.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
    }

    private IReadOnlyDictionary<string, IReadOnlyList<string>> LoadTalentsGatedByFlag()
    {
        var gated = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(dataFolder, "Talents", "D_Talents.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    if (row.TryGetProperty("Name", out var n) && n.GetString() is { } talent)
                    {
                        foreach (var flag in RowNames(row, null, "RequiredFlags"))
                        {
                            (gated.TryGetValue(flag, out var list) ? list : gated[flag] = []).Add(talent);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            gated.Clear();
        }

        return gated.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every talent tree: its own name ("Resources"), its archetype's ("Survival", "Tier 1",
    /// "Olympus", "Envirosuits") and the archetype's model — Player, Blueprint and Solo trees are a
    /// character's; Workshop research and Prospect (mission completions) are the account's.
    /// </summary>
    public IReadOnlyDictionary<string, TreeInfo> TalentTrees => _talentTrees ??= LoadTalentTrees();

    private IReadOnlyDictionary<string, TreeInfo> LoadTalentTrees()
    {
        var trees = new Dictionary<string, TreeInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var archetypesPath = Path.Combine(dataFolder, "Talents", "D_TalentArchetypes.json");
            var treesPath = Path.Combine(dataFolder, "Talents", "D_TalentTrees.json");
            if (!File.Exists(archetypesPath) || !File.Exists(treesPath))
            {
                return trees;
            }

            var archetypes = new Dictionary<string, (string Display, string Model)>(StringComparer.OrdinalIgnoreCase);
            using (var document = JsonDocument.Parse(File.ReadAllText(archetypesPath)))
            {
                foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    if (row.TryGetProperty("Name", out var n) && n.GetString() is { } name)
                    {
                        var model = row.TryGetProperty("Model", out var m) && m.ValueKind == JsonValueKind.Object && m.TryGetProperty("RowName", out var mr) ? mr.GetString() ?? "" : "";
                        archetypes[name] = (ParseLocText(row, "DisplayName") ?? HumanizeMountType(name), model);
                    }
                }
            }

            using var treeDocument = JsonDocument.Parse(File.ReadAllText(treesPath));
            foreach (var row in treeDocument.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (row.TryGetProperty("Name", out var n) && n.GetString() is { } name)
                {
                    var archetype = row.TryGetProperty("Archetype", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("RowName", out var ar) ? ar.GetString() ?? "" : "";
                    var (archetypeDisplay, model) = archetypes.GetValueOrDefault(archetype, (HumanizeMountType(archetype), ""));
                    trees[name] = new TreeInfo(ParseLocText(row, "DisplayName") ?? HumanizeMountType(name), archetypeDisplay, model);
                }
            }
        }
        catch (Exception)
        {
            trees.Clear();
        }

        return trees;
    }

    /// <summary>What each account flag is for (by row index), from D_AccountFlags' own fields — researched 2026-10-23.</summary>
    public IReadOnlyList<FlagInfo> AccountFlagInfos => _accountFlagInfos ??= LoadAccountFlagInfos();

    /// <summary>What each character flag is for (by row index): every one turned out to be set by a talent, deprecated, tooltip-only, test or never granted — researched 2026-10-23.</summary>
    public IReadOnlyList<FlagInfo> CharacterFlagInfos => _characterFlagInfos ??= LoadCharacterFlagInfos();

    public FlagInfo AccountFlagInfo(int id) => id >= 0 && id < AccountFlagInfos.Count ? AccountFlagInfos[id] : new FlagInfo($"Flag {id}", $"Flag {id}", "Not in the game's flag table (from a mod, or removed).", FlagKind.Unknown);

    public FlagInfo CharacterFlagInfo(int id) => id >= 0 && id < CharacterFlagInfos.Count ? CharacterFlagInfos[id] : new FlagInfo($"Flag {id}", $"Flag {id}", "Not in the game's flag table (from a mod, or removed).", FlagKind.Unknown);

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

    private IReadOnlyList<FlagInfo> LoadAccountFlagInfos()
    {
        var result = new List<FlagInfo>();
        try
        {
            var path = Path.Combine(dataFolder, "Flags", "D_AccountFlags.json");
            if (!File.Exists(path))
            {
                return result;
            }

            var missions = LoadMissionNames();
            var talentPoints = LoadTalentPointModifiers();
            var templateNames = LoadItemTemplateNames();
            var workshopNames = LoadWorkshopItemNames();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var defaults = DefaultsOf(document);
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                var name = row.TryGetProperty("Name", out var n) ? n.GetString() ?? "?" : "?";
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
        }
        catch (Exception)
        {
            result.Clear();
        }

        return result;
    }

    private IReadOnlyList<FlagInfo> LoadCharacterFlagInfos()
    {
        var result = new List<FlagInfo>();
        try
        {
            var path = Path.Combine(dataFolder, "Flags", "D_CharacterFlags.json");
            if (!File.Exists(path))
            {
                return result;
            }

            var grantedBy = LoadTalentGrantedFlags();
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var defaults = DefaultsOf(document);
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                var name = row.TryGetProperty("Name", out var n) ? n.GetString() ?? "?" : "?";
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
        }
        catch (Exception)
        {
            result.Clear();
        }

        return result;
    }

    private static IEnumerable<string> RowNames(JsonElement row, JsonElement? defaults, string property) =>
        TryGet(row, defaults, property, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("RowName", out var r) ? r.GetString() : null).OfType<string>()
            : [];

    /// <summary>D_ProspectList row -> its drop name ("ARCWOOD: Outpost"), for mission names.</summary>
    private Dictionary<string, string> LoadMissionNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(dataFolder, "Prospects", "D_ProspectList.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    if (row.TryGetProperty("Name", out var n) && n.GetString() is { } rowName && ParseLocText(row, "DropName") is { Length: > 0 } drop)
                    {
                        names[rowName] = drop;
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return names;
    }

    /// <summary>Account flag -> the bonus talent points it gives (D_PlayerTalentModifiers' TalentPointModifier, keyed by its RequiredFlags).</summary>
    private Dictionary<string, int> LoadTalentPointModifiers()
    {
        var points = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(dataFolder, "Talents", "D_PlayerTalentModifiers.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    var modifier = row.TryGetProperty("TalentPointModifier", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 0;
                    foreach (var flag in RowNames(row, null, "RequiredFlags"))
                    {
                        points[flag] = points.GetValueOrDefault(flag) + modifier;
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return points;
    }

    /// <summary>Character flag -> the talent whose rewards grant it (D_Talents Rewards[].GrantedFlags).</summary>
    private Dictionary<string, string> LoadTalentGrantedFlags()
    {
        var grantedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(dataFolder, "Talents", "D_Talents.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    if (!row.TryGetProperty("Rewards", out var rewards) || rewards.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var talentName = ParseLocText(row, "DisplayName") ?? (row.TryGetProperty("Name", out var n) ? n.GetString() ?? "?" : "?");
                    foreach (var reward in rewards.EnumerateArray())
                    {
                        foreach (var flag in RowNames(reward, null, "GrantedFlags"))
                        {
                            grantedBy.TryAdd(flag, talentName);
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return grantedBy;
    }

    /// <summary>D_ItemTemplate row -> the item's display name (via D_ItemsStatic -> D_Itemable).</summary>
    private Dictionary<string, string> LoadItemTemplateNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var templatePath = Path.Combine(dataFolder, "Items", "D_ItemTemplate.json");
            if (File.Exists(templatePath))
            {
                using var templates = JsonDocument.Parse(File.ReadAllText(templatePath));
                foreach (var row in templates.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    if (row.TryGetProperty("Name", out var n) && n.GetString() is { } templateName
                        && row.TryGetProperty("ItemStaticData", out var st) && st.ValueKind == JsonValueKind.Object
                        && st.TryGetProperty("RowName", out var stRow) && stRow.GetString() is { } staticRow
                        && Items.TryGetValue(staticRow, out var info))
                    {
                        names[templateName] = info.DisplayName;
                    }
                }
            }
        }
        catch (Exception)
        {
        }

        return names;
    }

    /// <summary>
    /// D_DLCPackageData's rows in order: flags_&lt;SteamID&gt;.dat holds 0-based indexes into it —
    /// the game's cached list of the DLC this account owns (OfflinePackageFlagsSubsystem in the
    /// game's code; researched 2026-10-23). It was shown as character flags before.
    /// </summary>
    public IReadOnlyList<string> DlcPackageNames => _dlcPackageNames ??= LoadRowNames(Path.Combine("DLC", "D_DLCPackageData.json"));

    /// <summary>The D_DLCPackageData row for a flags-file index ("Dangerous_Horizons"); null when the table doesn't have it.</summary>
    public string? DlcPackageRowName(int id) => id >= 0 && id < DlcPackageNames.Count ? DlcPackageNames[id] : null;

    /// <summary>"Dangerous Horizons" for a flags-file index; "DLC 31" when the table doesn't have it.</summary>
    public string DlcPackageName(int id) => id >= 0 && id < DlcPackageNames.Count ? HumanizeMountType(DlcPackageNames[id]) : $"DLC {id}";

    public string CharacterFlagName(int id) => id >= 0 && id < CharacterFlagNames.Count ? CharacterFlagNames[id] : $"Flag {id}";

    public string AccountFlagName(int id) => id >= 0 && id < AccountFlagNames.Count ? AccountFlagNames[id] : $"Flag {id}";

    /// <summary>D_Mounts rows carry no display-name field at all (confirmed directly against the real table) — a light PascalCase/underscore split is the best available "Woolly Mammoth" instead of "WoollyMammoth".</summary>
    public static string HumanizeMountType(string rowName) =>
        System.Text.RegularExpressions.Regex.Replace(rowName.Replace('_', ' '), "(?<=[a-z0-9])(?=[A-Z])", " ");

    private IReadOnlyList<string> LoadRowNames(string relativePath)
    {
        try
        {
            var path = Path.Combine(dataFolder, relativePath);
            if (!File.Exists(path))
            {
                return [];
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return [.. document.RootElement.GetProperty("Rows").EnumerateArray()
                .Select(row => row.TryGetProperty("Name", out var name) ? name.GetString() ?? "?" : "?")];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private IReadOnlyDictionary<string, string> LoadMountTypeIcons()
    {
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(dataFolder, "AI", "D_Mounts.json");
            if (!File.Exists(path))
            {
                return icons;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (row.TryGetProperty("Name", out var name) && name.GetString() is { } rowName
                    && row.TryGetProperty("Icon", out var icon) && icon.GetString() is { Length: > 0 } iconPath)
                {
                    icons[rowName] = iconPath;
                }
            }
        }
        catch (Exception)
        {
            // Missing/corrupt table just means mounts show with no thumbnail — the type picker
            // itself (MountTypeRowNames) degrades the exact same way already.
        }

        return icons;
    }

    private static readonly HashSet<string> CharacterTalentModels = new(StringComparer.OrdinalIgnoreCase) { "Player", "Blueprint", "Solo" };

    private IReadOnlySet<string> LoadCharacterTalentTrees()
    {
        var trees = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var archetypesPath = Path.Combine(dataFolder, "Talents", "D_TalentArchetypes.json");
            var treesPath = Path.Combine(dataFolder, "Talents", "D_TalentTrees.json");
            if (!File.Exists(archetypesPath) || !File.Exists(treesPath))
            {
                return trees;
            }

            var characterArchetypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var archetypes = JsonDocument.Parse(File.ReadAllText(archetypesPath)))
            {
                foreach (var row in archetypes.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    if (row.TryGetProperty("Name", out var name) && name.GetString() is { } archetypeName
                        && row.TryGetProperty("Model", out var model) && model.ValueKind == JsonValueKind.Object
                        && model.TryGetProperty("RowName", out var modelName) && modelName.GetString() is { } modelRow
                        && CharacterTalentModels.Contains(modelRow))
                    {
                        characterArchetypes.Add(archetypeName);
                    }
                }
            }

            using var treeDocument = JsonDocument.Parse(File.ReadAllText(treesPath));
            foreach (var row in treeDocument.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (row.TryGetProperty("Name", out var name) && name.GetString() is { } treeName
                    && row.TryGetProperty("Archetype", out var archetype) && archetype.ValueKind == JsonValueKind.Object
                    && archetype.TryGetProperty("RowName", out var archetypeName) && archetypeName.GetString() is { } archetypeRow
                    && characterArchetypes.Contains(archetypeRow))
                {
                    trees.Add(treeName);
                }
            }
        }
        catch (Exception)
        {
            // Missing/corrupt tables: no filter, every talent shows (the old behaviour).
            trees.Clear();
        }

        return trees;
    }

    private IReadOnlyDictionary<string, TalentInfo> LoadTalents()
    {
        var talents = new Dictionary<string, TalentInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(dataFolder, "Talents", "D_Talents.json");
            if (!File.Exists(path))
            {
                return talents;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var defaults = DefaultsOf(document);
            var workshopNames = LoadWorkshopItemNames();
            var workshopCosts = LoadWorkshopCosts();
            var missionNames = LoadMissionNames();
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out var name) || name.GetString() is not { } talentName)
                {
                    continue;
                }

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

                var tree = row.TryGetProperty("TalentTree", out var treeRef) && treeRef.TryGetProperty("RowName", out var treeName)
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
                var extraRowName = row.TryGetProperty("ExtraData", out var extra) && extra.ValueKind == JsonValueKind.Object
                    && extra.TryGetProperty("RowName", out var extraRow) ? extraRow.GetString() : null;
                var workshopName = extraRowName is not null && workshopNames.TryGetValue(extraRowName, out var itemName) ? itemName : null;
                var missionName = extraRowName is not null && missionNames.TryGetValue(extraRowName, out var mission) ? mission : null;
                var cost = extraRowName is not null ? workshopCosts.GetValueOrDefault(extraRowName) : null;

                talents.TryAdd(talentName, new TalentInfo(
                    ParseLocText(row, "DisplayName", defaults) ?? workshopName ?? missionName ?? talentName,
                    ParseLocText(row, "Description", defaults) ?? "",
                    tree,
                    maxRank,
                    defaultUnlocked,
                    cost));
            }
        }
        catch (Exception)
        {
            // Missing/corrupt table just means talents show by RowName with an uncapped rank.
        }

        return talents;
    }

    /// <summary>The game's own names for its currencies (D_MetaCurrency rows).</summary>
    private static readonly Dictionary<string, string> CurrencyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Credits"] = "Ren", ["Exotic1"] = "Exotics", ["Exotic_Red"] = "Red Exotics", ["Biomass"] = "Biomass",
        ["Exotic_Uranium"] = "Uranium", ["Licence"] = "Licences", ["Refund"] = "Respec Points", ["Biomass_Converter"] = "Flux",
    };

    /// <summary>D_WorkshopItems row -> "50 Ren" (its ResearchCost).</summary>
    private Dictionary<string, string> LoadWorkshopCosts()
    {
        var costs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(dataFolder, "MetaWorkshop", "D_WorkshopItems.json");
            if (File.Exists(path))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    if (row.TryGetProperty("Name", out var n) && n.GetString() is { } workshopRow
                        && row.TryGetProperty("ResearchCost", out var list) && list.ValueKind == JsonValueKind.Array)
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
            }
        }
        catch (Exception)
        {
        }

        return costs;
    }

    /// <summary>D_WorkshopItems row -> the display name of the item it researches (D_ItemTemplate -> D_ItemsStatic -> D_Itemable), for workshop talents. Empty when the tables are missing.</summary>
    private Dictionary<string, string> LoadWorkshopItemNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var workshopPath = Path.Combine(dataFolder, "MetaWorkshop", "D_WorkshopItems.json");
            if (!File.Exists(workshopPath))
            {
                return names;
            }

            // The workshop's Item is usually a D_ItemTemplate row; its ItemStaticData names the
            // D_ItemsStatic row Items is keyed by. Some share the static row's name already.
            var templateToStatic = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var templatePath = Path.Combine(dataFolder, "Items", "D_ItemTemplate.json");
            if (File.Exists(templatePath))
            {
                using var templates = JsonDocument.Parse(File.ReadAllText(templatePath));
                foreach (var row in templates.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    if (row.TryGetProperty("Name", out var n) && n.GetString() is { } templateName
                        && row.TryGetProperty("ItemStaticData", out var st) && st.ValueKind == JsonValueKind.Object
                        && st.TryGetProperty("RowName", out var stRow) && stRow.GetString() is { } staticRow)
                    {
                        templateToStatic[templateName] = staticRow;
                    }
                }
            }

            using var workshop = JsonDocument.Parse(File.ReadAllText(workshopPath));
            foreach (var row in workshop.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (row.TryGetProperty("Name", out var n) && n.GetString() is { } workshopRow
                    && row.TryGetProperty("Item", out var item) && item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("RowName", out var itemRow) && itemRow.GetString() is { } itemRowName)
                {
                    var staticRow = templateToStatic.GetValueOrDefault(itemRowName, itemRowName);
                    if (Items.TryGetValue(staticRow, out var info) || Items.TryGetValue(itemRowName, out info))
                    {
                        names[workshopRow] = info.DisplayName;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Missing/corrupt tables: workshop rows keep their raw names.
        }

        return names;
    }

    private IReadOnlyDictionary<string, AccoladeInfo> LoadAccolades()
    {
        var accolades = new Dictionary<string, AccoladeInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(dataFolder, "Accolades", "D_Accolades.json");
            if (!File.Exists(path))
            {
                return accolades;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var defaults = DefaultsOf(document);
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out var name) || name.GetString() is not { } rowName)
                {
                    continue;
                }

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
        }
        catch (Exception)
        {
            // Missing/corrupt table just means accolades show by RowName with no description.
        }

        return accolades;
    }

    private IReadOnlyDictionary<string, BestiaryCreatureInfo> LoadBestiaryCreatures()
    {
        var creatures = new Dictionary<string, BestiaryCreatureInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = Path.Combine(dataFolder, "Bestiary", "D_BestiaryData.json");
            if (!File.Exists(path))
            {
                return creatures;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var defaults = DefaultsOf(document);
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out var name) || name.GetString() is not { } rowName)
                {
                    continue;
                }

                var pointsRequired = TryGet(row, defaults, "TotalPointsRequired", out var points) && points.ValueKind == JsonValueKind.Number ? points.GetInt32() : 0;
                var isBoss = TryGet(row, defaults, "bIsBoss", out var boss) && boss.ValueKind == JsonValueKind.True;
                var imagePath = TryGet(row, defaults, "Image", out var image) && image.ValueKind == JsonValueKind.String && image.GetString() is { Length: > 0 } img && img != "None" ? img : null;

                creatures.TryAdd(rowName, new BestiaryCreatureInfo(
                    ParseLocText(row, "CreatureName", defaults) ?? rowName,
                    pointsRequired,
                    isBoss,
                    imagePath));
            }
        }
        catch (Exception)
        {
            // Missing/corrupt table just means creatures show by RowName with no points goal.
        }

        return creatures;
    }

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
    private IReadOnlyDictionary<string, ItemInfo> LoadItems()
    {
        var items = new Dictionary<string, ItemInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var itemablePath = Path.Combine(dataFolder, "Traits", "D_Itemable.json");
            if (!File.Exists(itemablePath))
            {
                return items;
            }

            var itemableInfo = new Dictionary<string, (string DisplayName, int Weight, int MaxStack, string? IconPath)>(StringComparer.OrdinalIgnoreCase);
            using (var itemableDoc = JsonDocument.Parse(File.ReadAllText(itemablePath)))
            {
                var defaults = DefaultsOf(itemableDoc);
                foreach (var row in itemableDoc.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    if (row.TryGetProperty("Name", out var name) && name.GetString() is { } itemableRowName)
                    {
                        var weight = TryGet(row, defaults, "Weight", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
                        var maxStack = TryGet(row, defaults, "MaxStack", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 1;
                        var iconPath = TryGet(row, defaults, "Icon", out var icon) && icon.ValueKind == JsonValueKind.String && icon.GetString() is { Length: > 0 } ip && ip != "None" ? ip : null;
                        itemableInfo[itemableRowName] = (ParseLocText(row, "DisplayName", defaults) ?? itemableRowName, weight, maxStack, iconPath);
                    }
                }
            }

            var staticPath = Path.Combine(dataFolder, "Items", "D_ItemsStatic.json");
            if (!File.Exists(staticPath))
            {
                return items;
            }

            using var staticDoc = JsonDocument.Parse(File.ReadAllText(staticPath));
            foreach (var row in staticDoc.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out var name) || name.GetString() is not { } rowName)
                {
                    continue;
                }

                if (row.TryGetProperty("Itemable", out var itemableRef) && itemableRef.TryGetProperty("RowName", out var itemableRowNameEl)
                    && itemableRowNameEl.GetString() is { } itemableRowName && itemableRowName != "None"
                    && itemableInfo.TryGetValue(itemableRowName, out var info))
                {
                    items.TryAdd(rowName, new ItemInfo(info.DisplayName, info.Weight, info.MaxStack, info.IconPath));
                }
            }
        }
        catch (Exception)
        {
            // Missing/corrupt tables just means items show by their raw RowName.
        }

        return items;
    }

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
        return match.Success ? match.Groups[1].Value.Replace("\\\"", "\"") : raw;
    }
}

/// <param name="MaxRank">The most ranks this talent can hold, from its own Rewards tiers — what the editor caps rank controls at.</param>
/// <param name="IsDefaultUnlocked">The row's own bDefaultUnlocked — the game grants it from the start WITHOUT writing it into the save, so the editor must show it as unlocked even at rank 0.</param>
/// <param name="Cost">Workshop research only: "50 Ren" (D_WorkshopItems ResearchCost).</param>
public sealed record TalentInfo(string DisplayName, string Description, string Tree, int MaxRank, bool IsDefaultUnlocked, string? Cost = null);

/// <param name="Display">The tree's own name ("Resources").</param>
/// <param name="ArchetypeDisplay">Its archetype's ("Survival", "Tier 1", "Olympus", "Envirosuits").</param>
/// <param name="Model">Player, Blueprint, Solo (a character's), Workshop or Prospect (the account's).</param>
public sealed record TreeInfo(string Display, string ArchetypeDisplay, string Model)
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
public sealed record BestiaryCreatureInfo(string DisplayName, int PointsRequired, bool IsBoss, string? ImagePath);

/// <param name="IconPath">The Itemable row's own raw "Icon" field (a "/Game/…/ITEM_Xxx.ITEM_Xxx" texture reference) — null for a row that never carried one, or when the table itself is missing.</param>
public sealed record ItemInfo(string DisplayName, int Weight, int MaxStack, string? IconPath);
