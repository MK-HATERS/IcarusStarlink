using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>Another copy of the mod the same changes can be saved to.</summary>
public sealed partial class ModConfigAlsoSave(ModConfigLocation location) : ObservableObject
{
    public ModConfigLocation Location { get; } = location;

    [ObservableProperty]
    private bool _isChecked;
}
