namespace IcarusStarlink.App.Services;

public interface IThemeService
{
    IReadOnlyList<string> AvailableThemes { get; }

    void ApplyTheme(string themeName);
}
