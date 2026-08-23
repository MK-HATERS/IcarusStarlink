using System.Text.Json.Nodes;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One entry in the account-wide MetaInventory bank — view + delete only, deliberately not
/// per-field editable. A real item's own JSON (ItemDynamicData, CustomProperties.Alterations,
/// LivingItemSlots, DatabaseGUID, ...) is deep and format-sensitive enough that hand-editing it
/// risks producing an item the game can't parse; Node is kept and written back completely
/// untouched for anything that survives, so the only real edit this offers — removing an item —
/// can never corrupt one that stays.
/// </summary>
public sealed class SaveInventoryItemViewModel(JsonObject node, string displayName, string rowName, int weight, int maxStack)
{
    public JsonObject Node { get; } = node;

    public string DisplayName { get; } = displayName;

    public string RowName { get; } = rowName;

    public string WeightDisplay => weight > 0 ? $"{weight / 1000.0:0.##} kg" : "";

    public string MaxStackDisplay => maxStack > 1 ? $"stacks to {maxStack}" : "";
}
