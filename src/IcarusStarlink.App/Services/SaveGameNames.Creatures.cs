using System.IO;
using System.Text.Json;

namespace IcarusStarlink.App.Services;

public sealed partial class SaveGameNames
{
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
        // Biomes in the order the table first lists them (Forest, Arctic, Desert, ...); "Other" last.
        var groupOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Other"] = int.MaxValue - 1 };
        foreach (var (rowName, row) in RowsOf(table))
        {
            var pointsRequired = TryGet(row, defaults, "TotalPointsRequired", out var points) && points.ValueKind == JsonValueKind.Number ? points.GetInt32() : 0;
            var isBoss = TryGet(row, defaults, "bIsBoss", out var boss) && boss.ValueKind == JsonValueKind.True;
            var imagePath = TryGet(row, defaults, "Image", out var image) && image.ValueKind == JsonValueKind.String && image.GetString() is { Length: > 0 } img && img != "None" ? img : null;

            var group = BiomeGroup(RowNames(row, defaults, "Biomes").FirstOrDefault());
            groupOrder.TryAdd(group, groupOrder.Count);
            creatures.TryAdd(rowName, new BestiaryCreatureInfo(
                ParseLocText(row, "CreatureName", defaults) ?? rowName,
                pointsRequired,
                isBoss,
                imagePath,
                group,
                groupOrder[group]));
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
}
