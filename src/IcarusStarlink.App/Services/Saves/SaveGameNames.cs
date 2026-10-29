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
public sealed partial class SaveGameNames(string dataFolder)
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
