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
        ["Icarus"] = new("/IcarusStarlink;component/Themes/Icarus.xaml", UriKind.Relative),
        ["Dark"] = new("/IcarusStarlink;component/Themes/Dark.xaml", UriKind.Relative),
        ["Light"] = new("/IcarusStarlink;component/Themes/Light.xaml", UriKind.Relative),
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
            var baseTheme = new ResourceDictionary { Source = ThemeUris[DefaultTheme] };
            mergedDictionaries.Add(baseTheme);

            var skin = customSkinStore.Load();
            if (skin is null)
            {
                // First use: materialize a full starting-point file from the Icarus values, so
                // "edit the skin" always means editing real, complete content rather than
                // guessing token names into an empty file.
                skin = new CustomSkin { Colors = new Dictionary<string, string>(GetThemeColors(DefaultTheme)) };
                try
                {
                    customSkinStore.Save(skin);
                }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
                {
                    // Applied from memory; the file is written the next time the skin is saved.
                }
            }

            // Only the theme's colour keys: a hand-added key named like a style would otherwise
            // replace that style with a brush.
            var knownKeys = baseTheme.Keys.OfType<string>().Where(key => baseTheme[key] is SolidColorBrush).ToHashSet();
            var overlay = new ResourceDictionary();
            foreach (var (key, color) in SkinColorsToApply(skin, knownKeys))
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                overlay[key] = brush;
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

        var colors = new Dictionary<string, string>();
        ResourceDictionary dictionary;
        try
        {
            dictionary = new ResourceDictionary { Source = uri };
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.IO.IOException)
        {
            // Windows closing every window at once (shutting down, signing out) while Settings was
            // being built: WPF can no longer read its own resources ("The URI prefix is not
            // recognized"). The skin editor's colours are only a starting point.
            return colors;
        }

        foreach (var key in dictionary.Keys)
        {
            if (key is string name && dictionary[key] is SolidColorBrush brush)
            {
                colors[name] = FormatColor(brush.Color);
            }
        }

        return colors;
    }

    /// <summary>
    /// The skin colours that go into the overlay: only keys the theme has as colours, and only
    /// values that parse. A skin file is hand-editable, so <see cref="CustomSkin.Colors"/> can be null.
    /// </summary>
    internal static IReadOnlyList<KeyValuePair<string, Color>> SkinColorsToApply(CustomSkin skin, IReadOnlyCollection<string> knownKeys)
    {
        var colors = new List<KeyValuePair<string, Color>>();
        foreach (var (key, text) in skin.Colors ?? [])
        {
            if (knownKeys.Contains(key) && TryParseColor(text, out var color))
            {
                colors.Add(new(key, color));
            }
        }

        return colors;
    }

    /// <summary>A colour as a skin holds it ("#RRGGBB", "#AARRGGBB", a colour name); false for anything else, never a throw.</summary>
    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();

        // A skin never needs a colour profile, and WPF would load one from any path or URL.
        if (trimmed.StartsWith("ContextColor", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            if (ColorConverter.ConvertFromString(trimmed) is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // An unfinished scRGB value such as sc#1 throws InvalidOperationException, not FormatException.
        }

        return false;
    }

    private static string FormatColor(Color color) =>
        color.A == 0xFF ? $"#{color.R:X2}{color.G:X2}{color.B:X2}" : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
