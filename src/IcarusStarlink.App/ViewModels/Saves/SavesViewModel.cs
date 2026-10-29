using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Views;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Saves;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// The Saves page (the spec's save editor, S1: slots + backup/restore + Overview cards +
/// character/currency editing — layout modeled on Icarus Workshop's own save editor screens:
/// character cards on an Overview beside an account snapshot, click a card to edit).
///
/// Safety posture, stricter than any other page: nothing writes without a full slot backup first
/// (the repository enforces it), Restore writes a pre_restore zip (ditto), saving is refused
/// outright while Icarus is running (the game holds these files and overwrites them on exit —
/// an edit made mid-session would be silently lost or, worse, half-read — checked once before the
/// confirm dialog and again immediately before the real write, since the first check can go stale
/// for however long the user takes to answer that dialog), and every destructive action confirms
/// first — except a plain list-remove (delete a mount, delete an inventory item) that's only ever
/// staged in memory until Save's own confirm covers it; deleting a whole character is the one
/// exception to THAT, since losing one's entire progress is too big to leave to Save's generic
/// confirm text.
/// </summary>
public sealed partial class SavesViewModel : ObservableObject
{
    private readonly ISaveRepository _repository;
    private readonly IActivityLog _activityLog;
    /// <summary>SaveModSources.Read: where the save's mod rows come from (null: not labelled).</summary>
    private readonly Func<SaveModSources.Index>? _readModSources;

    /// <summary>PlayerGrowthReader.Read: the player's level cap and point curves as installed (null: not shown).</summary>
    private readonly Func<IcarusStarlink.PakIO.Assets.PlayerGrowth?>? _readPlayerGrowth;

    private IcarusStarlink.PakIO.Assets.PlayerGrowth? _playerGrowth;

    private readonly PlayerPointsMod? _pointsMod;

    /// <summary>PlayerGrowthReader.ReadMounts: each mount type's growth, archetype and looks as installed.</summary>
    private readonly Func<IReadOnlyDictionary<string, IcarusStarlink.PakIO.Assets.MountGrowth>>? _readMountGrowth;

    private IReadOnlyDictionary<string, IcarusStarlink.PakIO.Assets.MountGrowth> _mountGrowth = new Dictionary<string, IcarusStarlink.PakIO.Assets.MountGrowth>();
    private readonly SaveGameNames _gameNames;
    private readonly IDialogService _dialogService;
    private readonly IGameProcessChecker _gameProcessChecker;
    private readonly IBaseGameIconDecoder _baseGameIconDecoder;
    private readonly Utilities.DebounceTimer _talentSearchDebounceTimer;

    private JsonObject? _profile;
    private List<JsonObject> _characterNodes = [];
    private List<int>? _binaryFlagIds;
    private JsonObject? _accoladesRoot;
    private JsonObject? _bestiaryRoot;
    private JsonObject? _metaInventoryRoot;
    private bool _metaInventoryDirty;
    private JsonObject? _mountsRoot;

    /// <summary>
    /// Characters and Mounts are BOTH per-field dirty-tracked already (see DirtyTrackedCollections
    /// below) — but adding or removing a whole entry from either list (Duplicate/Delete character,
    /// Delete mount) isn't a per-field edit, so nothing in that tracking would ever notice it on
    /// its own. These two flags catch exactly that gap, the same reasoning _metaInventoryDirty
    /// already applies to a collection with no per-field tracking at all.
    /// </summary>
    private bool _charactersListDirty;
    private bool _mountsListDirty;

    /// <summary>Profile.json fields changed outside the tracked collections (Duplicate character advances NextChrSlot).</summary>
    private bool _profileFieldsDirty;

    /// <summary>
    /// Each read file's last-write time and size when this slot was loaded (null: not loaded, or
    /// unreadable). Compared before every write: if the game rewrote the save since (the user
    /// played with this page open), writing would put this older copy over the newer progress.
    /// </summary>
    private IReadOnlyDictionary<string, SaveFileStamp?>? _loadedStamps;

    /// <summary>Sections whose data couldn't be read this load. Each stays empty and its file is left exactly as it is.</summary>
    private readonly List<string> _skippedSections = [];

    /// <summary>A quiet note naming the sections that couldn't be read, or null when everything loaded.</summary>
    [ObservableProperty]
    private string? _skippedSectionsNote;

    private SaveSlot? _lastLoadedSlot;
    private bool _suppressSlotChangeGuard;

    /// <summary>
    /// Fire-and-forget from OnSelectedSlotChanged — production UI never needs to await this
    /// (bindings just pick up the ObservableCollection mutations once they land), but a test that
    /// sets SelectedSlot has no other way to know the async load (real file I/O, or a fake
    /// repository, off the UI thread) has actually finished before asserting against
    /// Characters/Currencies/etc. See WaitForPendingSlotLoadAsync.
    /// </summary>
    private Task _pendingSlotLoad = Task.CompletedTask;

