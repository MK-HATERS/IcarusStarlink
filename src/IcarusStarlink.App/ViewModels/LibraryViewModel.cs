using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Pak;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly ILibraryRepository _repository;
    private readonly IUe4ssModRepository _ue4ssModRepository;
    private readonly IUe4ssModStateService _ue4ssModStateService;
    private readonly ISettingsService _settingsService;
    private readonly IUnrealPakService _unrealPakService;
    private readonly INexusApiClient _nexusApiClient;
    private readonly ICredentialStore _credentialStore;
    private readonly IDaedalusCatalogClient _daedalusClient;
    private readonly IJimk72CatalogClient _jimk72Client;
    private readonly Func<string, ExmodEditorViewModel> _editorFactory;
    private readonly IActivityLog _activityLog;
    private readonly HttpClient _downloadHttpClient;
    private readonly IPendingDownloadStore _pendingDownloadStore;
    private readonly IModVersionComparer _modVersionComparer;

    /// <summary>Resolved lazily so opening Library doesn't force the whole Downloads/Nexus stack (catalog fetches, API clients) to construct alongside it.</summary>
    private readonly Func<DownloadsViewModel> _downloadsViewModel;
    private readonly Func<NexusCatalogViewModel> _nexusCatalogViewModel;

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

    /// <summary>Null when every folder on disk read cleanly — see Reload()'s own comment for what sets it.</summary>
    [ObservableProperty]
    private string? _unreadableFoldersMessage;

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

    [ObservableProperty]
    private bool _isCheckingForUpdates;

    [ObservableProperty]
    private string? _updateCheckStatusMessage;

    public LibraryViewModel(
        ILibraryRepository repository, IUe4ssModRepository ue4ssModRepository, IUe4ssModStateService ue4ssModStateService,
        ISettingsService settingsService, IUnrealPakService unrealPakService, INexusApiClient nexusApiClient,
        ICredentialStore credentialStore, IDaedalusCatalogClient daedalusClient, IJimk72CatalogClient jimk72Client,
        Func<string, ExmodEditorViewModel> editorFactory, IActivityLog activityLog, HttpClient downloadHttpClient,
        IPendingDownloadStore pendingDownloadStore, IModVersionComparer modVersionComparer,
        Func<DownloadsViewModel> downloadsViewModel, Func<NexusCatalogViewModel> nexusCatalogViewModel,
        string backupDirectory)
    {
        _modVersionComparer = modVersionComparer;
        _downloadsViewModel = downloadsViewModel;
        _nexusCatalogViewModel = nexusCatalogViewModel;
        _downloadHttpClient = downloadHttpClient;
        _pendingDownloadStore = pendingDownloadStore;
        _repository = repository;
        _ue4ssModRepository = ue4ssModRepository;
        _ue4ssModStateService = ue4ssModStateService;
        _settingsService = settingsService;
        _unrealPakService = unrealPakService;
        _nexusApiClient = nexusApiClient;
        _credentialStore = credentialStore;
        _daedalusClient = daedalusClient;
        _jimk72Client = jimk72Client;
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

        // Constructors can't be async — same "fire it and let the method itself handle every
        // failure" shape DownloadsViewModel's own launch-time Nexus check already uses. Silent:
        // no "sign in first" nag if unconfigured, that only shows on an explicit button click.
        _ = CheckForUpdatesAsync(isAutomatic: true);
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
            TryImport(dialog.FolderName, path => _repository.Import(path));
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
            TryImport(dialog.FileName, path => _repository.Import(path));
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
            TryImport(dialog.FileName, path => _repository.ImportPak(path));
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

    /// <summary>
    /// The action behind the "Update available" badge. A Database-sourced mod genuinely updates in
    /// place: re-download from the catalog, then replace (delete-then-reimport under the same
    /// "Replace, not accumulate" rule Install/patch-import already use), carrying pin/favorite/
    /// notes across the swap. A Nexus-sourced mod opens its mod page instead — a real in-app Nexus
    /// download needs a file ID this app doesn't have (only the mod ID), so the honest action is
    /// the page where the user's own click starts a Mod Manager Download through the existing
    /// nxm:// pipeline.
    /// </summary>
    [RelayCommand]
    private async Task GetUpdateAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (string.Equals(item.Source, "Nexus", StringComparison.OrdinalIgnoreCase))
        {
            // Nexus downloads persist in Pending Downloads (MO2-style) — if a file for this mod is
            // already sitting there, activating it is one click closer than a fresh browser trip,
            // so point there instead of the website. (Can't distinguish which version that file is
            // — PendingDownloadEntry carries no version — so this stays a pointer, not an
            // automatic activation of a possibly-older file.)
            if (item.NexusModId is { } nexusModId && _pendingDownloadStore.Entries.Any(e => e.ModId == nexusModId))
            {
                StatusMessage = "A downloaded file for this mod is already in Downloads → Pending Downloads — Activate/Reinstall it there (or re-download from Nexus if it's older than the update).";
                return;
            }

            item.OpenOnNexusCommand.Execute(null);
            StatusMessage = "Opened the mod's Nexus page — use its Mod Manager Download button to pull the update through this app.";
            return;
        }

        StatusMessage = $"Updating '{item.Name}'…";
        try
        {
            var failedSources = new List<string>();
            var daedalusTask = CatalogSourceFetch.FetchAsync(_daedalusClient.FetchAsync, "Daedalus", failedSources);
            var jimk72Task = CatalogSourceFetch.FetchAsync(_jimk72Client.FetchAsync, "Jimk72", failedSources);
            await Task.WhenAll(daedalusTask, jimk72Task);
            var allEntries = daedalusTask.Result.Concat(jimk72Task.Result).ToList();

            // Same ID-first, name-fallback matching CheckForUpdatesAsync itself uses.
            var catalogEntry = (item.CatalogEntryId is not null ? allEntries.FirstOrDefault(e => e.Id == item.CatalogEntryId) : null)
                ?? allEntries.FirstOrDefault(e => CatalogKey.Normalize(e.Name, e.Author) == CatalogKey.Normalize(item.Name, item.Author));
            if (catalogEntry is null)
            {
                StatusMessage = $"Couldn't find '{item.Name}' in the catalog anymore.";
                return;
            }

            var downloadUrl = catalogEntry.ExmodzUrl ?? catalogEntry.PakUrl;
            if (downloadUrl is null)
            {
                StatusMessage = $"'{catalogEntry.Name}' has no downloadable file listed.";
                return;
            }

            var isExmodz = catalogEntry.ExmodzUrl is not null;
            var tempPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_{Guid.NewGuid():N}{(isExmodz ? ".EXMODZ" : ".pak")}");
            try
            {
                var bytes = await _downloadHttpClient.GetByteArrayAsync(downloadUrl);
                await File.WriteAllBytesAsync(tempPath, bytes);

                // Captured before the delete so the swap doesn't silently drop them; cancel any
                // pending debounced notes save first, same reasoning DeleteSelected documents.
                var (folderName, isPinned, isFavorite, notes) = (item.FolderName, item.IsPinned, item.IsFavorite, item.Notes);
                var displayNameOverride = _repository.GetAll()
                    .FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase))?.DisplayNameOverride;
                item.CancelPendingSave();

                // Snapshot first, so the delete-then-reimport below can genuinely roll back — a
                // download that parsed as bytes can still fail import validation, and without this
                // that failure would lose the working old copy.
                _repository.BackupMod(folderName);
                _repository.Delete(folderName);

                try
                {
                    var imported = isExmodz
                        ? _repository.Import(tempPath, source: "Database", catalogEntryId: catalogEntry.Id)
                        : _repository.ImportPak(tempPath, source: "Database", catalogEntryId: catalogEntry.Id);
                    if (isPinned || isFavorite || !string.IsNullOrEmpty(notes))
                    {
                        _repository.UpdateMetadata(imported.FolderName, isPinned, isFavorite, notes);
                    }

                    if (displayNameOverride is not null)
                    {
                        _repository.SetDisplayNameOverride(imported.FolderName, displayNameOverride);
                    }

                    StatusMessage = $"Updated '{imported.Name}' to v{imported.Version}.";
                    _activityLog.Log($"Updated '{imported.Name}' from the catalog.", ActivityEntryKind.Success);

                    // The backup taken above is the mod's own previous version, so "what did the
                    // author actually change?" is answerable right now — offered rather than shown
                    // automatically, since an update the user just wanted applied shouldn't force a
                    // window open.
                    await OfferVersionComparisonAsync(imported.Name, imported.FolderName);
                }
                catch (Exception importEx)
                {
                    var restored = _repository.RestoreLatestModBackup(folderName);
                    StatusMessage = restored
                        ? $"Update failed ({importEx.Message}) — your previous copy was restored from its backup."
                        : $"Update failed: {importEx.Message}";
                }

                Reload();
                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            }
            finally
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception)
                {
                    // Best-effort temp cleanup, same as Downloads' own download path.
                }
            }
        }
        catch (Exception ex)
        {
            // Failures before the swap starts (catalog fetch, download) — the mod on disk is
            // untouched at this point; the swap itself has its own backup-and-restore path above.
            StatusMessage = $"Update failed: {ex.Message}";
        }
    }

    /// <summary>Snapshots the mod's whole folder — a safety net before a risky manual edit, independent of the EXMOD editor's own transient per-field preview.</summary>
    [RelayCommand]
    private void BackupMod(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        try
        {
            _repository.BackupMod(item.FolderName);
            item.NotifyBackupStateChanged();
            StatusMessage = $"Backed up '{item.Name}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Takes the user to this mod in the community Database, pre-searched by name — the spec's
    /// "open DB from a library row". Resolving the target ViewModel directly (rather than
    /// announcing the search by message) is deliberate: page ViewModels are built lazily on first
    /// navigation, so a message would land on nobody if Downloads had never been opened.
    /// </summary>
    [RelayCommand]
    private void FindInDatabase(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var downloads = _downloadsViewModel();
        downloads.SelectedTabIndex = 0;
        downloads.CatalogSearchText = item.Name;
        WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("downloads"));
    }

    /// <summary>The spec's "Nexus search from a library row" — searches Nexus for this mod by name, for a mod that has no Nexus link yet.</summary>
    [RelayCommand]
    private void SearchNexusFor(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _nexusCatalogViewModel().SearchText = item.Name;
        WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("nexus"));
    }

    /// <summary>
    /// "See what the author changed" — compares this mod's most recent backup (its previous
    /// version, whether that backup came from an update or a manual Create mod backup) against
    /// what's installed now. Read-only: nothing is restored, moved, or written.
    /// </summary>
    [RelayCommand]
    private async Task CompareToPreviousVersionAsync(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await ShowVersionComparisonAsync(item.Name, item.FolderName);
    }

    /// <summary>Asks first, then shows — the post-update half of the same comparison the context menu offers on demand.</summary>
    private async Task OfferVersionComparisonAsync(string modName, string folderName)
    {
        var answer = MessageBox.Show(
            $"'{modName}' was updated.\n\nSee what the author changed between your old version and this one?",
            "Update installed", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes)
        {
            await ShowVersionComparisonAsync(modName, folderName);
        }
    }

    private async Task ShowVersionComparisonAsync(string modName, string folderName)
    {
        var previousVersionPath = _repository.TryGetLatestModBackupPath(folderName);
        if (previousVersionPath is null)
        {
            StatusMessage = $"There's no earlier copy of '{modName}' to compare against — this app only has one once it's updated or backed up (right-click → Create mod backup).";
            return;
        }

        StatusMessage = $"Comparing '{modName}' against its previous version…";
        try
        {
            var result = await _modVersionComparer.CompareAsync(
                previousVersionPath, _repository.GetFolderPath(folderName), _settingsService.Current.UnrealPakExePath);

            var window = new ModVersionCompareWindow(new ModVersionCompareViewModel(modName, result))
            {
                Owner = Application.Current.MainWindow,
            };
            window.Show();

            StatusMessage = result.IsIdentical
                ? $"'{modName}' is identical to its previous version."
                : $"Showing what changed in '{modName}'.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't compare versions: {ex.Message}";
        }
    }

    /// <summary>
    /// Replaces the mod's current folder with its own most recent backup — a real point-in-time
    /// restore, so this is gated the same way every other hard-to-reverse action in this app is.
    /// </summary>
    [RelayCommand]
    private void RestoreModBackup(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var confirmResult = MessageBox.Show(
            $"This replaces '{item.Name}''s current content with its most recent backup — any edit made since then is lost.\n\nContinue?",
            "Restore backup", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirmResult != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var restored = _repository.RestoreLatestModBackup(item.FolderName);
            StatusMessage = restored ? $"Restored '{item.Name}' from its latest backup." : $"No backup exists yet for '{item.Name}'.";
            if (restored)
            {
                Reload();
                WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Manually connects a Library entry (however it got here — a manual Import, or an unmatched
    /// entry from a future bulk import) to a real Nexus mod ID, so it starts participating in
    /// update-checking like anything activated through the real nxm:// pipeline. Best-effort
    /// enrichment afterward (real name/author/version via the API) mirrors
    /// DownloadsViewModel.EnrichOpaquePakFromNexusAsync's own two-step shape — a rejected/missing
    /// key just means the ID link itself still succeeds without the extra display data.
    /// </summary>
    [RelayCommand]
    private async Task LinkToNexus(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var dialog = new LinkNexusDialog { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _repository.LinkToNexus(item.FolderName, dialog.NexusModId);

            var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
            if (apiKey is not null)
            {
                try
                {
                    var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", dialog.NexusModId);
                    if (info is not null)
                    {
                        _repository.SetNexusMetadata(item.FolderName, info.Name, info.Author, info.Summary, info.Version);
                    }
                }
                catch (Exception)
                {
                    // Best-effort — the ID link above already succeeded regardless.
                }
            }

            StatusMessage = $"Linked '{item.Name}' to Nexus mod #{dialog.NexusModId}.";
            Reload();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't link to Nexus: {ex.Message}";
        }
    }

    /// <summary>Opens a small dialog to override how a mod's own name displays in Library — never touches its real folder, FileName, or file content. Reset clears the override; Cancel does nothing.</summary>
    [RelayCommand]
    private void RenameItem(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var dialog = new RenameModDialog(item.Name) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _repository.SetDisplayNameOverride(item.FolderName, dialog.NewDisplayName);
            Reload();
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't rename: {ex.Message}";
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
    private void DeleteSelected() => DeleteMod(SelectedItem);

    /// <summary>The context-menu counterpart of DeleteSelected — takes the clicked row directly, so deleting doesn't require selecting first.</summary>
    [RelayCommand]
    private void DeleteItem(LibraryItemViewModel? item) => DeleteMod(item);

    private void DeleteMod(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        // Deleting removes the mod's real folder from disk and can't be undone from here — and a
        // right-click menu is far easier to hit by accident than the detail pane's own button, so
        // both paths ask, matching how every other irreversible action in this app behaves.
        var confirm = MessageBox.Show(
            $"Delete '{item.Name}' from your Library?\n\nIts folder is removed from disk. This can't be undone (unless you made a backup first).",
            "Delete mod", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var name = item.Name;
        // Cancel first: a pending debounced notes save firing after Delete() below could write
        // this entry's metadata into a different mod that reuses the same freed folder name.
        item.CancelPendingSave();
        try
        {
            _repository.Delete(item.FolderName);
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

        if (SelectedItem == item)
        {
            SelectedItem = null;
        }

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

        // A folder that's visibly on disk but unreadable (corrupt EXMOD, locked file) was only
        // ever explained in the log file — surfaced here so "why isn't my mod showing?" has an
        // answer in the UI itself.
        UnreadableFoldersMessage = _repository.UnreadableFolders.Count > 0
            ? $"{_repository.UnreadableFolders.Count} folder(s) couldn't be read and aren't shown: {string.Join(", ", _repository.UnreadableFolders)}. Check the mod's own files (a corrupt .EXMOD is the usual cause), or see the log."
            : null;

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
        var created = new LibraryItemViewModel(entry, _repository, _unrealPakService, _settingsService, status => StatusMessage = status, () => Reload());
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

    [RelayCommand]
    private Task CheckForUpdates() => CheckForUpdatesAsync(isAutomatic: false);

    /// <summary>
    /// Nexus-sourced mods get a real version lookup via the Nexus API (same GetModInfoAsync the
    /// Activate enrichment flow already uses), compared against each mod's own currently-known
    /// Version. Database-sourced mods cross-reference the live Daedalus+Jimk72 catalog by
    /// (Name, Author) — same CatalogKey normalization Export Patch already uses — and compare its
    /// Version field. Neither result is persisted; both are recomputed every call, matching
    /// Downloads' own Nexus watchlist precedent ("a real but coarse signal... not persisted").
    /// isAutomatic (the once-per-launch call from the constructor) suppresses status messages
    /// entirely — no "sign in first" nag just from opening the app, only from an explicit click.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool isAutomatic)
    {
        var nexusItems = _itemsByFolderName.Values
            .Where(i => string.Equals(i.Source, "Nexus", StringComparison.OrdinalIgnoreCase) && i.NexusModId is not null)
            .ToList();
        var databaseItems = _itemsByFolderName.Values
            .Where(i => string.Equals(i.Source, "Database", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nexusItems.Count == 0 && databaseItems.Count == 0)
        {
            return;
        }

        IsCheckingForUpdates = true;
        try
        {
            var updatedCount = 0;
            var messages = new List<string>();

            if (nexusItems.Count > 0)
            {
                var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
                if (apiKey is null)
                {
                    if (!isAutomatic)
                    {
                        messages.Add("Sign in with your Nexus API key in Settings to check Nexus-sourced mods.");
                    }
                }
                else
                {
                    var results = await Task.WhenAll(nexusItems.Select(async item =>
                    {
                        try
                        {
                            var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", item.NexusModId!.Value);
                            return (Item: item, Version: info?.Version);
                        }
                        catch (Exception)
                        {
                            // Best-effort per-mod — one bad lookup (rate limit, a since-removed
                            // mod) shouldn't stop the rest of the batch from checking.
                            return (Item: item, Version: (string?)null);
                        }
                    }));

                    foreach (var (item, version) in results)
                    {
                        if (string.IsNullOrEmpty(version))
                        {
                            continue;
                        }

                        item.LatestVersion = version;
                        if (item.HasUpdateAvailable)
                        {
                            updatedCount++;
                        }
                    }
                }
            }

            if (databaseItems.Count > 0)
            {
                try
                {
                    // Isolated per-source, same as Downloads' own catalog refresh and Export
                    // Patch's own lookup — a transient blip in either source shouldn't fail the
                    // whole check.
                    var failedSources = new List<string>();
                    var daedalusTask = CatalogSourceFetch.FetchAsync(_daedalusClient.FetchAsync, "Daedalus", failedSources);
                    var jimk72Task = CatalogSourceFetch.FetchAsync(_jimk72Client.FetchAsync, "Jimk72", failedSources);
                    await Task.WhenAll(daedalusTask, jimk72Task);

                    // ID-first, name-fallback — same rename-safe matching Downloads'
                    // ApplyCatalogFilters now uses, for the same reason (a renamed/oddly-named mod
                    // still checks correctly when its stable CatalogEntryId was recorded at
                    // Download & extract time).
                    var catalogVersionById = new Dictionary<string, string>();
                    var catalogVersionByKey = new Dictionary<(string Name, string Author), string>();
                    foreach (var catalogEntry in daedalusTask.Result.Concat(jimk72Task.Result))
                    {
                        catalogVersionById[catalogEntry.Id] = catalogEntry.Version;
                        catalogVersionByKey[CatalogKey.Normalize(catalogEntry.Name, catalogEntry.Author)] = catalogEntry.Version;
                    }

                    foreach (var item in databaseItems)
                    {
                        var catalogVersion = item.CatalogEntryId is not null && catalogVersionById.TryGetValue(item.CatalogEntryId, out var byId)
                            ? byId
                            : catalogVersionByKey.GetValueOrDefault(CatalogKey.Normalize(item.Name, item.Author));
                        if (catalogVersion is null)
                        {
                            continue;
                        }

                        item.LatestVersion = catalogVersion;
                        if (item.HasUpdateAvailable)
                        {
                            updatedCount++;
                        }
                    }
                }
                catch (Exception)
                {
                    // Best-effort — a catalog fetch failure just means Database-sourced mods don't
                    // get checked this time, not a reason to fail the whole update check.
                }
            }

            if (!isAutomatic)
            {
                messages.Add(updatedCount > 0 ? $"{updatedCount} mod(s) have an update available." : "Everything checked is up to date.");
                UpdateCheckStatusMessage = string.Join(" ", messages);
            }
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }
}
