namespace IcarusStarlink.App.Services;

public interface IThemeService
{
    IReadOnlyList<string> AvailableThemes { get; }

    void ApplyTheme(string themeName);

    /// <summary>
    /// The color tokens (key → "#RRGGBB"/"#AARRGGBB" hex) a built-in theme defines — Settings'
    /// custom-skin editor uses this both to enumerate which tokens exist (so the editor and the
    /// theme files can't drift apart) and to offer "copy a built-in theme's values" as a starting
    /// point. An unknown theme name returns the default theme's tokens.
    /// </summary>
    IReadOnlyDictionary<string, string> GetThemeColors(string themeName);
}
