using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Services;

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

    /// <summary>What the flag does / where it comes from (FlagInfo.Detail); empty for DLC packs.</summary>
    public string Detail { get; }

    public FlagKind Kind { get; }

    /// <summary>The game's own row name, for the tooltip.</summary>
    public string RowName { get; }

    /// <summary>Talent-granted flags follow their talent; editing one alone is pointless, so it's shown, not switched.</summary>
    public bool IsEditable => Kind != FlagKind.FromTalent;

    public SaveFlagViewModel(int id, string name, bool isUnlocked, Action onDirtyChanged)
        : this(id, new FlagInfo(name, name, "", FlagKind.Marker), isUnlocked, onDirtyChanged)
    {
    }

    public SaveFlagViewModel(int id, FlagInfo info, bool isUnlocked, Action onDirtyChanged)
    {
        Id = id;
        Name = info.Display;
        Detail = info.Detail;
        Kind = info.Kind;
        RowName = info.RowName;
        _isUnlocked = isUnlocked;
        _originalUnlocked = isUnlocked;
        _onDirtyChanged = onDirtyChanged;
    }

    partial void OnIsUnlockedChanged(bool value) => _onDirtyChanged();

    public void MarkClean() => _originalUnlocked = IsUnlocked;
}
