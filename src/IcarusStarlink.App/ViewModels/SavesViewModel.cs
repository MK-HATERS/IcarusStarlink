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
            activityLog);

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

        RefreshSlots();
    }

    /// <summary>
    /// Drops the cached names and reloads the current slot so they show. Unsaved edits are never
    /// thrown away for this — the new names then appear on the next slot load instead.
    /// </summary>
    private void OnGameDataRefreshed()
    {
        _gameNames.Invalidate();
        WorldSettings.InvalidateDefinitions();
        if (SelectedSlot is null)
        {
            return;
        }

        if (HasAnyUnsavedEdits)
        {
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

    /// <summary>Test seam only — see _pendingSlotLoad's own doc comment for why this exists.</summary>
    internal Task WaitForPendingSlotLoadAsync() => _pendingSlotLoad;

    /// <summary>Test seam only — see _pendingIconLoad's own doc comment for why this exists.</summary>
    internal Task WaitForPendingIconLoadAsync() => _pendingIconLoad;

    /// <summary>One file's load result: its value, or why it couldn't be read (that section is then skipped, not the slot).</summary>
    private readonly record struct Loaded<T>(T? Value, Exception? Error)
    {
        public T Require() => Error is null ? Value! : throw Error;
    }

    private static Loaded<T> TryLoad<T>(Func<T> load)
    {
        try
        {
            return new Loaded<T>(load(), null);
        }
        catch (Exception ex)
        {
            return new Loaded<T>(default, ex);
        }
    }

    /// <summary>
    /// Builds one section; if its data can't be read (a game update changed a field's shape, a file
    /// is damaged), resets it to empty and names it in SkippedSectionsNote instead of failing the
    /// whole slot. An empty section is never dirty, so its file is left exactly as it is on Save.
    /// </summary>
    private void LoadSection(string name, Action build, Action reset)
    {
        try
        {
            build();
        }
        catch (Exception)
        {
            reset();
            _skippedSections.Add(name);
        }
    }

    private async Task LoadSlotAsync()
    {
        // A section that fails to load must not leave the last slot's mission links behind (review, 2026-10-23).
        _rewardsOfMission.Clear();
        _missionsOfReward.Clear();
        Characters.Clear();
        Currencies.Clear();
        Backups.Clear();
        BinaryFlags.Clear();
        AccountFlags.Clear();
        WorkshopTalents.Clear();
        Accolades.Clear();
        BestiaryEntries.Clear();
        MetaInventoryItems.Clear();
        Mounts.Clear();
        _profile = null;
        _characterNodes = [];
        _binaryFlagIds = null;
        _accoladesRoot = null;
        _bestiaryRoot = null;
        _metaInventoryRoot = null;
        _metaInventoryDirty = false;
        _mountsRoot = null;
        _charactersListDirty = false;
        _mountsListDirty = false;
        _profileFieldsDirty = false;
        _loadedStamps = null;
        _skippedSections.Clear();
        SkippedSectionsNote = null;
        SelectedCharacter = null;

        // Bumped here, not just once icons are actually about to be queued below — a slot switch
        // that fails/returns early (SelectedSlot is null, the read throws) still needs to retire any
        // icon resolution left over from whatever slot was loaded before it.
        _iconLoadGeneration++;

        _lastLoadedSlot = SelectedSlot;
        _loadFailed = false;
        var loadGeneration = ++_slotLoadGeneration;

        // Started before the await below, so the world files (often many MB each) are parsed on a
        // background thread alongside the player files instead of on the UI thread first.
        var worldsLoad = WorldSettings.ReloadAsync();

        if (SelectedSlot is null)
        {
            LastBackupDisplay = null;
            RefreshFlagFilter();
            await worldsLoad;
            return;
        }

        // Captured before the await — if the user switches slots again while this load is still
        // in flight, SelectedSlot will have moved on by the time we resume, and this continuation
        // must recognize it's stale rather than populate the UI with the wrong slot's data (a real
        // race the previous fully-synchronous LoadSlot could never hit).
        var slotBeingLoaded = SelectedSlot;
        var steamId = slotBeingLoaded.SteamId;

        try
        {
            // The real JSON files a slot load reads, off the UI thread. Stamps are taken FIRST: a
            // write that lands between stamping and reading then looks like a change at Save time
            // (a needless refusal) rather than slipping through unnoticed. Each file is read on its
            // own, so one unreadable file only hides its own section. Every ObservableCollection
            // mutation below still runs back on this (UI) thread, since that's not safe off it.
            // The game tables every row is named from are read here too (Warm), once each: they
            // used to be parsed on the UI thread as each section asked, D_Talents five times.
            var files = await Task.Run(() =>
            {
                var read = (
                    Stamps: TryLoad(() => _repository.GetFileStamps(steamId)),
                    Profile: _repository.LoadProfile(steamId),
                    Characters: TryLoad(() => _repository.LoadCharacters(steamId)),
                    Accolades: TryLoad(() => _repository.LoadAccolades(steamId)),
                    Bestiary: TryLoad(() => _repository.LoadBestiary(steamId)),
                    MetaInventory: TryLoad(() => _repository.LoadMetaInventory(steamId)),
                    Mounts: TryLoad(() => _repository.LoadMounts(steamId)));
                _gameNames.Warm();
                return read;
            });

            // A newer load started meanwhile (another slot, or this one again: Reload, Restore, a
            // game-data refresh). Only the newest may fill the lists; two loads of the same slot
            // used to both add their rows, listing every character twice.
            if (!ReferenceEquals(SelectedSlot, slotBeingLoaded) || loadGeneration != _slotLoadGeneration)
            {
                return;
            }

            // Required, not optional: without the load-time stamps Save can't tell whether the game
            // wrote the files since, and would silently write an older copy over newer progress.
            _loadedStamps = files.Stamps.Require();
            _profile = files.Profile;

            LoadSection("characters", () =>
            {
                _characterNodes = [.. files.Characters.Require()];
                foreach (var node in _characterNodes)
                {
                    Characters.Add(new SaveCharacterViewModel(node, _gameNames, NotifyDirtyChanged));
                }
            }, () =>
            {
                Characters.Clear();
                _characterNodes = [];
            });

            LoadSection("currencies", () =>
            {
                if (_profile["MetaResources"] is JsonArray resources)
                {
                    foreach (var resource in resources.OfType<JsonObject>())
                    {
                        var key = SaveJson.String(resource["MetaRow"]) ?? "?";
                        // A key the game doesn't have (Exotic_stabilized, in a real save) is said to be one:
                        // it used to be labelled as if it were a real currency.
                        Currencies.Add(new SaveCurrencyViewModel(resource, CurrencyLabels.GetValueOrDefault(key, $"{key} (not a game currency)"), NotifyDirtyChanged));
                    }
                }
            }, Currencies.Clear);

            LoadSection("account flags", BuildAccountFlags, AccountFlags.Clear);
            LoadSection("unlocks (flags file)", BuildBinaryFlags, () =>
            {
                BinaryFlags.Clear();
                _binaryFlagIds = null;
                OnPropertyChanged(nameof(HasBinaryFlags));
            });
            LoadSection("workshop research", BuildWorkshopTalents, () =>
            {
                WorkshopTalents.Clear();
                RefreshAccountTalentFilters();
            });

            LoadSection("accolades", () =>
            {
                _accoladesRoot = files.Accolades.Require();
                BuildAccolades();
            }, () =>
            {
                _accoladesRoot = null;
                Accolades.Clear();
                RefreshAccoladeFilter();
            });
            LoadSection("bestiary", () =>
            {
                _bestiaryRoot = files.Bestiary.Require();
                BuildBestiaryEntries();
            }, () =>
            {
                _bestiaryRoot = null;
                BestiaryEntries.Clear();
                RefreshBestiaryFilter();
            });
            LoadSection("items", () =>
            {
                _metaInventoryRoot = files.MetaInventory.Require();
                BuildMetaInventoryItems();
            }, () =>
            {
                _metaInventoryRoot = null;
                MetaInventoryItems.Clear();
                RefreshItemFilter();
            });
            LoadSection("mounts", () =>
            {
                _mountsRoot = files.Mounts.Require();
                BuildMounts();
            }, () =>
            {
                _mountsRoot = null;
                Mounts.Clear();
                RefreshMountFilter();
            });

            QueueIconResolution();

            SelectedCharacter = Characters.FirstOrDefault();
            RefreshBackupsList();
            StatusMessage = null;
            SkippedSectionsNote = _skippedSections.Count == 0
                ? null
                : $"Couldn't read: {string.Join(", ", _skippedSections)}. Those parts are hidden and their files are left exactly as they are when you save.";
        }
        catch (Exception ex)
        {
            // A load that threw partway left a half-populated but fully SAVEABLE state (an audit
            // finding): Save rebuilt Profile.json's Talents/UnlockedFlags from the (empty)
            // collections and wiped the account's research. Nothing from a failed load is writable.
            // Only Profile.json itself (or its file stamps) gets here now; every other section
            // skips itself (LoadSection).
            _loadFailed = true;
            _profile = null;
            _characterNodes = [];
            _loadedStamps = null;
            StatusMessage = $"Couldn't read this save: {ex.Message} Its backups are still listed under Backup & restore.";
            try
            {
                RefreshBackupsList();
            }
            catch (Exception)
            {
                // The backups folder itself unreadable: the status line already says what failed.
            }
        }

        // SelectedCharacter's change handler refreshes these, but a slot with no characters
        // leaves it null (unchanged), so the account and binary flags kept the previous slot's.
        RefreshFlagFilter();
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(ChangesSummary));
        _pendingModSources = ClassifyModRowsAsync();
        _pendingPlayerGrowth = ReadPlayerGrowthAsync();
        _pendingMountGrowth = ReadMountGrowthAsync();
        ApplyPackNotes();
        RunSaveCheck();
        await worldsLoad;
    }

    /// <summary>"Unsaved: Characters, Unlocks, Bestiary" — what Save would write, named on the Save bar.</summary>
    public string? ChangesSummary
    {
        get
        {
            if (_loadFailed)
            {
                return null;
            }

            var parts = new List<string>();
            void Add(bool dirty, string name)
            {
                if (dirty)
                {
                    parts.Add(name);
                }
            }

            Add(CharactersDirty, "Characters");
            Add(Currencies.Any(c => c.IsDirty), "Currencies");
            Add(AccountFlags.Any(f => f.IsDirty), "Unlocks");
            Add(BinaryFlags.Any(f => f.IsDirty), "Owned DLC");
            Add(WorkshopTalents.Any(t => t.IsDirty && !IsMission(t)), "Workshop");
            Add(WorkshopTalents.Any(t => t.IsDirty && IsMission(t)), "Missions");
            Add(Accolades.Any(a => a.IsDirty), "Accolades");
            Add(BestiaryEntries.Any(b => b.IsDirty), "Bestiary");
            Add(_metaInventoryDirty, "Items");
            Add(_mountsListDirty || Mounts.Any(m => m.IsDirty), "Mounts");
            return parts.Count == 0 ? null : $"Unsaved: {string.Join(", ", parts)}";
        }
    }

    private void NotifyDirtyChanged()
    {
        OnPropertyChanged(nameof(ChangesSummary));
        OnPropertyChanged(nameof(AccoladesSummary));
        OnPropertyChanged(nameof(BestiarySummary));
        OnPropertyChanged(nameof(TalentRanksSummary));
        OnPropertyChanged(nameof(PointsNotes));
        OnPropertyChanged(nameof(HasPointsNotes));
        RefreshPointsEditors();
        OnPropertyChanged(nameof(WorkshopSummary));
        OnPropertyChanged(nameof(MissionsSummary));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(HasAnyUnsavedEdits));
        OnPropertyChanged(nameof(OwnedDlcSummary));
    }

    /// <summary>
    /// Writes AccountFlags/WorkshopTalents back into the profile node — the profile-level
    /// counterpart of SaveCharacterViewModel.ApplyToNode, same minimal-diff rules. Each list is
    /// rebuilt only when it was edited, so a section that couldn't be read (empty, see LoadSection)
    /// can never wipe the save's own list.
    /// </summary>
    private void ApplyProfileEdits()
    {
        if (_profile is null)
        {
            return;
        }

        if (AccountFlags.Any(f => f.IsDirty))
        {
            ApplyAccountFlagEdits(_profile);
        }

        if (WorkshopTalents.Any(t => t.IsDirty))
        {
            ApplyWorkshopTalentEdits(_profile);
        }
    }

    [RelayCommand]
    private void SaveChanges()
    {
        if (SelectedSlot is null)
        {
            return;
        }

        if (!HasUnsavedChanges)
        {
            StatusMessage = "Nothing changed.";
            return;
        }

        if (IsGameRunning())
        {
            StatusMessage = "Icarus is running — close it before saving, or the game will overwrite your edits when it exits.";
            return;
        }

        // The page is a singleton that read the save once: if the user played since, writing now
        // would put that older copy over their newer progress (a review finding).
        if (SaveFilesChangedOnDisk())
        {
            RefuseStaleSave();
            return;
        }

        // Via IDialogService, not ThemedMessageBox.Show directly — same reasoning as
        // RestoreBackupAsync's own confirm: this method's real write needs to be testable without
        // a live WPF Dispatcher, including the late re-check immediately below.
        var confirm = _dialogService.Confirm(
            "Write your edits into the player save?\n\nA full backup of the slot is taken automatically first (Restore can undo this).",
            "Save player data", ThemedConfirmSeverity.Question);
        if (!(confirm))
        {
            return;
        }

        var backedUp = false;
        try
        {
            // Each file is written only when its own section changed: Characters.json for the
            // characters, Profile.json for currencies, account flags, workshop research (and the
            // NextChrSlot a duplicate advances). Only edited characters/currencies touch their nodes.
            var writeCharacters = CharactersDirty;
            var writeProfile = ProfileDirty;

            // A SECOND, LATE check, immediately before the first real disk write below — the first
            // IsGameRunning() check above (before the confirm dialog) can go stale for however long
            // the user takes to answer that dialog, and Icarus could be launched in that window.
            // This doesn't close the race entirely (no check here can, without an OS-level file
            // lock held across processes for the whole write) — it only shrinks the exposed window
            // down to "immediately before the write" instead of "however long the confirm dialog
            // was up", aborting late-and-safe (nothing has been written yet) rather than
            // early-and-stale.
            if (IsGameRunning())
            {
                StatusMessage = "Icarus started running while this save was about to write — nothing was touched. Close it and try Save again.";
                return;
            }

            // Same late re-check for an outside write while the confirm dialog was up.
            if (SaveFilesChangedOnDisk())
            {
                RefuseStaleSave();
                return;
            }

            // Only now are the edits put into the loaded save: applied before these checks, an
            // aborted save left them in it, and an edit the user then undid was written anyway by
            // the next Save (a review finding).
            foreach (var character in Characters.Where(c => c.IsDirty))
            {
                character.ApplyToNode();
            }

            foreach (var currency in Currencies.Where(c => c.IsDirty))
            {
                currency.ApplyToNode();
            }

            ApplyProfileEdits();

            // ONE backup for the whole save pass, taken up front — every SaveXxx call below passes
            // takeBackup: false. A single click used to be able to zip the same slot up to 6 times
            // back-to-back (Characters/Profile always, plus BinaryFlags/Accolades/Bestiary/
            // MetaInventory whenever dirty), each one blocking the UI in turn, when one pre-save
            // snapshot already covers everything this pass is about to change.
            _repository.BackupSlot(SelectedSlot.SteamId);
            backedUp = true;

            // Characters first, then profile — keeps write ORDER consistent with before, even
            // though backup ordering no longer matters now that there's just the one upfront zip.
            if (writeCharacters)
            {
                _repository.SaveCharacters(SelectedSlot.SteamId, _characterNodes, takeBackup: false);
            }

            if (writeProfile)
            {
                _repository.SaveProfile(SelectedSlot.SteamId, _profile!, takeBackup: false);
                _profileFieldsDirty = false;
            }

            // The DLC list is written only when a pack was toggled: packs that stay keep the file's
            // own order, newly-added ones append.
            if (_binaryFlagIds is { } original && BinaryFlags.Any(f => f.IsDirty))
            {
                var nowSet = BinaryFlags.Where(f => f.IsUnlocked).Select(f => f.Id).ToHashSet();
                var newIds = original.Where(nowSet.Contains).ToList();
                newIds.AddRange(nowSet.Except(original).OrderBy(i => i));
                _repository.SaveBinaryFlags(SelectedSlot.SteamId, newIds, takeBackup: false);
                _binaryFlagIds = newIds;
            }

            // Accolades.json/BestiaryData.json are separate files from Profile.json — these only
            // write when that specific section actually changed, same conditional-write reasoning
            // BinaryFlags above already uses, rather than always re-writing every file on every Save
            // click (the upfront backup above already covers whichever of these actually run).
            if (Accolades.Any(a => a.IsDirty))
            {
                ApplyAccoladeEdits();
                _repository.SaveAccolades(SelectedSlot.SteamId, _accoladesRoot!, takeBackup: false);
            }

            if (BestiaryEntries.Any(b => b.IsDirty))
            {
                ApplyBestiaryEdits();
                _repository.SaveBestiary(SelectedSlot.SteamId, _bestiaryRoot!, takeBackup: false);
            }

            if (_metaInventoryDirty)
            {
                ApplyMetaInventoryEdits();
                _repository.SaveMetaInventory(SelectedSlot.SteamId, _metaInventoryRoot!, takeBackup: false);
                _metaInventoryDirty = false;
            }

            // _mountsListDirty (a mount removed from the list) is checked alongside per-field
            // dirtiness — a pure removal with no remaining mount edited would otherwise never
            // trigger this write at all, silently leaving a "deleted" mount in Mounts.json.
            if (_mountsListDirty || Mounts.Any(m => m.IsDirty))
            {
                ApplyMountEdits();
                _repository.SaveMounts(SelectedSlot.SteamId, _mountsRoot!, takeBackup: false);
                _mountsListDirty = false;
            }

            foreach (var collection in DirtyTrackedCollections)
            {
                foreach (var item in collection)
                {
                    item.MarkClean();
                }
            }

            _charactersListDirty = false;

            // These writes changed the files' stamps; the new ones are the baseline for the next Save.
            try
            {
                _loadedStamps = _repository.GetFileStamps(SelectedSlot.SteamId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _loadedStamps = null;
            }

            OnPropertyChanged(nameof(HasUnsavedChanges));
            OnPropertyChanged(nameof(ChangesSummary));
            RefreshBackupsList();
            StatusMessage = "Saved. A backup of the previous state was kept.";
            _activityLog.Log($"Edited player save {SelectedSlot.Display}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            if (backedUp)
            {
                // Some files may already be written. Their new stamps are this page's own doing, so
                // they become the baseline: otherwise the next Save took them for the game's and
                // refused, leaving "reload and lose your edits" as the only way out.
                try
                {
                    _loadedStamps = _repository.GetFileStamps(SelectedSlot.SteamId);
                }
                catch (Exception stampEx) when (stampEx is IOException or UnauthorizedAccessException)
                {
                }

                RefreshBackupsList();
                StatusMessage = $"Save failed partway: {ex.Message} Some files may already be written. Click Save to try again, or Restore the newest backup (taken just before this save) to undo it.";
            }
            else
            {
                StatusMessage = $"Save failed, nothing was written: {ex.Message}";
            }

            _activityLog.Log($"Player save not written: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// The game holds these files and rewrites them on exit — any edit made while it runs is lost
    /// or half-read, so save/restore refuse rather than race it. Delegates to IGameProcessChecker
    /// (rather than calling Process.GetProcessesByName directly, which is all this used to do)
    /// purely so a test can fake the answer — including a DIFFERENT answer on a second call within
    /// the same pass, to exercise the late re-check SaveChanges/RestoreBackupAsync each run
    /// immediately before their real write.
    /// </summary>
    private bool IsGameRunning() => _gameProcessChecker.IsRunning();
}