    /// <summary>Same reasoning as _pendingSlotLoad, for the separate fire-and-forget icon-resolution pass QueueIconResolution starts once every row already exists — see WaitForPendingIconLoadAsync.</summary>
    private Task _pendingIconLoad = Task.CompletedTask;

    public string Title => "Saves";

    public ObservableCollection<SaveSlot> Slots { get; } = [];

    [ObservableProperty]
    private SaveSlot? _selectedSlot;

    public ObservableCollection<SaveCharacterViewModel> Characters { get; } = [];

    [ObservableProperty]
    private SaveCharacterViewModel? _selectedCharacter;

    public ObservableCollection<SaveCurrencyViewModel> Currencies { get; } = [];

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _lastBackupDisplay;

    public ObservableCollection<SaveBackupInfo> Backups { get; } = [];

    [ObservableProperty]
    private SaveBackupInfo? _selectedBackup;

    /// <summary>0 = Overview, 1 = Characters — set programmatically when an Overview card is clicked, per the spec's "Click a card to edit that character".</summary>
    [ObservableProperty]
    private int _selectedTabIndex;

    /// <summary>
    /// Every dirty-tracked collection on this page — the one place that needs to know about all 8
    /// (Characters/Currencies/AccountFlags/BinaryFlags/WorkshopTalents/Accolades/BestiaryEntries/
    /// Mounts), so HasUnsavedChanges and SaveChanges' own MarkClean pass both read it instead of
    /// each independently hand-listing the same 8 collections. MetaInventory isn't here — it
    /// tracks dirtiness as a single _metaInventoryDirty flag on this class, not per-row, since its
    /// own items have no IDirtyTrackable shape to join (view + delete only, no per-field edits).
    /// </summary>
    private IEnumerable<IEnumerable<IDirtyTrackable>> DirtyTrackedCollections =>
        [Characters, Currencies, AccountFlags, BinaryFlags, WorkshopTalents, Accolades, BestiaryEntries, Mounts];

    /// <summary>Set when LoadSlotAsync threw partway — see its catch. Nothing may be saved from that state.</summary>
    private bool _loadFailed;

    /// <summary>Bumped by every LoadSlotAsync, so a slower earlier load of the same slot knows it was overtaken.</summary>
    private int _slotLoadGeneration;

    /// <summary>The Worlds tab: the slot's saved worlds and their custom settings.</summary>
    public WorldSettingsViewModel WorldSettings { get; }

    public bool HasUnsavedChanges =>
        !_loadFailed && (DirtyTrackedCollections.Any(c => c.Any(x => x.IsDirty)) || _metaInventoryDirty || _charactersListDirty || _mountsListDirty || _profileFieldsDirty);

    /// <summary>
    /// Anything on this page that would be lost: the player-data edits above and the Worlds tab's
    /// own unsaved settings (which used to be dropped without a word on a slot switch, Reload,
    /// Restore, the reload after the game saved, and app close). What the "discard?" questions ask about.
    /// </summary>
    public bool HasAnyUnsavedEdits => HasUnsavedChanges || WorldSettings.HasChanges;

    private bool CharactersDirty => _charactersListDirty || Characters.Any(c => c.IsDirty);

    private bool ProfileDirty =>
        _profileFieldsDirty || Currencies.Any(c => c.IsDirty) || AccountFlags.Any(f => f.IsDirty) || WorkshopTalents.Any(t => t.IsDirty);

