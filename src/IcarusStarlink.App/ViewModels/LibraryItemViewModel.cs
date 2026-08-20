using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.Core.Library;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class LibraryItemViewModel : ObservableObject
{
    private readonly ILibraryRepository _repository;
    private readonly Action<string> _reportStatus;
    private readonly DispatcherTimer _notesSaveDebounceTimer;
    private bool _detailsLoaded;

    public string FolderName { get; }
    public string Name { get; }
    public string Author { get; }
    public string Version { get; }
    public string Description { get; }
    public string? VariantLabel { get; }

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isFavorite;

    [ObservableProperty]
    private string _notes;

    public ObservableCollection<string> AssetPaths { get; } = [];

    [ObservableProperty]
    private string? _readmeContent;

    [ObservableProperty]
    private string? _selectedAssetPath;

    [ObservableProperty]
    private string? _selectedAssetPreview;

    public LibraryItemViewModel(LibraryEntry entry, ILibraryRepository repository, Action<string> reportStatus)
    {
        _repository = repository;
        _reportStatus = reportStatus;
        FolderName = entry.FolderName;
        Name = entry.Name;
        Author = entry.Author;
        Version = entry.Version;
        Description = entry.Description;
        VariantLabel = entry.Variant;

        // Assigning the backing fields directly (not the generated properties) means this
        // doesn't route through OnIsPinnedChanged/etc. and save straight back to the repository
        // the values that just came from that same repository.
        _isPinned = entry.IsPinned;
        _isFavorite = entry.IsFavorite;
        _notes = entry.Notes;

        // Notes now binds UpdateSourceTrigger=PropertyChanged (so a keystroke immediately
        // followed by closing the app isn't lost the way it would be with the default
        // LostFocus trigger) — debounced the same way LibraryViewModel debounces search, so
        // typing a whole sentence doesn't fire a disk write and an FTS5 index update per
        // keystroke. Pinned/Favorite stay immediate: those are single clicks, not rapid-repeat.
        _notesSaveDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _notesSaveDebounceTimer.Tick += (_, _) =>
        {
            _notesSaveDebounceTimer.Stop();
            SaveMetadata();
        };
    }

    /// <summary>
    /// Stops any pending debounced notes save without flushing it — called right before this
    /// mod's folder is deleted. Without this, a stale timer firing after delete could write this
    /// entry's old metadata into a *different* mod that reuses the exact same folder name
    /// (MakeUniqueFolderName only avoids collisions with what currently exists on disk), since
    /// UpdateMetadata's only existence check is Directory.Exists on that name.
    /// </summary>
    public void CancelPendingSave() => _notesSaveDebounceTimer.Stop();

    partial void OnIsPinnedChanged(bool value) => SaveMetadata();

    partial void OnIsFavoriteChanged(bool value) => SaveMetadata();

    partial void OnNotesChanged(string value)
    {
        _notesSaveDebounceTimer.Stop();
        _notesSaveDebounceTimer.Start();
    }

    partial void OnSelectedAssetPathChanged(string? value)
    {
        if (value is null)
        {
            SelectedAssetPreview = null;
            return;
        }

        try
        {
            var bytes = _repository.ReadAssetContent(FolderName, value);
            SelectedAssetPreview = LooksLikeText(bytes)
                ? System.Text.Encoding.UTF8.GetString(bytes)
                : $"(binary file — {bytes.Length:N0} bytes, no preview)";
        }
        catch (Exception ex)
        {
            // Same UI boundary as SaveMetadata: the asset a user just clicked can vanish or get
            // locked between ListAssetPaths populating this list and this read (external edit,
            // AV quarantine), and that should show up as an explanation in the preview pane
            // itself — the one place this specific failure is actually relevant — rather than
            // crash the app out of a binding-driven property setter.
            SelectedAssetPreview = $"(failed to read this file: {ex.Message})";
            _reportStatus($"Preview failed: {ex.Message}");
        }
    }

    private void SaveMetadata()
    {
        try
        {
            _repository.UpdateMetadata(FolderName, IsPinned, IsFavorite, Notes);
        }
        catch (Exception ex)
        {
            // Same UI boundary as Import/Delete: a pin/favorite toggle or notes edit can fail
            // for the same reasons a delete can (sidecar locked, folder gone) and should show a
            // status message instead of crashing the app out of a property-changed callback.
            _reportStatus($"Save failed: {ex.Message}");
        }
    }

    /// <summary>Files/readme are only loaded once the user actually selects this item, not for every entry in the library up front.</summary>
    public void EnsureDetailsLoaded()
    {
        if (_detailsLoaded)
        {
            return;
        }

        try
        {
            foreach (var path in _repository.ListAssetPaths(FolderName))
            {
                AssetPaths.Add(path);
            }

            ReadmeContent = _repository.ReadReadme(FolderName);

            // Only mark this as done once it actually succeeded — a transient failure (folder
            // locked, deleted out from under the app) should let a later reselect retry rather
            // than permanently pin this mod's Files/Readme tabs empty for the rest of the run.
            _detailsLoaded = true;
        }
        catch (Exception ex)
        {
            // Same UI boundary as SaveMetadata/TryImport/DeleteSelected: this runs from
            // OnSelectedItemChanged, off a binding-driven selection change, so an unhandled
            // exception here would crash the app instead of showing a status message.
            _reportStatus($"Couldn't load mod details: {ex.Message}");
        }
    }

    private static bool LooksLikeText(byte[] bytes) =>
        !bytes.Take(Math.Min(bytes.Length, 512)).Any(b => b == 0);
}
