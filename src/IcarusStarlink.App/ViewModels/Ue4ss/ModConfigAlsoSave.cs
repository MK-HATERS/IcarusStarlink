using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Another copy of the mod the same changes can be saved to.</summary>
public sealed partial class ModConfigAlsoSave(ModConfigLocation location) : ObservableObject
{
    public ModConfigLocation Location { get; } = location;

    /// <summary>What ticking it writes there, shown on the tick.</summary>
    public string ToolTip { get; init; } = "Only the settings you changed are written there; its other settings stay as they are";

    [ObservableProperty]
    private bool _isChecked;
}
