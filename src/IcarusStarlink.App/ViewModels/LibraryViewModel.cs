using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Library;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly ILibraryRepository _repository;
    private readonly DispatcherTimer _searchDebounceTimer;
    private readonly Dictionary<string, LibraryItemViewModel> _itemsByFolderName = [];
    private readonly Dictionary<string, LibraryGroupViewModel> _groupsByKey = [];

    public string Title => "Library";

    /// <summary>Each element is either a LibraryGroupViewModel (a real family) or a bare LibraryItemViewModel (standalone) — WPF picks the right DataTemplate by type, the same routing pattern MainWindow uses for pages.</summary>
    public ObservableCollection<object> RootItems { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private LibraryItemViewModel? _selectedItem;

    [ObservableProperty]
    private string? _statusMessage;

    public LibraryViewModel(ILibraryRepository repository)
    {
        _repository = repository;

        // Reload() rebuilds every row's ViewModel and re-queries the search index; without
        // debouncing, every keystroke would pay that cost plus re-trigger the still-selected
        // item's EnsureDetailsLoaded (a fresh instance loses its "already loaded" state).
        _searchDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            Reload();
        };

        Reload();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    partial void OnSelectedItemChanged(LibraryItemViewModel? value) => value?.EnsureDetailsLoaded();

    [RelayCommand]
    private void ImportFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select the mod's extracted folder" };
        if (dialog.ShowDialog() == true)
        {
            TryImport(dialog.FolderName);
        }
    }

    [RelayCommand]
    private void ImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an .EXMODZ file",
            Filter = "EXMODZ package (*.EXMODZ)|*.EXMODZ",
        };

        if (dialog.ShowDialog() == true)
        {
            TryImport(dialog.FileName);
        }
    }

    [RelayCommand]
    private void DeleteSelected()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var name = SelectedItem.Name;
        // Cancel first: a pending debounced notes save firing after Delete() below could write
        // this entry's metadata into a different mod that reuses the same freed folder name.
        SelectedItem.CancelPendingSave();
        try
        {
            _repository.Delete(SelectedItem.FolderName);
        }
        catch (Exception ex)
        {
            // Same UI boundary as TryImport: deleting a folder that's locked by another
            // process (Explorer preview, an antivirus scan, ...) throws IOException /
            // UnauthorizedAccessException, and that should surface as a status message rather
            // than crash the app.
            StatusMessage = $"Delete failed: {ex.Message}";
            return;
        }

        SelectedItem = null;
        StatusMessage = $"Deleted '{name}'.";
        Reload();
    }

    private void TryImport(string sourcePath)
    {
        try
        {
            var entry = _repository.Import(sourcePath);
            StatusMessage = $"Imported '{entry.Name}'.";
            Reload();
        }
        catch (Exception ex)
        {
            // A user-initiated import can fail for many reasons (malformed archive, permission
            // denied, disk full, ...) — this is the UI boundary where any of them should show a
            // friendly message instead of crashing the app.
            StatusMessage = $"Import failed: {ex.Message}";
        }
    }

    private void Reload()
    {
        var previouslySelectedFolder = SelectedItem?.FolderName;

        var groups = VariantGrouping.Group(_repository.Search(SearchText))
            .OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase);

        var seenFolders = new HashSet<string>();
        var seenGroupKeys = new HashSet<string>();
        var targetRootItems = new List<object>();

        foreach (var group in groups)
        {
            var items = group.Entries.Select(GetOrCreateItem).ToList();
            foreach (var item in items)
            {
                seenFolders.Add(item.FolderName);
            }

            if (group.IsFamily)
            {
                seenGroupKeys.Add(group.GroupKey);
                targetRootItems.Add(GetOrCreateGroup(group.GroupKey, group.DisplayName, items));
            }
            else
            {
                targetRootItems.Add(items[0]);
            }
        }

        SyncRootItems(targetRootItems);

        // Drop cached instances for mods/families no longer present/matching, so these don't grow forever.
        foreach (var staleFolderName in _itemsByFolderName.Keys.Except(seenFolders).ToList())
        {
            _itemsByFolderName.Remove(staleFolderName);
        }

        foreach (var staleGroupKey in _groupsByKey.Keys.Except(seenGroupKeys).ToList())
        {
            _groupsByKey.Remove(staleGroupKey);
        }

        SelectedItem = previouslySelectedFolder is not null && _itemsByFolderName.TryGetValue(previouslySelectedFolder, out var stillSelected)
            ? stillSelected
            : null;
    }

    /// <summary>
    /// Updates RootItems to match `target` via targeted Remove/Insert/Move rather than
    /// Clear()+re-Add(): WPF's TreeView regenerates every container from scratch on the Reset
    /// notification Clear() raises, even for items whose object identity didn't change — which
    /// was collapsing every expanded family (and losing the selection highlight) on every
    /// debounced search reload despite GetOrCreateGroup/GetOrCreateItem already reusing the same
    /// instances. LibraryGroupViewModel.SetItems applies the same fix one level down, for a
    /// family's own children.
    /// </summary>
    private void SyncRootItems(IReadOnlyList<object> target) => ObservableCollectionSync.SyncTo(RootItems, target);

    /// <summary>
    /// Reusing the same LibraryItemViewModel instance across reloads (rather than always
    /// constructing a new one) means a mod that's still selected after a search-triggered reload
    /// keeps its already-loaded Files/Readme state (no redundant disk I/O) and stays the same
    /// object reference SelectedItem points at.
    /// </summary>
    private LibraryItemViewModel GetOrCreateItem(LibraryEntry entry)
    {
        if (_itemsByFolderName.TryGetValue(entry.FolderName, out var existing))
        {
            return existing;
        }

        var created = new LibraryItemViewModel(entry, _repository, status => StatusMessage = status);
        _itemsByFolderName[entry.FolderName] = created;
        return created;
    }

    /// <summary>
    /// Same instance-reuse rationale as GetOrCreateItem, but for family headers: reusing the
    /// LibraryGroupViewModel by GroupKey (not DisplayName — a search that narrows which variants
    /// match can't change the key) keeps the TreeView's expanded/collapsed state across a
    /// debounced search reload instead of collapsing every family on each keystroke. DisplayName
    /// is still refreshed on every call (via Update, not just at construction) since
    /// VariantGrouping can derive it from a different member entry between reloads — e.g. after
    /// the member that supplied it is deleted.
    /// </summary>
    private LibraryGroupViewModel GetOrCreateGroup(string groupKey, string displayName, IReadOnlyList<LibraryItemViewModel> items)
    {
        if (!_groupsByKey.TryGetValue(groupKey, out var group))
        {
            group = new LibraryGroupViewModel(displayName);
            _groupsByKey[groupKey] = group;
        }

        group.Update(displayName, items);
        return group;
    }
}
