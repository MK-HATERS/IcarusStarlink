using System.IO;
using System.Text.Json;

namespace IcarusStarlink.App.Services;

public sealed partial class SaveGameNames
{
    // --- Talent trees as the game draws them (D_Talents Position, Size, RequiredTalents,
    //     DrawMethodOverride, RequiredRank; D_TalentRanks Investment) ---

    private static readonly string TalentRanksTable = Path.Combine("Talents", "D_TalentRanks.json");

    private IReadOnlyDictionary<string, IReadOnlyList<TalentNodeLayout>>? _talentLayouts;
    private IReadOnlyList<TalentRankGate>? _talentRankGates;
    private IReadOnlyDictionary<string, string>? _talentIconPaths;

    /// <summary>
    /// Tree row ("Survival_Resources") -> every node the game draws in it, with its place on the
    /// tree. Reroute and MutuallyExclusive rows are in here (IsRoutePoint): they're the bends the
    /// game's lines go through, never talents. Empty when the tables aren't extracted.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TalentNodeLayout>> TalentLayouts => Cached(ref _talentLayouts, LoadTalentLayouts);

    /// <summary>Novice, Apprentice, Journeyman, Master: the points spent in a tree each needs (D_TalentRanks Investment, Novice 0).</summary>
    public IReadOnlyList<TalentRankGate> TalentRankGates => Cached(ref _talentRankGates, LoadTalentRankGates);

    /// <summary>
    /// Talent row -> the picture the game shows for it: its own Icon; a blueprint's is its item's
    /// (ExtraData -> D_Itemable Icon); workshop research's is the item it researches
    /// (ExtraData -> D_WorkshopItems Item -> D_ItemsStatic -> D_Itemable Icon).
    /// </summary>
    public IReadOnlyDictionary<string, string> TalentIconPaths => Cached(ref _talentIconPaths, LoadTalentIconPaths);

    private IReadOnlyDictionary<string, IReadOnlyList<TalentNodeLayout>> LoadTalentLayouts() => Safe<IReadOnlyDictionary<string, IReadOnlyList<TalentNodeLayout>>>(() =>
    {
        var byTree = new Dictionary<string, List<TalentNodeLayout>>(StringComparer.OrdinalIgnoreCase);
        var defaults = DefaultsOf(TalentsTable);
        foreach (var (name, row) in RowsOf(TalentsTable))
        {
            if (RowNameOf(row, "TalentTree") is not { } tree || !TryGet(row, defaults, "Position", out var position) || position.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = TryGet(row, defaults, "TalentType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var size = TryGet(row, defaults, "Size", out var s) && s.ValueKind == JsonValueKind.Object ? s : (JsonElement?)null;
            var draw = TryGet(row, defaults, "DrawMethodOverride", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            var rank = RowNameOf(row, "RequiredRank") ?? (defaults is { } def ? RowNameOf(def, "RequiredRank") : null);
            (byTree.TryGetValue(tree, out var list) ? list : byTree[tree] = []).Add(new TalentNodeLayout(
                name,
                Number(position, "X"),
                Number(position, "Y"),
                size is { } sz ? Number(sz, "X") : 64,
                size is { } sz2 ? Number(sz2, "Y") : 64,
                [.. RowNames(row, defaults, "RequiredTalents").Where(r => r != "None")],
                draw,
                rank is "None" ? null : rank,
                type is "Reroute" or "MutuallyExclusive"));
        }

        return byTree.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TalentNodeLayout>)kv.Value, StringComparer.OrdinalIgnoreCase);
    }, () => new Dictionary<string, IReadOnlyList<TalentNodeLayout>>(StringComparer.OrdinalIgnoreCase));

    private static double Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private IReadOnlyList<TalentRankGate> LoadTalentRankGates() => Safe<IReadOnlyList<TalentRankGate>>(() =>
    {
        var defaults = DefaultsOf(TalentRanksTable);
        return [.. RowsOf(TalentRanksTable).Select(r => new TalentRankGate(
            r.Name,
            ParseLocText(r.Row, "DisplayName", defaults) ?? r.Name,
            TryGet(r.Row, defaults, "Investment", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : 0))];
    }, () => []);

    private IReadOnlyDictionary<string, string> LoadTalentIconPaths() => Safe<IReadOnlyDictionary<string, string>>(() =>
    {
        var icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var itemableDefaults = DefaultsOf(ItemableTable);
        var itemableIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, row) in RowsOf(ItemableTable))
        {
            if (IconOf(row, itemableDefaults) is { } icon)
            {
                itemableIcons[rowName] = icon;
            }
        }

        var workshopIcons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var templateToStatic = TemplateStaticRows;
        foreach (var (workshopRow, row) in RowsOf(WorkshopItemsTable))
        {
            if (RowNameOf(row, "Item") is { } item
                && (Items.GetValueOrDefault(templateToStatic.GetValueOrDefault(item, item)) ?? Items.GetValueOrDefault(item)) is { IconPath: { } icon })
            {
                workshopIcons[workshopRow] = icon;
            }
        }

        var defaults = DefaultsOf(TalentsTable);
        foreach (var (name, row) in RowsOf(TalentsTable))
        {
            var extra = RowNameOf(row, "ExtraData");
            var icon = IconOf(row, defaults)
                ?? (extra is not null ? itemableIcons.GetValueOrDefault(extra) ?? workshopIcons.GetValueOrDefault(extra) : null);
            if (icon is not null)
            {
                icons[name] = icon;
            }
        }

        return icons;
    }, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static string? IconOf(JsonElement row, JsonElement? defaults) =>
        TryGet(row, defaults, "Icon", out var icon) && icon.ValueKind == JsonValueKind.String && icon.GetString() is { Length: > 0 } path && path != "None" ? path : null;
}

/// <summary>One node of a talent tree as the game places it.</summary>
/// <param name="X">Left edge in the game's tree units (a talent tree is about 900 by 1,400).</param>
/// <param name="Requires">The rows the game draws a line from (RequiredTalents).</param>
/// <param name="DrawMethod">How that line bends: XThenY (across, then down), YThenX, or straight.</param>
/// <param name="RequiredRank">Novice, Apprentice, Journeyman or Master; null when the row doesn't say.</param>
/// <param name="IsRoutePoint">A bend the game's lines go through (Reroute, MutuallyExclusive), not a talent.</param>
public sealed record TalentNodeLayout(string Row, double X, double Y, double Width, double Height, IReadOnlyList<string> Requires,
    string DrawMethod, string? RequiredRank, bool IsRoutePoint);

/// <param name="Investment">Points spent in the tree before talents of this rank can be bought.</param>
public sealed record TalentRankGate(string Row, string Display, int Investment);
