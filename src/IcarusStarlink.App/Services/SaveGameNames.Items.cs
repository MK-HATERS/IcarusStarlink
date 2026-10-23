using System.Text.Json;

namespace IcarusStarlink.App.Services;

public sealed partial class SaveGameNames
{
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
}
