using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One trackable creature's encounter points — same deferred-apply text-box pattern as SaveCurrencyViewModel. A creature with no prior save entry starts at 0, same as an untracked currency would.</summary>
public sealed partial class SaveBestiaryEntryViewModel : ObservableObject, IDirtyTrackable
{
    private readonly Action _onDirtyChanged;
    private int _originalPoints;

    public string RowName { get; }

    public string DisplayName { get; }

    /// <summary>The row's own TotalPointsRequired — what "Set to max" fills in, and what the UI shows progress against.</summary>
    public int PointsRequired { get; }

    public bool IsBoss { get; }

    /// <summary>D_BestiaryData's own raw "Image" field (a "/Game/…/T_Bestiary_Xxx.T_Bestiary_Xxx" texture reference) — null for a creature with no current data-table entry (e.g. an old save's tracked RowName the game no longer defines) or no Image on it.</summary>
    public string? ImagePath { get; }

    /// <summary>Null until (if ever) SavesViewModel's own background resolution decodes ImagePath through the base-game content provider — a row shows text-only for however long that takes, or forever if it never resolves.</summary>
    [ObservableProperty]
    private BitmapImage? _icon;

    [ObservableProperty]
    private string _pointsText;

    public bool IsDirty => int.TryParse(PointsText, out var points) ? points != _originalPoints : PointsText != _originalPoints.ToString();

    /// <summary>What Save should actually write: the parsed text, or the last-known-good value if the box is currently unparsable (e.g. mid-edit, cleared) — a save must never drop this entry just because the text box isn't a valid number right now.</summary>
    public int EffectivePoints => int.TryParse(PointsText, out var points) ? points : _originalPoints;

    public SaveBestiaryEntryViewModel(string rowName, string displayName, int pointsRequired, bool isBoss, int currentPoints, string? imagePath, Action onDirtyChanged)
    {
        RowName = rowName;
        DisplayName = displayName;
        PointsRequired = pointsRequired;
        IsBoss = isBoss;
        ImagePath = imagePath;
        _originalPoints = currentPoints;
        _pointsText = currentPoints.ToString();
        _onDirtyChanged = onDirtyChanged;
    }

    // Caps NEW input at PointsRequired — the real ceiling ("Set max" fills in this exact value,
    // and points beyond it don't unlock anything further). Same tolerance SaveTalentViewModel's
    // own MaxRank clamp uses for a legacy over-cap value already in the save: never clamps BELOW
    // whatever was originally loaded, only stops the user from typing something newly higher than
    // both the real cap and the original — so an old save from before a requirement changed keeps
    // showing its real number instead of being silently reduced just by opening the editor.
    partial void OnPointsTextChanged(string value)
    {
        if (PointsRequired > 0 && int.TryParse(value, out var parsed))
        {
            var ceiling = Math.Max(PointsRequired, _originalPoints);
            if (parsed > ceiling)
            {
                _pointsText = ceiling.ToString();
                OnPropertyChanged(nameof(PointsText));
            }
        }

        _onDirtyChanged();
    }

    /// <summary>0 means "the current data extraction doesn't recognize this creature," not "already maxed" — writing 0 here would zero out real tracked progress instead of maxing it, so this only fires when a real cap is known.</summary>
    public bool CanSetMax => PointsRequired > 0;

    [RelayCommand(CanExecute = nameof(CanSetMax))]
    private void SetMax() => PointsText = PointsRequired.ToString();

    public void MarkClean()
    {
        if (int.TryParse(PointsText, out var points))
        {
            _originalPoints = points;
        }
    }
}
