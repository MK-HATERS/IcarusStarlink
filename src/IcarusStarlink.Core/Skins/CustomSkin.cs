namespace IcarusStarlink.Core.Skins;

/// <summary>
/// A user-authorable skin (big-plan item 6): a plain map of the app's semantic color tokens
/// (AccentBrush, CardBackgroundBrush, …) to hex colors ("#RRGGBB" or "#AARRGGBB"). The three
/// built-in themes are compiled XAML a user can't edit — this is the runtime-loaded fourth slot.
/// Tokens missing from the map (or carrying an unparseable value) fall back to the Icarus theme's
/// own values, so a hand-edited file can never leave the app unreadable.
/// </summary>
public sealed class CustomSkin
{
    public Dictionary<string, string> Colors { get; init; } = [];
}
