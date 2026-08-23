using System.Globalization;
using System.Windows.Data;
using IcarusStarlink.Core.Profiles;

namespace IcarusStarlink.App.Converters;

/// <summary>Phase 10: short pill labels for Merge & Install's leveled gameplay options — the raw enum names (TwentyFivePercent, Level1, ...) are too long for a small pill button. Also handles the Stacks/Slots multiplier pills' own double? options (null = "Off", else "Nx").</summary>
public sealed class GameplayLevelToShortLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        BoostLevel.Off or CraftCostReduction.Off or XpBoostLevel.Off => "Off",
        BoostLevel.Level1 or XpBoostLevel.Level1 => "L1",
        BoostLevel.Level2 or XpBoostLevel.Level2 => "L2",
        XpBoostLevel.Level3 => "L3",
        CraftCostReduction.TwentyFivePercent => "25%",
        CraftCostReduction.FiftyPercent => "50%",
        CraftCostReduction.Creative => "Free",
        null => "Off",
        double multiplier => $"{multiplier:0.#}x",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