    public SavesViewModel(
        ISaveRepository repository, IActivityLog activityLog, SaveGameNames gameNames,
        IDialogService dialogService, IGameProcessChecker gameProcessChecker, IBaseGameIconDecoder baseGameIconDecoder,
        string? worldBackupFolder = null, Func<SaveModSources.Index>? readModSources = null,
        Func<IcarusStarlink.PakIO.Assets.PlayerGrowth?>? readPlayerGrowth = null, PlayerPointsMod? pointsMod = null,
        Func<IReadOnlyDictionary<string, IcarusStarlink.PakIO.Assets.MountGrowth>>? readMountGrowth = null)
    {
        _readMountGrowth = readMountGrowth;
        _readPlayerGrowth = readPlayerGrowth;
        _pointsMod = pointsMod;
        _readModSources = readModSources;
        _repository = repository;
        _activityLog = activityLog;
        _gameNames = gameNames;
        _dialogService = dialogService;
        _gameProcessChecker = gameProcessChecker;
        _baseGameIconDecoder = baseGameIconDecoder;
        _talentSearchDebounceTimer = new Utilities.DebounceTimer(TimeSpan.FromMilliseconds(250), RefreshTalentFilter);
        WorldSettings = new WorldSettingsViewModel(
            () => SelectedSlot is { } slot ? Path.Combine(slot.FolderPath, "Prospects") : null,
            () => gameNames.DataFolder,
            () => gameProcessChecker.IsRunning() ? "Close Icarus first: it rewrites its worlds when it exits, which would undo this." : null,
            worldBackupFolder ?? Path.Combine(Path.GetTempPath(), "IcarusStarlink", "WorldBackups"),
            activityLog,
            dialogService);

        WorldSettings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WorldSettingsViewModel.HasChanges))
            {
                OnPropertyChanged(nameof(HasAnyUnsavedEdits));
            }
        };

        WeakReferenceMessenger.Default.Register<UnsavedEditsRequestMessage>(this, (recipient, message) =>
        {
            if (((SavesViewModel)recipient).HasAnyUnsavedEdits)
            {
                message.Reply("your Saves page edits");
            }
        });

        // Refresh game data re-extracts the tables every name here comes from.
        WeakReferenceMessenger.Default.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) => ((SavesViewModel)recipient).OnGameDataRefreshed());

        // What's installed or in the Library may have changed: the next load reads the paks again.
        WeakReferenceMessenger.Default.Register<LibraryChangedMessage>(this, (recipient, _) => ((SavesViewModel)recipient)._gameContentStale = true);
        WeakReferenceMessenger.Default.Register<MergedPakBuiltMessage>(this, (recipient, _) => ((SavesViewModel)recipient)._gameContentStale = true);
        WeakReferenceMessenger.Default.Register<SettingsSavedMessage>(this, (recipient, _) => ((SavesViewModel)recipient)._gameContentStale = true);

        RefreshSlots();
    }

    /// <summary>
    /// The installed growth curves, mount growth and where mod rows come from are read from the
    /// game's paks and the Library, not the slot: they're read again only once something there
    /// may have changed (Library, Merge &amp; Install, Settings, Refresh game data). Every slot load
    /// and Reload used to read them all again.
    /// </summary>
    private bool _gameContentStale = true;

    /// <summary>
    /// Drops the cached names and reloads the current slot so they show. Unsaved edits are never
    /// thrown away for this (the Worlds tab's included) — the new names then appear on the next
    /// slot load instead.
    /// </summary>
    private void OnGameDataRefreshed()
    {
        _gameNames.Invalidate();
        _gameContentStale = true;
        WorldSettings.InvalidateDefinitions();
        if (SelectedSlot is null)
        {
            return;
        }

        if (HasAnyUnsavedEdits)
        {
            // The rows stay; the save check still reads the tables, so they're read now, off the UI thread.
            _ = Task.Run(_gameNames.Warm);
            StatusMessage = "Game data was refreshed — save or switch slots to see the updated names.";
            return;
        }

        _pendingSlotLoad = LoadSlotAsync();
    }

    /// <summary>
    /// Shared "clear target, re-add whatever from source passes includeGate (if given) and matches
    /// searchText against any of matchOn" — the same clear+filter+repopulate loop RefreshFlagFilter/
    /// RefreshTalentFilter/RefreshAccoladeFilter/RefreshBestiaryFilter/RefreshItemFilter each used to
    /// hand-write independently. includeGate is a pre-search condition (only WorkshopTalents' own
    /// "hide what this character doesn't have yet" filter needs one); matchOn is one selector per
    /// field the search text is allowed to match against.
    /// </summary>
    private static void RefreshFilter<T>(
        IEnumerable<T> source, ObservableCollection<T> target, string searchText,
        Func<T, bool>? includeGate, params Func<T, string>[] matchOn)
    {
        target.Clear();
        var search = searchText.Trim();
        foreach (var item in source)
        {
            if (includeGate is not null && !includeGate(item))
            {
                continue;
            }

            if (search.Length == 0 || matchOn.Any(selector => selector(item).Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(item);
            }
        }
    }

    private void RefreshSlots()
    {
        var previous = SelectedSlot?.SteamId;
        Slots.Clear();
        string? readError = null;
        try
        {
            foreach (var slot in _repository.ListSlots())
            {
                Slots.Add(slot);
            }
        }
        catch (Exception ex)
        {
            readError = $"Couldn't read the game's PlayerData folder: {ex.Message}";
        }

        SelectedSlot = Slots.FirstOrDefault(s => s.SteamId == previous) ?? Slots.FirstOrDefault();

        // A read error used to be overwritten straight away by "No player saves found", which
        // told someone with a locked or unreadable folder that they had never run the game.
        StatusMessage = readError ?? (Slots.Count == 0
            ? "No player saves found — Icarus keeps them under %LocalAppData%\\Icarus\\Saved\\PlayerData once you've run the game."
            : null);
    }

    /// <summary>
    /// Every other destructive action on this page (Restore, Save) confirms first via MessageBox —
    /// switching slots used to be the one exception, silently discarding whatever's unsaved across
    /// all 8 tabs. Since ObservableProperty setters can't be cancelled, a "No" answer here reverts
    /// SelectedSlot back (via _suppressSlotChangeGuard, so that revert itself doesn't re-prompt or
    /// re-run LoadSlot for data that's already loaded).
    /// </summary>
    partial void OnSelectedSlotChanged(SaveSlot? value)
    {
        if (_suppressSlotChangeGuard)
        {
            return;
        }

        if (HasAnyUnsavedEdits)
        {
            var confirm = _dialogService.Confirm(
                "Switching save slots discards any unsaved edits on this one — they were never written to disk. Continue?",
                "Unsaved changes", ThemedConfirmSeverity.Warning);
            if (!(confirm))
            {
                // Put back after the dropdown finishes its own selection change: set inside it, the
                // dropdown could keep showing the new slot over the old slot's data.
                var back = _lastLoadedSlot;
                void Revert()
                {
                    _suppressSlotChangeGuard = true;
                    SelectedSlot = back;
                    _suppressSlotChangeGuard = false;
                }

                if (System.Windows.Application.Current?.Dispatcher is { } dispatcher)
                {
                    dispatcher.BeginInvoke(Revert);
                }
                else
                {
                    Revert();
                }

                return;
            }
        }

        // Fire-and-forget, matching DownloadsViewModel.RefreshCatalogAsync's own established
        // precedent for a UI event that kicks off async work with nothing to await it here — the
        // Task itself is still kept (not discarded) so a test can await WaitForPendingSlotLoadAsync.
        _pendingSlotLoad = LoadSlotAsync();
    }

    /// <summary>
    /// Called each time the page is shown (MainViewModel). The game may have saved newer progress
    /// while another page was open: with nothing unsaved here the slot is simply read again; with
    /// unsaved edits it only says so (Save refuses anyway, see SaveFilesChangedOnDisk).
    /// </summary>
    public void OnPageShown()
    {
        if (SelectedSlot is null || !_pendingSlotLoad.IsCompleted || _loadFailed || !SaveFilesChangedOnDisk())
        {
            return;
        }

        if (HasAnyUnsavedEdits)
        {
            StatusMessage = "This save changed on disk since it was read (Icarus saved it). Saving is blocked so it can't undo that progress. Reload to see the newer copy; your edits here would be lost.";
            return;
        }

        _pendingSlotLoad = ReloadAfterOutsideChangeAsync();
    }

    private async Task ReloadAfterOutsideChangeAsync()
    {
        await LoadSlotAsync();
        if (!_loadFailed)
        {
            StatusMessage = "Read the save again: it changed on disk since this page last showed it.";
        }
    }

    /// <summary>Reads the slot from disk again. Unsaved edits are lost, so it asks first when there are some.</summary>
    [RelayCommand]
    private void Reload()
    {
        if (SelectedSlot is null)
        {
            return;
        }

        if (HasAnyUnsavedEdits && !_dialogService.Confirm(
                "Read this save from disk again?\n\nYour unsaved edits on this page will be lost.",
                "Reload save", ThemedConfirmSeverity.Warning))
        {
            return;
        }

        _pendingSlotLoad = LoadSlotAsync();
    }

    /// <summary>True when a file this page read was rewritten (or created, or deleted) since the load.</summary>
    private bool SaveFilesChangedOnDisk()
    {
        if (SelectedSlot is null || _loadFailed)
        {
            return false;
        }

        // Unknown counts as changed: "couldn't check" used to pass as "unchanged" and let Save
        // write an older copy over whatever the game had written (a review finding).
        if (_loadedStamps is null)
        {
            return true;
        }

        IReadOnlyDictionary<string, SaveFileStamp?> now;
        try
        {
            now = _repository.GetFileStamps(SelectedSlot.SteamId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }

        return now.Count != _loadedStamps.Count
            || now.Any(kv => !_loadedStamps.TryGetValue(kv.Key, out var then) || !Equals(then, kv.Value));
    }

    /// <summary>Save found newer files on disk: nothing is written. Offers to read them (losing the edits here).</summary>
    private void RefuseStaleSave()
    {
        var reload = _dialogService.Confirm(
            "This save changed on disk after it was read here, most likely because Icarus saved while you played. "
            + "Saving now would put the older copy back and undo that progress, so nothing was written.\n\n"
            + "Read the newer save now? Your unsaved edits on this page will be lost.",
            "Save changed on disk", ThemedConfirmSeverity.Warning);
        if (reload)
        {
            _pendingSlotLoad = LoadSlotAsync();
            return;
        }

        StatusMessage = "Not saved: the save changed on disk since it was read. Reload to see the newer copy (your edits here would be lost).";
    }
}
