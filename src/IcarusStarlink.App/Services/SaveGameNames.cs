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
    private IReadOnlyList<string>? _characterFlagNames;
    private IReadOnlyList<string>? _accountFlagNames;
    private IReadOnlyDictionary<string, TalentInfo>? _talents;
    private IReadOnlyDictionary<string, AccoladeInfo>? _accolades;
    private IReadOnlyDictionary<string, BestiaryCreatureInfo>? _bestiaryCreatures;
    private IReadOnlyDictionary<string, ItemInfo>? _items;
    private IReadOnlyList<string>? _mountTypeRowNames;
    private IReadOnlyDictionary<string, string>? _mountTypeIcons;

    public IReadOnlyList<string> CharacterFlagNames => _characterFlagNames ??= LoadRowNames(Path.Combine("Flags", "D_CharacterFlags.json"));

    public IReadOnlyList<string> AccountFlagNames => _accountFlagNames ??= LoadRowNames(Path.Combine("Flags", "D_AccountFlags.json"));

    /// <summary>Everything the save editor wants to know about each talent, keyed by the RowName the save stores. MaxRank comes from the row's own Rewards tier count (a reward-less row — blueprint/workshop unlocks, 1312 of 2227 — is a single-rank unlock).</summary>
    public IReadOnlyDictionary<string, TalentInfo> Talents => _talents ??= LoadTalents();

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
                if (row.TryGetProperty("TalentType", out var talentType)
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
                var defaultUnlocked = row.TryGetProperty("bDefaultUnlocked", out var b) && b.ValueKind == JsonValueKind.True;

                talents.TryAdd(talentName, new TalentInfo(
                    ParseLocText(row, "DisplayName") ?? talentName,
                    ParseLocText(row, "Description") ?? "",
                    tree,
                    maxRank,
                    defaultUnlocked));
            }
        }
        catch (Exception)
        {
            // Missing/corrupt table just means talents show by RowName with an uncapped rank.
        }

        return talents;
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
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out var name) || name.GetString() is not { } rowName)
                {
                    continue;
                }

                var category = row.TryGetProperty("Category", out var categoryRef) && categoryRef.TryGetProperty("Value", out var categoryValue)
                    ? categoryValue.GetString() ?? ""
                    : "";
                var goalCount = row.TryGetProperty("GoalCount", out var goal) && goal.ValueKind == JsonValueKind.Number ? goal.GetInt32() : 0;

                accolades.TryAdd(rowName, new AccoladeInfo(
                    ParseLocText(row, "DisplayName") ?? rowName,
                    ParseLocText(row, "Description") ?? "",
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
            foreach (var row in document.RootElement.GetProperty("Rows").EnumerateArray())
            {
                if (!row.TryGetProperty("Name", out var name) || name.GetString() is not { } rowName)
                {
                    continue;
                }

                var pointsRequired = row.TryGetProperty("TotalPointsRequired", out var points) && points.ValueKind == JsonValueKind.Number ? points.GetInt32() : 0;
                var isBoss = row.TryGetProperty("bIsBoss", out var boss) && boss.ValueKind == JsonValueKind.True;
                var imagePath = row.TryGetProperty("Image", out var image) && image.GetString() is { Length: > 0 } img ? img : null;

                creatures.TryAdd(rowName, new BestiaryCreatureInfo(
                    ParseLocText(row, "CreatureName") ?? rowName,
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
                foreach (var row in itemableDoc.RootElement.GetProperty("Rows").EnumerateArray())
                {
                    if (row.TryGetProperty("Name", out var name) && name.GetString() is { } itemableRowName)
                    {
                        var weight = row.TryGetProperty("Weight", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
                        var maxStack = row.TryGetProperty("MaxStack", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 1;
                        var iconPath = row.TryGetProperty("Icon", out var icon) && icon.GetString() is { Length: > 0 } ip ? ip : null;
                        itemableInfo[itemableRowName] = (ParseLocText(row, "DisplayName") ?? itemableRowName, weight, maxStack, iconPath);
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

    /// <summary>The tables wrap localized text as NSLOCTEXT("Table", "Key", "The English text") — the last quoted argument is the display string.</summary>
    private static string? ParseLocText(JsonElement row, string property)
    {
        if (!row.TryGetProperty(property, out var value) || value.GetString() is not { } raw)
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(raw, "\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\)\\s*$");
        return match.Success ? match.Groups[1].Value.Replace("\\\"", "\"") : raw;
    }
}

/// <param name="MaxRank">The most ranks this talent can hold, from its own Rewards tiers — what the editor caps rank controls at.</param>
/// <param name="IsDefaultUnlocked">The row's own bDefaultUnlocked — the game grants it from the start WITHOUT writing it into the save, so the editor must show it as unlocked even at rank 0.</param>
public sealed record TalentInfo(string DisplayName, string Description, string Tree, int MaxRank, bool IsDefaultUnlocked);

public sealed record AccoladeInfo(string DisplayName, string Description, string Category, int GoalCount);

/// <param name="PointsRequired">The row's own TotalPointsRequired — what the editor treats as "maxed out" for a Set to max action.</param>
/// <param name="ImagePath">The row's own raw "Image" field (a "/Game/…/T_Bestiary_Xxx.T_Bestiary_Xxx" texture reference) — null for a row that never carried one, or when the table itself is missing.</param>
public sealed record BestiaryCreatureInfo(string DisplayName, int PointsRequired, bool IsBoss, string? ImagePath);

/// <param name="IconPath">The Itemable row's own raw "Icon" field (a "/Game/…/ITEM_Xxx.ITEM_Xxx" texture reference) — null for a row that never carried one, or when the table itself is missing.</param>
public sealed record ItemInfo(string DisplayName, int Weight, int MaxStack, string? IconPath);
