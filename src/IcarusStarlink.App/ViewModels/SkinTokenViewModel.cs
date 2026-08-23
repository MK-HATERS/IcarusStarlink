using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One row in Settings' custom-skin editor: a semantic color token, its plain-language description, and its editable hex value (the live swatch next to it renders via HexToBrushConverter).</summary>
public sealed partial class SkinTokenViewModel(string key, string description) : ObservableObject
{
    public string Key { get; } = key;

    public string Description { get; } = description;

    [ObservableProperty]
    private string _hex = "";
}
