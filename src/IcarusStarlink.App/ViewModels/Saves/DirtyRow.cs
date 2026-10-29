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

    /// <summary>The current values become the clean ones (after a Save wrote them).</summary>
    protected abstract void Snapshot();

    public void MarkClean()
    {
        Snapshot();
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>An edit happened: the row's IsDirty and the page's dirty state may both have changed.</summary>
    protected void Changed()
    {
        OnPropertyChanged(nameof(IsDirty));
        onDirtyChanged();
    }
}
