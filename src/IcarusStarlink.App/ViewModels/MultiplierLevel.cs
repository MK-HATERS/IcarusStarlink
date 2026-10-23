namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The Stacks/Slots multiplier pills' own fixed values — a UI-only concept, not part of
/// GameplayOptions itself (that stays a free `double?`, since a hand-edited profile or an imported
/// patch could carry any positive multiplier, not just one of these five presets). A real enum,
/// not a `double?` list with null="Off": WPF's Selector treats SelectedItem==null as "nothing
/// selected" rather than "the null entry is selected", so a nullable-double pill never showed Off
/// as highlighted on load — a real bug, found live, that this enum sidesteps entirely by giving
/// "Off" a real, non-null value like every other pill selector on this page already has.
/// </summary>
public enum MultiplierLevel
{
    Off,
    Times2,
    Times3,
    Times4,
    Times5,
    Times10,
}

public static class MultiplierLevelExtensions
{
    public static double? ToMultiplier(this MultiplierLevel level) => level switch
    {
        MultiplierLevel.Times2 => 2,
        MultiplierLevel.Times3 => 3,
        MultiplierLevel.Times4 => 4,
        MultiplierLevel.Times5 => 5,
        MultiplierLevel.Times10 => 10,
        _ => null,
    };

    /// <summary>The closest preset for a stored value — an exact match for anything this UI itself ever wrote. A value from outside the preset set (hand-edited JSON, an imported patch) falls back to Off rather than guessing a nearest match, since silently rounding someone's real number to a different one would be worse than just not pre-selecting a pill for it (the page keeps the real value until that option is changed: see MergeInstallViewModel._keptStacksMultiplier).</summary>
    public static MultiplierLevel FromMultiplier(double? value) => value switch
    {
        2 => MultiplierLevel.Times2,
        3 => MultiplierLevel.Times3,
        4 => MultiplierLevel.Times4,
        5 => MultiplierLevel.Times5,
        10 => MultiplierLevel.Times10,
        _ => MultiplierLevel.Off,
    };
}
