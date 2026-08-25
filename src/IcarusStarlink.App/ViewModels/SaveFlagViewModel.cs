using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One unlockable flag chip — the save stores just the int ID; the name comes from the game's own flag table (row index = ID). Toggling stays in the ViewModel until save-time apply, like every other save-editor edit.</summary>
public sealed partial class SaveFlagViewModel : ObservableObject, IDirtyTrackable
{
    private readonly Action _onDirtyChanged;
    private bool _originalUnlocked;

    public int Id { get; }

    public string Name { get; }

    [ObservableProperty]
    private bool _isUnlocked;

    public bool IsDirty => IsUnlocked != _originalUnlocked;

    public SaveFlagViewModel(int id, string name, bool isUnlocked, Action onDirtyChanged)
    {
        Id = id;
        Name = name;
        _isUnlocked = isUnlocked;
        _originalUnlocked = isUnlocked;
        _onDirtyChanged = onDirtyChanged;
    }

    partial void OnIsUnlockedChanged(bool value) => _onDirtyChanged();

    public void MarkClean() => _originalUnlocked = IsUnlocked;
}
