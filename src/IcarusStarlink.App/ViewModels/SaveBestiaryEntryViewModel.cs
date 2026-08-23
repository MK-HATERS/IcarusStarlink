using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One trackable creature's encounter points — same deferred-apply text-box pattern as SaveCurrencyViewModel. A creature with no prior save entry starts at 0, same as an untracked currency would.</summary>
public sealed partial class SaveBestiaryEntryViewModel : ObservableObject
{
    private readonly Action _onDirtyChanged;
    private int _originalPoints;

    public string RowName { get; }

    public string DisplayName { get; }

    /// <summary>The row's own TotalPointsRequired — what "Set to max" fills in, and what the UI shows progress against.</summary>
    public int PointsRequired { get; }

    public bool IsBoss { get; }

    [ObservableProperty]
    private string _pointsText;

    public bool IsDirty => int.TryParse(PointsText, out var points) ? points != _originalPoints : PointsText != _originalPoints.ToString();

    public SaveBestiaryEntryViewModel(string rowName, string displayName, int pointsRequired, bool isBoss, int currentPoints, Action onDirtyChanged)
    {
        RowName = rowName;
        DisplayName = displayName;
        PointsRequired = pointsRequired;
        IsBoss = isBoss;
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

    [RelayCommand]
    private void SetMax() => PointsText = PointsRequired.ToString();

    public void MarkClean()
    {
        if (int.TryParse(PointsText, out var points))
        {
            _originalPoints = points;
        }
    }
}
