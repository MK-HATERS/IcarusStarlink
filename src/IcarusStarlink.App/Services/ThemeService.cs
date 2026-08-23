using System.Windows;
using System.Windows.Media;
using IcarusStarlink.Core.Skins;

namespace IcarusStarlink.App.Services;

public sealed class ThemeService(ICustomSkinStore customSkinStore) : IThemeService
{
    public const string CustomThemeName = "Custom";

    private const string DefaultTheme = "Icarus";

    private static readonly IReadOnlyDictionary<string, Uri> ThemeUris = new Dictionary<string, Uri>
    {
        ["Icarus"] = new("/IcarusStarlink.App;component/Themes/Icarus.xaml", UriKind.Relative),
        ["Dark"] = new("/IcarusStarlink.App;component/Themes/Dark.xaml", UriKind.Relative),
        ["Light"] = new("/IcarusStarlink.App;component/Themes/Light.xaml", UriKind.Relative),
    };

    public IReadOnlyList<string> AvailableThemes { get; } = [.. ThemeUris.Keys, CustomThemeName];

    /// <summary>The code-built skin overlay currently merged in (if any) — it has no Source URI, so it can only be identified for removal by reference, unlike the built-in dictionaries.</summary>
    private ResourceDictionary? _customOverlay;

    public void ApplyTheme(string themeName)
    {
        var mergedDictionaries = Application.Current.Resources.MergedDictionaries;

        var existingTheme = mergedDictionaries.FirstOrDefault(d => d.Source is not null && ThemeUris.Values.Contains(d.Source));
        if (existingTheme is not null)
        {
            mergedDictionaries.Remove(existingTheme);
        }

        if (_customOverlay is not null)
        {
            mergedDictionaries.Remove(_customOverlay);
            _customOverlay = null;
        }

        if (themeName == CustomThemeName)
        {
            // The Icarus theme goes underneath as the fallback layer: any token the skin file
            // doesn't define — or defines with an unparseable hex — still resolves, so a
            // hand-edited file can degrade colors but never break the UI.
            mergedDictionaries.Add(new ResourceDictionary { Source = ThemeUris[DefaultTheme] });

            var skin = customSkinStore.Load();
            if (skin is null)
            {
                // First use: materialize a full starting-point file from the Icarus values, so
                // "edit the skin" always means editing real, complete content rather than
                // guessing token names into an empty file.
                skin = new CustomSkin { Colors = new Dictionary<string, string>(GetThemeColors(DefaultTheme)) };
                customSkinStore.Save(skin);
            }

            var overlay = new ResourceDictionary();
            foreach (var (key, hex) in skin.Colors)
            {
                if (TryParseColor(hex, out var color))
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    overlay[key] = brush;
                }
            }

            mergedDictionaries.Add(overlay);
            _customOverlay = overlay;
            return;
        }

        if (!ThemeUris.TryGetValue(themeName, out var uri))
        {
            uri = ThemeUris[DefaultTheme];
        }

        mergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }

    public IReadOnlyDictionary<string, string> GetThemeColors(string themeName)
    {
        if (!ThemeUris.TryGetValue(themeName, out var uri))
        {
            uri = ThemeUris[DefaultTheme];
        }

        var dictionary = new ResourceDictionary { Source = uri };
        var colors = new Dictionary<string, string>();
        foreach (var key in dictionary.Keys)
        {
            if (key is string name && dictionary[key] is SolidColorBrush brush)
            {
                colors[name] = FormatColor(brush.Color);
            }
        }

        return colors;
    }

    public static bool TryParseColor(string hex, out Color color)
    {
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (FormatException)
        {
        }

        color = default;
        return false;
    }

    private static string FormatColor(Color color) =>
        color.A == 0xFF ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
