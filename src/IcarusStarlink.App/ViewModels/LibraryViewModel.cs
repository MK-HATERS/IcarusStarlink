using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly ILibraryRepository _repository;
    private readonly IUe4ssModRepository _ue4ssModRepository;
    private readonly IUe4ssModStateService _ue4ssModStateService;
    private readonly ISettingsService _settingsService;
    private readonly Func<string, ExmodEditorViewModel> _editorFactory;
    private readonly IActivityLog _activityLog;
    private readonly string _backupDirectory;
    private readonly DebounceTimer _searchDebounceTimer;
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

    [ObservableProperty]
    private int _modCount;

    /// <summary>Count of variant families currently shown (LibraryGroup.IsFamily), not the total root-row count — matches the real app's "N mod(s) · M group(s)" wording.</summary>
    [ObservableProperty]
    private int _groupCount;

    /// <summary>
    /// Library's UE4SS tab (Phase 8.5 rework) — the single place to manage UE4SS mods, replacing
    /// the old split between this read-only tab and Merge & Install's own separate Staged/Attached
    /// section. Each row's IsEnabled starts equal to its real current state (present in the game's
    /// own Mods folder or not) and only diverges once toggled — nothing actually moves until Apply.
    /// </summary>
    public ObservableCollection<Ue4ssModRowViewModel> Ue4ssMods { get; } = [];

    [ObservableProperty]
    private bool _hasPendingUe4ssChanges;

    [ObservableProperty]
    private string? _ue4ssStatusMessage;

    public LibraryViewModel(
        ILibraryRepository repository, IUe4ssModRepository ue4ssModRepository, IUe4ssModStateService ue4ssModStateService,
        ISettingsService settingsService, Func<string, ExmodEditorViewModel> editorFactory, IActivityLog activityLog, string backupDirectory)
    {
        _repository = repository;
        _ue4ssModRepository = ue4ssModRepository;
        _ue4ssModStateService = ue4ssModStateService;
        _settingsService = settingsService;
        _editorFactory = editorFactory;
        _activityLog = activityLog;
        _backupDirectory = backupDirectory;

        // Reload() rebuilds every row's ViewModel and re-queries the search index; without
        // debouncing, every keystroke would pay that cost plus re-trigger the still-selected
        // item's EnsureDetailsLoaded (a fresh instance loses its "already loaded" state).
        _searchDebounceTimer = new DebounceTimer(TimeSpan.FromMilliseconds(250), () => Reload());

        // This VM is a DI singleton, constructed once and never re-scanned on its own — without
        // this, a mod imported from Downloads (a different page, sharing the same
        // ILibraryRepository) wouldn't show up here until the user happened to trigger some
        // unrelated reload (a search edit, or this page's own Refresh button).
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) => ((LibraryViewModel)recipient).Reload(fullResync: true));

        Reload();
        ReloadInstalledUe4ssMods();
    }

    partial void OnSearchTextChanged(string value) => _searchDebounceTimer.Restart();

    partial void OnSelectedItemChanged(LibraryItemViewModel? value) => value?.EnsureDetailsLoaded();

    [RelayCommand]
    private void ReloadInstalledUe4ssMods()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            Ue4ssMods.Clear();
            HasPendingUe4ssChanges = false;
            Ue4ssStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        try
        {
            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(_settingsService.Current.IcarusContentPath);
            var states = _ue4ssModStateService.GetAll(modsFolder);

            Ue4ssMods.Clear();
            foreach (var state in states)
            {
                Ue4ssMods.Add(new Ue4ssModRowViewModel(state.Name, state.IsEnabled, RecomputeHasPendingUe4ssChanges));
            }
            HasPendingUe4ssChanges = false;

            Ue4ssStatusMessage = states.Count == 0 ? "No UE4SS mods found." : null;
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Couldn't read the game's UE4SS Mods folder: {ex.Message}";
        }
    }

    private void RecomputeHasPendingUe4ssChanges() => HasPendingUe4ssChanges = Ue4ssMods.Any(m => m.IsDirty);

    /// <summary>
    /// Moves every dirty row to match its toggled state — enabling copies staging→game, disabling
    /// backs up then moves game→staging (IUe4ssModStateService.Apply) — then reloads so every row's
    /// RealIsEnabled (and the page's own dirty state) reflects what's actually on disk now.
    /// </summary>
    [RelayCommand]
    private void ApplyUe4ssChanges()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            Ue4ssStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        try
        {
            var modsFolder = Ue4ssGamePaths.ResolveModsFolder(_settingsService.Current.IcarusContentPath);
            var desired = Ue4ssMods.Where(m => m.IsDirty).ToDictionary(m => m.Name, m => m.IsEnabled);
            var changedCount = desired.Count;

            _ue4ssModStateService.Apply(modsFolder, desired, _backupDirectory);
            ReloadInstalledUe4ssMods();
            Ue4ssStatusMessage = $"Applied {changedCount} change(s).";
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Apply failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ImportUe4ssMod()
    {
        var dialog = new OpenFileDialog { Title = "Select a UE4SS mod zip", Filter = "Zip archive (*.zip)|*.zip" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var folderName = _ue4ssModRepository.Import(dialog.FileName);
            ReloadInstalledUe4ssMods();
            Ue4ssStatusMessage = $"Staged '{folderName}' — enable it below, then click Apply.";
        }
        catch (Exception ex)
        {
            Ue4ssStatusMessage = $"Import failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ImportFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select the mod's extracted folder" };
        if (dialog.ShowDialog() == true)
        {
            TryImport(dialog.FolderName, _repository.Import);
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
            TryImport(dialog.FileName, _repository.Import);
        }
    }

    [RelayCommand]
    private void ImportPak()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select a prebuilt .pak file",
            Filter = "Unreal pak package (*.pak)|*.pak",
        };

        if (dialog.ShowDialog() == true)
        {
            TryImport(dialog.FileName, _repository.ImportPak);
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        try
        {
            _repository.Refresh();
        }
        catch (Exception ex)
        {
            // Same UI boundary as import/delete — Refresh() re-walks Extracted_Mods from disk,
            // which can hit the same locked/permission-denied conditions RescanAll's own per-mod
            // catch already tolerates, except this one is the top-level directory enumeration
            // itself, which isn't wrapped per-folder.
            StatusMessage = $"Refresh failed: {ex.Message}";
            return;
        }

        StatusMessage = "Library refreshed.";
        Reload(fullResync: true);
    }

    [RelayCommand]
    private void EditSelected()
    {
        if (SelectedItem is null)
        {
            return;
        }

        if (SelectedItem.IsOpaquePak)
        {
            StatusMessage = "An opaque .pak import has no .EXMOD to edit.";
            return;
        }

        try
        {
            OpenEditor(SelectedItem.FolderName);
        }
        catch (Exception ex)
        {
            // Same UI boundary as NewMod's own identical OpenEditor call — the mod's .EXMOD can
            // have gone missing, become ambiguous, or be locked by another process since Library
            // last scanned it.
            StatusMessage = $"Couldn't open the editor: {ex.Message}";
        }
    }

    [RelayCommand]
    private void NewMod()
    {
        var dialog = new NewModDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var entry = _repository.CreateBlankMod(dialog.ModName, dialog.ModAuthor);
            StatusMessage = $"Created '{entry.Name}'.";
            Reload();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            _activityLog.Log($"Created new mod '{entry.Name}'.", ActivityEntryKind.Success);
            OpenEditor(entry.FolderName);
        }
        catch (Exception ex)
        {
            // Same UI boundary as TryImport — folder creation can fail for the same reasons any
            // other Extracted_Mods write can (permission denied, disk full, ...).
            StatusMessage = $"Couldn't create the mod: {ex.Message}";
        }
    }

    /// <summary>Phase 10: an inline per-row Edit action (row hover icons) — takes the row's own item directly rather than requiring it to be selected first, unlike EditSelectedCommand.</summary>
    [RelayCommand]
    private void EditItem(LibraryItemViewModel? item)
    {
        if (item is null || item.IsOpaquePak)
        {
            return;
        }

        try
        {
            OpenEditor(item.FolderName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't open the editor: {ex.Message}";
        }
    }

    /// <summary>
    /// A new, non-modal Window + a fresh, transient ExmodEditorViewModel per call — classic IMM's
    /// own real editor supports two independent sessions open at once, which .Show() (not
    /// ShowDialog) matches; this app's other ViewModels stay DI singletons, but the editor
    /// deliberately isn't one.
    /// </summary>
    private void OpenEditor(string folderName)
    {
        var editorViewModel = _editorFactory(folderName);
        var window = new ExmodEditorWindow(editorViewModel) { Owner = Application.Current.MainWindow };
        window.Show();
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
        WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        _activityLog.Log($"Deleted '{name}'.", ActivityEntryKind.Info);
    }

    /// <summary>Shared by ImportFolder/ImportFile/ImportPak — same try/catch/status/reload shape, differing only in which repository method actually reads sourcePath.</summary>
    private void TryImport(string sourcePath, Func<string, LibraryEntry> importer)
    {
        try
        {
            var entry = importer(sourcePath);
            StatusMessage = $"Imported '{entry.Name}'.";
            Reload();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            _activityLog.Log($"Imported '{entry.Name}' v{entry.Version}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            // A user-initiated import can fail for many reasons (malformed archive, permission
            // denied, disk full, ...) — this is the UI boundary where any of them should show a
            // friendly message instead of crashing the app.
            StatusMessage = $"Import failed: {ex.Message}";
        }
    }

    /// <summary>
    /// fullResync is true only for the explicit Refresh() command — every other caller (the
    /// search debounce, import, delete, a pin toggle) reloads because the *set* of visible items
    /// changed, not because any individual still-visible mod's own data did, so those paths must
    /// leave already-cached LibraryItemViewModel instances alone rather than re-syncing them.
    /// Doing that unconditionally on every reload was tried and reverted: it re-applied whatever
    /// Notes/Pinned/Favorite happened to be in the repository's last-saved snapshot over top of
    /// values the user might still be mid-typing (Notes saves on a 500ms debounce), and it wiped
    /// every still-selected mod's cached Files/Readme/thumbnail on every keystroke of a search,
    /// forcing a redundant disk re-read — defeating the exact caching GetOrCreateItem exists for.
    /// </summary>
    private void Reload(bool fullResync = false)
    {
        var previouslySelectedFolder = SelectedItem?.FolderName;

        // Pinned mods (or a family with any pinned member) sort first, matching the real app's
        // "pinned mods sort to the top" — then alphabetical within each of those two bands.
        var groups = VariantGrouping.Group(_repository.Search(SearchText))
            .OrderByDescending(g => g.Entries.Any(e => e.IsPinned))
            .ThenBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ModCount = groups.Sum(g => g.Entries.Count);
        GroupCount = groups.Count(g => g.IsFamily);

        var seenFolders = new HashSet<string>();
        var seenGroupKeys = new HashSet<string>();
        var targetRootItems = new List<object>();

        foreach (var group in groups)
        {
            var items = group.Entries.Select(entry => GetOrCreateItem(entry, fullResync)).ToList();
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

        // Drop cached instances for mods/families no longer present/matching, so these don't grow
        // forever. FlushPendingSave() first, not CancelPendingSave() — the mod's folder isn't going
        // anywhere here (unlike DeleteSelected), so a pending Notes edit should be saved a little
        // early, not silently discarded; without this, an orphaned timer could fire later and write
        // the edit to disk after a fresh instance for the same mod (built from the now-stale
        // repository read) has already replaced this one, leaving the visible Notes field stale
        // relative to what's actually on disk.
        foreach (var staleFolderName in _itemsByFolderName.Keys.Except(seenFolders).ToList())
        {
            _itemsByFolderName[staleFolderName].FlushPendingSave();
            _itemsByFolderName.Remove(staleFolderName);
        }

        foreach (var staleGroupKey in _groupsByKey.Keys.Except(seenGroupKeys).ToList())
        {
            _groupsByKey.Remove(staleGroupKey);
        }

        SelectedItem = previouslySelectedFolder is not null && _itemsByFolderName.TryGetValue(previouslySelectedFolder, out var stillSelected)
            ? stillSelected
            : null;

        // OnSelectedItemChanged only fires on an actual reference change, but SelectedItem above
        // is frequently reassigned to the exact same cached instance it already was (nothing
        // selection-worthy changed) — that path still needs EnsureDetailsLoaded() run explicitly
        // so a just-applied Update() (which clears AssetPaths/ReadmeContent/ThumbnailImage and
        // resets _detailsLoaded) actually reloads the still-selected mod's details right away,
        // rather than only the next time the user reselects it.
        SelectedItem?.EnsureDetailsLoaded();
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
    private LibraryItemViewModel GetOrCreateItem(LibraryEntry entry, bool fullResync)
    {
        if (_itemsByFolderName.TryGetValue(entry.FolderName, out var existing))
        {
            // Only on an explicit Refresh() — see the fullResync doc comment on Reload() for why
            // this can't run on every reload.
            if (fullResync)
            {
                existing.Update(entry);
            }

            return existing;
        }

        // onPinnedChanged: pinned status drives Reload()'s sort order, but toggling it only
        // updates the repository/cache in place — nothing else re-runs that ordering. Without
        // this, a pin saves correctly but the row silently stays wherever it already was in the
        // tree until some unrelated change (a search edit, a delete) happens to trigger the next
        // Reload().
        var created = new LibraryItemViewModel(entry, _repository, status => StatusMessage = status, () => Reload());
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
