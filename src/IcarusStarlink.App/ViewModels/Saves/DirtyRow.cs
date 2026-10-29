using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// A Saves page row that tracks its own unsaved edit: each row keeps what it was loaded (or last
/// saved) with, compares against it in IsDirty, and takes it again in Snapshot after a Save.
/// Changed() is the one place an edit is reported, so the row's own "changed" dot (bound to
/// IsDirty) and the page's Save bar always hear about it together — several rows used to tell
/// only the page, so their dot never showed.
/// </summary>
public abstract class DirtyRow(Action onDirtyChanged) : ObservableObject, IDirtyTrackable
{
    public abstract bool IsDirty { get; }

    /// <summary>What's wrong with the row's number box by SaveNumberRules ("Whole number, digits only"), or null; rows without one keep null.</summary>
    protected virtual string? NumberProblem => null;

    /// <summary>
    /// The line under the row's number box, shown while typing: set exactly when Save would refuse
    /// the box (an edited box Save can't write, see SavesViewModel.BadNumberBoxes), else null.
    /// </summary>
    public string? NumberError => IsDirty ? NumberProblem : null;

    /// <summary>The red border on the box (NumberError is set).</summary>
    public bool HasNumberError => NumberError is not null;

    /// <summary>The current values become the clean ones (after a Save wrote them).</summary>
    protected abstract void Snapshot();

    public void MarkClean()
    {
        Snapshot();
        OnDirtyStateChanged();
    }

    /// <summary>An edit happened: the row's IsDirty and the page's dirty state may both have changed.</summary>
    protected void Changed()
    {
        OnDirtyStateChanged();
        onDirtyChanged();
    }

    private void OnDirtyStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(NumberError));
        OnPropertyChanged(nameof(HasNumberError));
    }
}
