using System.Globalization;
using System.Windows.Data;
using IcarusStarlink.Core.Profiles;

namespace IcarusStarlink.App.Converters;

/// <summary>Phase 10: short pill labels for Merge & Install's leveled gameplay options — the raw enum names (TwentyFivePercent, Level1, ...) are too long for a small pill button.</summary>
public sealed class GameplayLevelToShortLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        BoostLevel.Off or CraftCostReduction.Off => "Off",
        BoostLevel.Level1 => "L1",
        BoostLevel.Level2 => "L2",
        CraftCostReduction.TwentyFivePercent => "25%",
        CraftCostReduction.FiftyPercent => "50%",
        _ => value?.ToString() ?? "",
    };

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
