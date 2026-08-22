using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One row in Library's UE4SS tab. IsEnabled is the PENDING checkbox state — starts equal to
/// RealIsEnabled (what's actually true right now) and only diverges once the user toggles it;
/// clicking Apply is what actually moves the mod and syncs RealIsEnabled back to match. IsDirty
/// drives whether the page-level Apply button is enabled at all.
/// </summary>
public sealed partial class Ue4ssModRowViewModel : ObservableObject
{
    private readonly Action _onDirtyChanged;

    public Ue4ssModRowViewModel(string name, bool realIsEnabled, Action onDirtyChanged)
    {
        Name = name;
        RealIsEnabled = realIsEnabled;
        _isEnabled = realIsEnabled;
        _onDirtyChanged = onDirtyChanged;
    }

    public string Name { get; }

    public bool RealIsEnabled { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public bool IsDirty => IsEnabled != RealIsEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDirty));
        _onDirtyChanged();
    }
}
