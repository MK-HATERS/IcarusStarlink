using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One entry in the account-wide MetaInventory bank — view + delete only, deliberately not
/// per-field editable. A real item's own JSON (ItemDynamicData, CustomProperties.Alterations,
/// LivingItemSlots, DatabaseGUID, ...) is deep and format-sensitive enough that hand-editing it
/// risks producing an item the game can't parse; Node is kept and written back completely
/// untouched for anything that survives, so the only real edit this offers — removing an item —
/// can never corrupt one that stays.
///
/// ObservableObject (not a plain record-style class the way this was before) purely so Icon can be
/// filled in after construction — SavesViewModel resolves it through the base-game content provider
/// asynchronously, off the UI thread, and this row must be able to raise PropertyChanged once that
/// finishes; every other field here is still set once at construction and never changes.
/// </summary>
public sealed partial class SaveInventoryItemViewModel : ObservableObject
{
    public JsonObject Node { get; }

    public string DisplayName { get; }

    public string RowName { get; }

    public string WeightDisplay { get; }

    public string MaxStackDisplay { get; }

    /// <summary>D_Itemable's own raw "Icon" field for this item's RowName (a "/Game/…/ITEM_Xxx.ITEM_Xxx" texture reference) — null when the current data extraction has no Itemable entry for this item, or no Icon on it. What SavesViewModel's own icon-resolution pass reads to fill in Icon below.</summary>
    public string? IconPath { get; }

    /// <summary>Null until (if ever) SavesViewModel's own background resolution decodes IconPath through the base-game content provider — a row shows text-only for however long that takes, or forever if it never resolves (missing game install, unmatched path, not a real texture).</summary>
    [ObservableProperty]
    private BitmapImage? _icon;

    public SaveInventoryItemViewModel(JsonObject node, string displayName, string rowName, int weight, int maxStack, string? iconPath)
    {
        Node = node;
        DisplayName = displayName;
        RowName = rowName;
        WeightDisplay = weight > 0 ? $"{weight / 1000.0:0.##} kg" : "";
        MaxStackDisplay = maxStack > 1 ? $"stacks to {maxStack}" : "";
        IconPath = iconPath;
    }
}
