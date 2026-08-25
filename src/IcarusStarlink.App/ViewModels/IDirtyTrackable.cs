namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The shared shape every Save Editor row ViewModel already independently implemented by hand
/// (SaveAccoladeViewModel, SaveBestiaryEntryViewModel, SaveCosmeticFieldViewModel, SaveCurrencyViewModel,
/// SaveFlagViewModel, SaveTalentViewModel — each its own private "_original&lt;X&gt;" field, an
/// IsDirty comparison, and a MarkClean() that re-snapshots it) — collapsing them onto one interface
/// lets SavesViewModel.HasUnsavedChanges/SaveChanges iterate a single list of collections instead of
/// one hand-written clause and one hand-written foreach per section.
/// </summary>
public interface IDirtyTrackable
{
    bool IsDirty { get; }

    void MarkClean();
}
