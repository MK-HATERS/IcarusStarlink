using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Skins;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SettingsViewModel
{
    // --- Custom skin (big-plan item 6) ---

    /// <summary>Display order + plain-language description per token, so the editor reads as "what part of the UI is this" rather than a bare brush-key dump. Tokens the themes define but this list doesn't know still show (appended at the end, keyed name as its own description) — the editor can't silently drop a token added to the theme files later.</summary>
    private static readonly (string Key, string Description)[] SkinTokenOrder =
    [
        ("WindowBackgroundBrush", "Window background"),
        ("PanelBackgroundBrush", "Side panels and nav rail"),
        ("ContentBackgroundBrush", "Page content background"),
        ("CardBackgroundBrush", "Cards and input boxes"),
        ("CardRaisedBackgroundBrush", "Raised cards (one step lighter)"),
        ("ForegroundBrush", "Main text"),
        ("SecondaryForegroundBrush", "Secondary text"),
        ("FaintForegroundBrush", "Faint text and disabled items"),
        ("AccentBrush", "Accent — buttons and highlights"),
        ("AccentForegroundBrush", "Text on accent buttons"),
        ("AccentSoftBrush", "Soft accent fill (badges)"),
        ("BorderBrush", "Borders"),
        ("NavItemHoverBrush", "Nav item hover"),
        ("NavItemSelectedBrush", "Nav item selected"),
        ("DangerBrush", "Danger / removals"),
        ("DangerSoftBrush", "Soft danger fill (badges)"),
        ("SuccessBrush", "Success / additions"),
        ("SuccessSoftBrush", "Soft success fill (badges)"),
        ("InfoBrush", "Info"),
        ("WarningBrush", "Waiting / needs a look"),
        ("WarningSoftBrush", "Soft waiting fill (badges)"),
        ("TagExmodBrush", "EXMOD tag"),
        ("TagPakBrush", "Pak tag"),
        ("TagUe4ssBrush", "UE4SS tag"),
        ("TagPakUe4ssBrush", "Pak + UE4SS tag"),
        ("SourceNexusBrush", "Source: Nexus"),
        ("SourceDatabaseBrush", "Source: IMM Database"),
        ("GroupColor0Brush", "Group heading colour 1"),
        ("GroupColor1Brush", "Group heading colour 2"),
        ("GroupColor2Brush", "Group heading colour 3"),
        ("GroupColor3Brush", "Group heading colour 4"),
        ("GroupColor4Brush", "Group heading colour 5"),
        ("GroupColor5Brush", "Group heading colour 6"),
    ];

    public System.Collections.ObjectModel.ObservableCollection<SkinTokenViewModel> SkinTokens { get; } = [];

    [ObservableProperty]
    private string? _skinStatusMessage;

    private void LoadSkinTokens()
    {
        var defaults = _themeService.GetThemeColors("Icarus");
        var saved = _customSkinStore.Load()?.Colors ?? [];

        SkinTokens.Clear();
        foreach (var (key, description) in SkinTokenOrder)
        {
            if (!defaults.ContainsKey(key))
            {
                continue;
            }

            SkinTokens.Add(new SkinTokenViewModel(key, description)
            {
                Hex = saved.GetValueOrDefault(key) ?? defaults[key],
            });
        }

        foreach (var (key, hex) in defaults)
        {
            if (!SkinTokens.Any(t => t.Key == key))
            {
                SkinTokens.Add(new SkinTokenViewModel(key, key) { Hex = saved.GetValueOrDefault(key) ?? hex });
            }
        }
    }

    [RelayCommand]
    private void SaveSkin()
    {
        var invalid = SkinTokens.Where(t => !ThemeService.TryParseColor(t.Hex.Trim(), out _)).Select(t => t.Key).ToList();
        if (invalid.Count > 0)
        {
            SkinStatusMessage = $"Not saved — these aren't valid hex colors (#RRGGBB or #AARRGGBB): {string.Join(", ", invalid)}.";
            return;
        }

        try
        {
            _customSkinStore.Save(new CustomSkin
            {
                Colors = SkinTokens.ToDictionary(t => t.Key, t => t.Hex.Trim()),
            });

            if (_settingsService.Current.ThemeName == ThemeService.CustomThemeName)
            {
                _themeService.ApplyTheme(ThemeService.CustomThemeName);
                SkinStatusMessage = "Skin saved and applied.";
            }
            else
            {
                SkinStatusMessage = "Skin saved — pick the Custom theme (top right) to see it.";
            }
        }
        catch (Exception ex)
        {
            SkinStatusMessage = $"Couldn't save the skin: {ex.Message}";
        }
    }

    /// <summary>Fills the editor's boxes from a built-in theme's own values as a starting point — doesn't save or apply anything until Save skin is clicked.</summary>
    [RelayCommand]
    private void CopyThemeIntoSkin(string themeName)
    {
        var colors = _themeService.GetThemeColors(themeName);
        foreach (var token in SkinTokens)
        {
            if (colors.TryGetValue(token.Key, out var hex))
            {
                token.Hex = hex;
            }
        }

        SkinStatusMessage = $"Copied the {themeName} theme's values — adjust and Save skin.";
    }

    [RelayCommand]
    private void OpenSkinFile()
    {
        try
        {
            if (_customSkinStore.Load() is null)
            {
                _customSkinStore.Save(new CustomSkin
                {
                    Colors = SkinTokens.ToDictionary(t => t.Key, t => t.Hex.Trim()),
                });
            }

            if (UrlOpener.TryOpen(_customSkinStore.FilePath) is { } ex)
            {
                SkinStatusMessage = $"Couldn't open the skin file: {ex.Message}";
            }
        }
        catch (Exception ex)
        {
            SkinStatusMessage = $"Couldn't open the skin file: {ex.Message}";
        }
    }

    /// <summary>Re-reads the skin file into the editor (and re-applies it if Custom is active) — the companion to hand-editing via OpenSkinFile.</summary>
    [RelayCommand]
    private void ReloadSkinFile()
    {
        LoadSkinTokens();
        if (_settingsService.Current.ThemeName == ThemeService.CustomThemeName)
        {
            _themeService.ApplyTheme(ThemeService.CustomThemeName);
        }

        SkinStatusMessage = "Skin file reloaded.";
    }

    /// <summary>Settings › Appearance › Show pop-up messages. Saved straight away like Performance tracking; turning it off also clears any cards showing.</summary>
    [ObservableProperty]
    private bool _showPopUpMessages;

    partial void OnShowPopUpMessagesChanged(bool value) =>
        _settingsService.SetAndSave(s => s.ShowPopUpMessages, (s, v) => s.ShowPopUpMessages = v, value);

    /// <summary>Settings › Appearance › Size of text and controls: 90% to 150%.</summary>
    public IReadOnlyList<UiScaleChoice> UiScaleChoices => UiScale.Choices;

    /// <summary>
    /// The chosen size, as one of UiScaleChoices' values (a hand-edited value in between shows as
    /// the nearest). Saved straight away and applied to every open window without a restart.
    /// </summary>
    public double SelectedUiScale
    {
        get
        {
            var saved = UiScale.Normalize(_settingsService.Current.UiScale);
            return UiScale.Choices.MinBy(c => Math.Abs(c.Value - saved))!.Value;
        }
        set
        {
            var scale = UiScale.Normalize(value);
            if (_settingsService.SetAndSave(s => s.UiScale, (s, v) => s.UiScale = v, scale))
            {
                UiScale.Set(scale);
                OnPropertyChanged();
            }
        }
    }
}
