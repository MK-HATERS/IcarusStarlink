using System.Windows;

namespace IcarusStarlink.App.Services;

public sealed class ThemeService : IThemeService
{
    private const string DefaultTheme = "Icarus";

    private static readonly IReadOnlyDictionary<string, Uri> ThemeUris = new Dictionary<string, Uri>
    {
        ["Icarus"] = new("/IcarusStarlink.App;component/Themes/Icarus.xaml", UriKind.Relative),
        ["Dark"] = new("/IcarusStarlink.App;component/Themes/Dark.xaml", UriKind.Relative),
        ["Light"] = new("/IcarusStarlink.App;component/Themes/Light.xaml", UriKind.Relative),
    };

    public IReadOnlyList<string> AvailableThemes { get; } = ThemeUris.Keys.ToList();

    public void ApplyTheme(string themeName)
    {
        if (!ThemeUris.TryGetValue(themeName, out var uri))
        {
            uri = ThemeUris[DefaultTheme];
        }

        var mergedDictionaries = Application.Current.Resources.MergedDictionaries;
        var existingTheme = mergedDictionaries.FirstOrDefault(d => d.Source is not null && ThemeUris.Values.Contains(d.Source));
        if (existingTheme is not null)
        {
            mergedDictionaries.Remove(existingTheme);
        }

        mergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }
}
