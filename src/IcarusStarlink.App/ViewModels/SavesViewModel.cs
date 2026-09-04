using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
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
    /// <summary>Real MetaRow keys → the names the game's own UI uses (per the spec's own list: "Ren, Exotics, Red, Biomass, Uranium, Licence, Respec"). An unrecognized key still shows, under its raw name — same preserve-everything philosophy as the repository.</summary>
    private static readonly Dictionary<string, string> CurrencyLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Credits"] = "Ren",
        ["Exotic1"] = "Exotics",
        ["Exotic_Red"] = "Red Exotics",
        ["Exotic_stabilized"] = "Stabilized Exotics",
        ["Biomass"] = "Biomass",
        ["Exotic_Uranium"] = "Uranium",
        ["Licence"] = "Licences",
        ["Refund"] = "Respec Points",
    };

    private readonly ISaveRepository _repository;
    private readonly IActivityLog _activityLog;
    private readonly SaveGameNames _gameNames;
    private readonly IDialogService _dialogService;
    private readonly IGameProcessChecker _gameProcessChecker;
    private readonly IBaseGameIconDecoder _baseGameIconDecoder;
    private readonly Utilities.DebounceTimer _talentSearchDebounceTimer;

    /// <summary>
    /// Session-lifetime cache: raw Icon/Image path → its decoded (and frozen) BitmapImage, or a
    /// cached null for a path that didn't resolve — shared across every row this page ever builds,
    /// not just the currently-loaded slot's. A save can carry many rows referencing the exact same
    /// D_Mounts/D_Itemable/D_BestiaryData texture (ten mounts of the same species; the same item
    /// stacked in several MetaInventory entries), and this stops that from re-running a real texture
    /// decode (20-150ms per IBaseGameContentProvider's own doc comment) for a path already resolved
    /// once this session — IBaseGameContentProvider's own mount is already app-lifetime-cached, but
    /// the per-texture decode on top of it is not.
    /// </summary>
    private readonly Dictionary<string, BitmapImage?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bumped every time LoadSlotAsync runs — lets a still-in-flight icon resolution from a
    /// PREVIOUS slot load recognize it's stale and stop touching rows that may no longer be shown
    /// (a fresh LoadSlotAsync rebuilds every row from scratch), the same generation-guard shape
    /// LibraryItemViewModel's own _assetPreviewGeneration already uses for exactly this reason.
    /// </summary>
    private int _iconLoadGeneration;

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

    public string Title => "Saves (Beta)";

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

    public bool HasSlots => Slots.Count > 0;

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

    public bool HasUnsavedChanges =>
        DirtyTrackedCollections.Any(c => c.Any(x => x.IsDirty)) || _metaInventoryDirty || _charactersListDirty || _mountsListDirty;

    public SavesViewModel(
        ISaveRepository repository, IActivityLog activityLog, SaveGameNames gameNames,
        IDialogService dialogService, IGameProcessChecker gameProcessChecker, IBaseGameIconDecoder baseGameIconDecoder)
    {
        _repository = repository;
        _activityLog = activityLog;
        _gameNames = gameNames;
        _dialogService = dialogService;
        _gameProcessChecker = gameProcessChecker;
        _baseGameIconDecoder = baseGameIconDecoder;
        _talentSearchDebounceTimer = new Utilities.DebounceTimer(TimeSpan.FromMilliseconds(250), RefreshTalentFilter);
        RefreshSlots();
    }

    // --- Flags (character flags on the selected character; account flags on the profile;
    //     binary flags in the slot's own flags_<SteamID>.dat — a third store the game keeps
    //     OUTSIDE the JSON, holding account-wide character-flag unlocks) ---

    public ObservableCollection<SaveFlagViewModel> AccountFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> BinaryFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredCharacterFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredAccountFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredBinaryFlags { get; } = [];

    /// <summary>The binary section shows only when the game itself has created the file — this editor never invents one for a slot that has none.</summary>
    public bool HasBinaryFlags => BinaryFlags.Count > 0;

    [ObservableProperty]
    private string _flagSearchText = "";

    partial void OnFlagSearchTextChanged(string value) => RefreshFlagFilter();

    private void RefreshFlagFilter()
    {
        // 45 + 100 rows — a straight rebuild per keystroke is cheaper than filter plumbing.
        RefreshFilter(SelectedCharacter?.Flags ?? [], FilteredCharacterFlags, FlagSearchText, null, f => f.Name);
        RefreshFilter(AccountFlags, FilteredAccountFlags, FlagSearchText, null, f => f.Name);
        RefreshFilter(BinaryFlags, FilteredBinaryFlags, FlagSearchText, null, f => f.Name);
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

    // --- Talents (character talents on the selected character; workshop research on the profile) ---

    public ObservableCollection<SaveTalentViewModel> WorkshopTalents { get; } = [];

    public ObservableCollection<SaveTalentViewModel> FilteredTalents { get; } = [];

    [ObservableProperty]
    private string _talentSearchText = "";

    /// <summary>Off = only what the character has; on = every talent the game defines, rank 0 included — turning one up grants it.</summary>
    [ObservableProperty]
    private bool _showUnlearnedTalents;

    partial void OnTalentSearchTextChanged(string value) => _talentSearchDebounceTimer.Restart();

    partial void OnShowUnlearnedTalentsChanged(bool value) => RefreshTalentFilter();

    private void RefreshTalentFilter()
    {
        // "Only what this character has" includes bDefaultUnlocked talents — the game grants those
        // from the start without writing them into the save, so rank alone under-reports.
        RefreshFilter(
            SelectedCharacter?.Talents ?? [], FilteredTalents, TalentSearchText,
            t => ShowUnlearnedTalents || t.IsUnlockedInGame || t.IsDirty,
            t => t.DisplayName, t => t.RowName, t => t.Tree);
    }

    partial void OnSelectedCharacterChanged(SaveCharacterViewModel? value)
    {
        RefreshFlagFilter();
        RefreshTalentFilter();
    }

    [RelayCommand]
    private void RefreshSlots()
    {
        var previous = SelectedSlot?.SteamId;
        Slots.Clear();
        try
        {
            foreach (var slot in _repository.ListSlots())
            {
                Slots.Add(slot);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't read the game's PlayerData folder: {ex.Message}";
        }

        OnPropertyChanged(nameof(HasSlots));
        SelectedSlot = Slots.FirstOrDefault(s => s.SteamId == previous) ?? Slots.FirstOrDefault();
        StatusMessage = Slots.Count == 0
            ? "No player saves found — Icarus keeps them under %LocalAppData%\\Icarus\\Saved\\PlayerData once you've run the game."
            : null;
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

        if (HasUnsavedChanges)
        {
            var confirm = ThemedMessageBox.Show(
                "Switching save slots discards any unsaved edits on this one — they were never written to disk. Continue?",
                "Unsaved changes", ThemedConfirmSeverity.Warning);
            if (!(confirm))
            {
                _suppressSlotChangeGuard = true;
                SelectedSlot = _lastLoadedSlot;
                _suppressSlotChangeGuard = false;
                return;
            }
        }

        // Fire-and-forget, matching DownloadsViewModel.RefreshCatalogAsync's own established
        // precedent for a UI event that kicks off async work with nothing to await it here — the
        // Task itself is still kept (not discarded) so a test can await WaitForPendingSlotLoadAsync.
        _pendingSlotLoad = LoadSlotAsync();
    }

    /// <summary>Test seam only — see _pendingSlotLoad's own doc comment for why this exists.</summary>
    internal Task WaitForPendingSlotLoadAsync() => _pendingSlotLoad;

    /// <summary>Test seam only — see _pendingIconLoad's own doc comment for why this exists.</summary>
    internal Task WaitForPendingIconLoadAsync() => _pendingIconLoad;

    private async Task LoadSlotAsync()
    {
        Characters.Clear();
        Currencies.Clear();
        Backups.Clear();
        BinaryFlags.Clear();
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
        SelectedCharacter = null;

        // Bumped here, not just once icons are actually about to be queued below — a slot switch
        // that fails/returns early (SelectedSlot is null, the read throws) still needs to retire any
        // icon resolution left over from whatever slot was loaded before it.
        _iconLoadGeneration++;

        _lastLoadedSlot = SelectedSlot;

        if (SelectedSlot is null)
        {
            LastBackupDisplay = null;
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
            // The five real JSON files a slot load reads (worse for a character-heavy save), off
            // the UI thread — same reasoning LibraryViewModel.CheckModsAgainstCurrentDataAsync
            // already applies to its own whole-library diff pass. Every ObservableCollection
            // mutation below still runs back on this (UI) thread, since that's not safe off it.
            var (profile, characterNodes, accoladesRoot, bestiaryRoot, metaInventoryRoot, mountsRoot) = await Task.Run(() => (
                _repository.LoadProfile(steamId),
                _repository.LoadCharacters(steamId),
                _repository.LoadAccolades(steamId),
                _repository.LoadBestiary(steamId),
                _repository.LoadMetaInventory(steamId),
                _repository.LoadMounts(steamId)));

            if (!ReferenceEquals(SelectedSlot, slotBeingLoaded))
            {
                return;
            }

            _profile = profile;
            _characterNodes = [.. characterNodes];

            foreach (var node in _characterNodes)
            {
                Characters.Add(new SaveCharacterViewModel(node, _gameNames, NotifyDirtyChanged));
            }

            if (_profile["MetaResources"] is JsonArray resources)
            {
                foreach (var resource in resources.OfType<JsonObject>())
                {
                    var key = resource["MetaRow"]?.GetValue<string>() ?? "?";
                    Currencies.Add(new SaveCurrencyViewModel(resource, CurrencyLabels.GetValueOrDefault(key, key), NotifyDirtyChanged));
                }
            }

            BuildAccountFlags();
            BuildBinaryFlags();
            BuildWorkshopTalents();

            _accoladesRoot = accoladesRoot;
            BuildAccolades();
            _bestiaryRoot = bestiaryRoot;
            BuildBestiaryEntries();
            _metaInventoryRoot = metaInventoryRoot;
            BuildMetaInventoryItems();
            _mountsRoot = mountsRoot;
            BuildMounts();

            QueueIconResolution();

            SelectedCharacter = Characters.FirstOrDefault();
            RefreshBackupsList();
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Couldn't read this save: {ex.Message}";
        }

        OnPropertyChanged(nameof(HasUnsavedChanges));
    }

    private void NotifyDirtyChanged() => OnPropertyChanged(nameof(HasUnsavedChanges));

    private void BuildAccountFlags()
    {
        AccountFlags.Clear();
        var unlocked = _profile?["UnlockedFlags"] is JsonArray array
            ? array.Select(n => n?.GetValue<int>() ?? -1).Where(i => i >= 0).ToHashSet()
            : [];
        var count = Math.Max(_gameNames.AccountFlagNames.Count, unlocked.Count > 0 ? unlocked.Max() + 1 : 0);
        for (var id = 0; id < count; id++)
        {
            AccountFlags.Add(new SaveFlagViewModel(id, _gameNames.AccountFlagName(id), unlocked.Contains(id), NotifyDirtyChanged));
        }
    }

    /// <summary>
    /// The slot's flags_&lt;SteamID&gt;.dat holds account-wide unlocks as CHARACTER-flag row indexes
    /// (confirmed against the real file: every ID mapped to a real D_CharacterFlags name —
    /// Talent_RepairBench, Mission_Olympus_Unlock, Unlocked_Bait…). Without reading it, unlocks
    /// the player genuinely has would show as locked — the exact bug report that led here.
    /// </summary>
    private void BuildBinaryFlags()
    {
        BinaryFlags.Clear();
        _binaryFlagIds = null;
        if (SelectedSlot is null)
        {
            OnPropertyChanged(nameof(HasBinaryFlags));
            return;
        }

        try
        {
            _binaryFlagIds = _repository.LoadBinaryFlags(SelectedSlot.SteamId) is { } ids ? [.. ids] : null;
        }
        catch (FormatException)
        {
            // An unreadable flags file just means the section stays hidden — the JSON-side editing
            // still works, and hiding beats showing toggles that couldn't be written back safely.
        }

        if (_binaryFlagIds is { } unlocked)
        {
            var set = unlocked.ToHashSet();
            var count = Math.Max(_gameNames.CharacterFlagNames.Count, set.Count > 0 ? set.Max() + 1 : 0);
            for (var id = 0; id < count; id++)
            {
                BinaryFlags.Add(new SaveFlagViewModel(id, _gameNames.CharacterFlagName(id), set.Contains(id), NotifyDirtyChanged));
            }
        }

        OnPropertyChanged(nameof(HasBinaryFlags));
    }

    private void BuildWorkshopTalents()
    {
        WorkshopTalents.Clear();
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_profile?["Talents"] is JsonArray talentArray)
        {
            foreach (var entry in talentArray.OfType<JsonObject>())
            {
                if (entry["RowName"]?.GetValue<string>() is { } rowName)
                {
                    ranks[rowName] = entry["Rank"]?.GetValue<int>() ?? 0;
                }
            }
        }

        // The profile's own list first (its order), then any Workshop_* talent the game defines
        // that isn't researched yet — rank one up to research it.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, rank) in ranks)
        {
            WorkshopTalents.Add(new SaveTalentViewModel(rowName, SaveCharacterViewModel.DisplayInfoFor(_gameNames, rowName), rank, NotifyDirtyChanged));
            seen.Add(rowName);
        }

        foreach (var (rowName, info) in _gameNames.Talents)
        {
            if (rowName.StartsWith("Workshop_", StringComparison.OrdinalIgnoreCase) && !seen.Contains(rowName))
            {
                WorkshopTalents.Add(new SaveTalentViewModel(rowName, new TalentDisplayInfo(info.DisplayName, info.Description, info.Tree, info.MaxRank, info.IsDefaultUnlocked), 0, NotifyDirtyChanged));
            }
        }
    }

    // --- Accolades (account-wide, Accolades.json — a separate file from Profile.json) ---

    public ObservableCollection<SaveAccoladeViewModel> Accolades { get; } = [];

    public ObservableCollection<SaveAccoladeViewModel> FilteredAccolades { get; } = [];

    [ObservableProperty]
    private string _accoladeSearchText = "";

    partial void OnAccoladeSearchTextChanged(string value) => RefreshAccoladeFilter();

    private void RefreshAccoladeFilter() =>
        RefreshFilter(Accolades, FilteredAccolades, AccoladeSearchText, null, a => a.DisplayName, a => a.Category);

    /// <summary>CompletedAccolades only — PlayerTrackers/PlayerTaskListTrackers (the raw progress counters behind eligibility) are preserved untouched, out of scope for this editor.</summary>
    private void BuildAccolades()
    {
        Accolades.Clear();
        var completed = _accoladesRoot?["CompletedAccolades"] is JsonArray array
            ? array.OfType<JsonObject>()
                .Select(entry => entry["Accolade"]?["RowName"]?.GetValue<string>())
                .Where(name => name is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)!
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, info) in _gameNames.Accolades)
        {
            Accolades.Add(new SaveAccoladeViewModel(rowName, info.DisplayName, info.Description, info.Category, completed.Contains(rowName), NotifyDirtyChanged));
            seen.Add(rowName);
        }

        // A completed accolade the current data tables don't recognize (an old save vs. an updated
        // game) still shows, by RowName — same "never hide anything in the save" rule Talents' own
        // fallback path uses.
        foreach (var rowName in completed.Except(seen))
        {
            Accolades.Add(new SaveAccoladeViewModel(rowName, rowName, "", "", true, NotifyDirtyChanged));
        }

        RefreshAccoladeFilter();
    }

    // --- Bestiary (account-wide, BestiaryData.json — a separate file from Profile.json) ---

    public ObservableCollection<SaveBestiaryEntryViewModel> BestiaryEntries { get; } = [];

    public ObservableCollection<SaveBestiaryEntryViewModel> FilteredBestiaryEntries { get; } = [];

    [ObservableProperty]
    private string _bestiarySearchText = "";

    partial void OnBestiarySearchTextChanged(string value) => RefreshBestiaryFilter();

    private void RefreshBestiaryFilter() =>
        RefreshFilter(BestiaryEntries, FilteredBestiaryEntries, BestiarySearchText, null, b => b.DisplayName);

    /// <summary>BestiaryTracking only — FishTracking (a separate fishing sub-tracker) is preserved untouched, out of scope for this editor.</summary>
    private void BuildBestiaryEntries()
    {
        BestiaryEntries.Clear();
        var points = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_bestiaryRoot?["BestiaryTracking"] is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                if (entry["BestiaryGroup"]?["RowName"]?.GetValue<string>() is { } rowName)
                {
                    points[rowName] = entry["NumPoints"]?.GetValue<int>() ?? 0;
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, info) in _gameNames.BestiaryCreatures)
        {
            BestiaryEntries.Add(new SaveBestiaryEntryViewModel(rowName, info.DisplayName, info.PointsRequired, info.IsBoss, points.GetValueOrDefault(rowName), info.ImagePath, NotifyDirtyChanged));
            seen.Add(rowName);
        }

        foreach (var (rowName, currentPoints) in points)
        {
            if (!seen.Contains(rowName))
            {
                BestiaryEntries.Add(new SaveBestiaryEntryViewModel(rowName, rowName, 0, false, currentPoints, null, NotifyDirtyChanged));
            }
        }

        RefreshBestiaryFilter();
    }

    /// <summary>Writes Accolades back into its own root node — same minimal-diff order-preserving rule as ApplyProfileEdits, just against Accolades.json's CompletedAccolades array instead of Profile.json's UnlockedFlags.</summary>
    private void ApplyAccoladeEdits()
    {
        if (_accoladesRoot is null)
        {
            return;
        }

        var nowCompleted = Accolades.Where(a => a.IsCompleted).Select(a => a.RowName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newArray = new JsonArray();
        if (_accoladesRoot["CompletedAccolades"] is JsonArray existing)
        {
            foreach (var entry in existing.OfType<JsonObject>())
            {
                // Kept exactly as the game wrote it (TimeCompleted/ProspectID preserved) — only
                // whether it stays in the list changes here, never its own fields.
                if (entry["Accolade"]?["RowName"]?.GetValue<string>() is { } rowName && nowCompleted.Contains(rowName))
                {
                    newArray.Add(entry.DeepClone());
                }
            }
        }

        var alreadyWritten = newArray.OfType<JsonObject>()
            .Select(e => e["Accolade"]?["RowName"]?.GetValue<string>())
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        foreach (var accolade in Accolades)
        {
            if (accolade.IsCompleted && !alreadyWritten.Contains(accolade.RowName))
            {
                newArray.Add(new JsonObject
                {
                    ["Accolade"] = new JsonObject { ["RowName"] = accolade.RowName, ["DataTableName"] = "D_Accolades" },
                    ["TimeCompleted"] = DateTime.Now.ToString("yyyy.MM.dd-HH.mm.ss"),
                    ["ProspectID"] = "",
                });
            }
        }

        _accoladesRoot["CompletedAccolades"] = newArray;
    }

    /// <summary>Writes BestiaryEntries back into its own root node — a plain per-row rebuild (unlike the array-membership rewrite pattern above) since every tracked creature always has exactly one NumPoints entry, not a present/absent one.</summary>
    private void ApplyBestiaryEdits()
    {
        if (_bestiaryRoot is null)
        {
            return;
        }

        var newArray = new JsonArray();
        foreach (var entry in BestiaryEntries)
        {
            // EffectivePoints, not a re-parse of PointsText: a box left unparsable (e.g. cleared
            // mid-edit) must fall back to the entry's own last-known-good value, never silently
            // drop the whole creature's tracking entry out of the save.
            var points = entry.EffectivePoints;
            if (points > 0)
            {
                newArray.Add(new JsonObject
                {
                    ["BestiaryGroup"] = new JsonObject { ["RowName"] = entry.RowName, ["DataTableName"] = "D_BestiaryData" },
                    ["NumPoints"] = points,
                });
            }
        }

        _bestiaryRoot["BestiaryTracking"] = newArray;
    }

    // --- Items (account-wide, MetaInventory.json — a separate file from Profile.json) ---

    public ObservableCollection<SaveInventoryItemViewModel> MetaInventoryItems { get; } = [];

    public ObservableCollection<SaveInventoryItemViewModel> FilteredMetaInventoryItems { get; } = [];

    [ObservableProperty]
    private string _itemSearchText = "";

    partial void OnItemSearchTextChanged(string value) => RefreshItemFilter();

    private void RefreshItemFilter() =>
        RefreshFilter(MetaInventoryItems, FilteredMetaInventoryItems, ItemSearchText, null, i => i.DisplayName, i => i.RowName);

    private void BuildMetaInventoryItems()
    {
        MetaInventoryItems.Clear();
        _metaInventoryDirty = false;
        if (_metaInventoryRoot?["Items"] is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                var rowName = entry["ItemStaticData"]?["RowName"]?.GetValue<string>() ?? "?";
                var info = _gameNames.Items.GetValueOrDefault(rowName);
                MetaInventoryItems.Add(new SaveInventoryItemViewModel(entry, info?.DisplayName ?? rowName, rowName, info?.Weight ?? 0, info?.MaxStack ?? 1, info?.IconPath));
            }
        }

        RefreshItemFilter();
    }

    /// <summary>Deliberately view + delete only (see SaveInventoryItemViewModel's own doc comment) — every surviving item's Node is its own original JsonObject, written back completely untouched.</summary>
    [RelayCommand]
    private void DeleteMetaInventoryItem(SaveInventoryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        MetaInventoryItems.Remove(item);
        FilteredMetaInventoryItems.Remove(item);
        _metaInventoryDirty = true;
        NotifyDirtyChanged();
    }

    private void ApplyMetaInventoryEdits()
    {
        if (_metaInventoryRoot is null)
        {
            return;
        }

        var newArray = new JsonArray();
        foreach (var item in MetaInventoryItems)
        {
            newArray.Add(item.Node.DeepClone());
        }

        _metaInventoryRoot["Items"] = newArray;
    }

    // --- Mounts (account-wide, Mounts.json — a separate file from Profile.json) ---

    public ObservableCollection<SaveMountViewModel> Mounts { get; } = [];

    public ObservableCollection<SaveMountViewModel> FilteredMounts { get; } = [];

    [ObservableProperty]
    private string _mountSearchText = "";

    partial void OnMountSearchTextChanged(string value) => RefreshMountFilter();

    private void RefreshMountFilter() =>
        RefreshFilter(Mounts, FilteredMounts, MountSearchText, null, m => m.Name, m => m.TypeDisplayName);

    /// <summary>Name/Level/Type only — RecorderBlob (the mount's real stats/stomach/saddle state, a raw Unreal binary blob, not JSON) is preserved untouched on each mount's own live Node. See SaveMountViewModel's own doc comment for why a mount has no natural key to rebuild an array by, unlike Bestiary/Accolades.</summary>
    private void BuildMounts()
    {
        Mounts.Clear();
        var availableTypes = _gameNames.MountTypeRowNames;
        if (_mountsRoot?["SavedMounts"] is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                var name = entry["MountName"]?.GetValue<string>() ?? "";
                var level = entry["MountLevel"]?.GetValue<int>() ?? 0;
                var typeRowName = entry["MountType"]?.GetValue<string>() ?? "";
                // Guarantee the mount's own current type is always in its own picker list — the
                // game data folder may not be extracted yet (empty availableTypes) or may no
                // longer carry this exact row, and the value should stay visible either way,
                // matching how a talent rank above its documented cap is kept as-is rather than
                // silently dropped.
                var typesForThisMount = availableTypes.Contains(typeRowName) || typeRowName.Length == 0
                    ? availableTypes
                    : [.. availableTypes, typeRowName];
                var iconPath = _gameNames.MountTypeIcons.GetValueOrDefault(typeRowName);
                Mounts.Add(new SaveMountViewModel(entry, name, level, typeRowName, typesForThisMount, iconPath, NotifyDirtyChanged));
            }
        }

        RefreshMountFilter();
    }

    /// <summary>Same view+delete shape as DeleteMetaInventoryItem, mirrored exactly — no confirmation prompt (a mount is one collectible among many, the same severity as one inventory item, unlike deleting a whole character), just remove-and-mark-dirty; Save's own confirm covers the actual write.</summary>
    [RelayCommand]
    private void DeleteMount(SaveMountViewModel? mount)
    {
        if (mount is null)
        {
            return;
        }

        Mounts.Remove(mount);
        FilteredMounts.Remove(mount);
        _mountsListDirty = true;
        NotifyDirtyChanged();
    }

    /// <summary>
    /// Full rebuild from the CURRENT Mounts collection, not an in-place-only foreach over it — a
    /// mount removed from Mounts (DeleteMount, above) never touched _mountsRoot itself, since each
    /// mount's own Node is still a live child of the OLD SavedMounts array at that point; an
    /// in-place-only pass (this method's own shape before mount deletion existed) would silently
    /// keep a "deleted" mount in the written file. DeepClone is required, not optional, on the way
    /// into the new array: a JsonNode can only ever belong to one parent, and mount.Node is still
    /// parented to the array this replaces. Mirrors ApplyMetaInventoryEdits' own reasoning.
    /// </summary>
    private void ApplyMountEdits()
    {
        if (_mountsRoot is null)
        {
            return;
        }

        var newArray = new JsonArray();
        foreach (var mount in Mounts)
        {
            mount.ApplyToNode();
            newArray.Add(mount.Node.DeepClone());
        }

        _mountsRoot["SavedMounts"] = newArray;
    }

    // --- Icons (item/mount/creature thumbnails, resolved lazily through the base-game content
    //     provider — see IBaseGameIconDecoder's own doc comment for the real decode this wraps) ---

    /// <summary>
    /// Fire-and-forget, called once from LoadSlotAsync right after every icon-bearing row
    /// (MetaInventoryItems/Mounts/BestiaryEntries) already exists — same "list first, expensive
    /// per-item extras after" precedent LibraryItemViewModel.EnsureDetailsLoaded already sets for
    /// LoadRemoteThumbnailAsync, just for a whole page of rows instead of one mod's single
    /// thumbnail. Every row shows its plain text immediately regardless of how long (or whether) its
    /// own icon ever resolves.
    /// </summary>
    private void QueueIconResolution()
    {
        var requests = new List<(string? IconPath, Action<BitmapImage> SetIcon)>();
        foreach (var item in MetaInventoryItems)
        {
            requests.Add((item.IconPath, bitmap => item.Icon = bitmap));
        }

        foreach (var mount in Mounts)
        {
            requests.Add((mount.IconPath, bitmap => mount.Icon = bitmap));
        }

        foreach (var creature in BestiaryEntries)
        {
            requests.Add((creature.ImagePath, bitmap => creature.Icon = bitmap));
        }

        _pendingIconLoad = LoadIconsAsync(requests, _iconLoadGeneration);
    }

    /// <summary>
    /// Resolves every request ONE AT A TIME — deliberately not Task.WhenAll'd — both because
    /// IBaseGameContentProvider's own real DefaultFileProvider mount was never confirmed
    /// thread-safe for concurrent LoadPackage calls (only ever exercised from one decode at a time
    /// so far, per CueUassetMaterialDecoder's own fallback), and because staying sequential means
    /// every `await Task.Run(...)` below resumes back on THIS (UI) thread's own captured
    /// SynchronizationContext — the same mechanism DecodeCompiledAssetPreviewAsync already relies on
    /// — so each row's own Icon assignment is always a plain UI-thread property set, no manual
    /// Dispatcher marshaling required. A save with many Bestiary rows (every creature the game
    /// defines, not just ones the player's encountered — see BuildBestiaryEntries) means this can
    /// trail on in the background for a while after a slot loads; that's real but bounded (no UI
    /// thread time, one background thread, nothing blocks on it) rather than a correctness problem.
    /// </summary>
    private async Task LoadIconsAsync(IReadOnlyList<(string? IconPath, Action<BitmapImage> SetIcon)> requests, int generation)
    {
        foreach (var (iconPath, setIcon) in requests)
        {
            if (generation != _iconLoadGeneration)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(iconPath))
            {
                continue;
            }

            if (!_iconCache.TryGetValue(iconPath, out var bitmap))
            {
                var pngBytes = await Task.Run(() => _baseGameIconDecoder.TryDecodeIconToPng(iconPath));
                bitmap = pngBytes is null ? null : TryDecodeImage(pngBytes);
                _iconCache[iconPath] = bitmap;
            }

            if (generation != _iconLoadGeneration)
            {
                return;
            }

            if (bitmap is not null)
            {
                setIcon(bitmap);
            }
        }
    }

    /// <summary>Decodes image bytes to a frozen bitmap, or null if they aren't a decodable image — same shape as LibraryItemViewModel's own private TryDecodeImage, duplicated rather than shared since nothing currently ties this page to that one.</summary>
    private static BitmapImage? TryDecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Writes AccountFlags/WorkshopTalents back into the profile node — the profile-level counterpart of SaveCharacterViewModel.ApplyToNode, same minimal-diff rules.</summary>
    private void ApplyProfileEdits()
    {
        if (_profile is null)
        {
            return;
        }

        var originallyUnlocked = _profile["UnlockedFlags"] is JsonArray array
            ? array.Select(n => n?.GetValue<int>() ?? -1).Where(i => i >= 0).ToList()
            : [];
        var nowUnlocked = AccountFlags.Where(f => f.IsUnlocked).Select(f => f.Id).ToHashSet();
        var flagArray = new JsonArray();
        foreach (var id in originallyUnlocked.Where(nowUnlocked.Contains))
        {
            flagArray.Add(id);
        }

        foreach (var id in nowUnlocked.Except(originallyUnlocked).OrderBy(i => i))
        {
            flagArray.Add(id);
        }

        _profile["UnlockedFlags"] = flagArray;

        var byRow = WorkshopTalents.ToDictionary(t => t.RowName, t => t.Rank, StringComparer.OrdinalIgnoreCase);
        var talentArrayNew = new JsonArray();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_profile["Talents"] is JsonArray existing)
        {
            foreach (var entry in existing.OfType<JsonObject>())
            {
                if (entry["RowName"]?.GetValue<string>() is { } rowName && byRow.GetValueOrDefault(rowName) > 0)
                {
                    talentArrayNew.Add(new JsonObject { ["RowName"] = rowName, ["Rank"] = byRow[rowName] });
                    written.Add(rowName);
                }
            }
        }

        foreach (var talent in WorkshopTalents)
        {
            if (talent.Rank > 0 && !written.Contains(talent.RowName))
            {
                talentArrayNew.Add(new JsonObject { ["RowName"] = talent.RowName, ["Rank"] = talent.Rank });
            }
        }

        _profile["Talents"] = talentArrayNew;
    }

    private void RefreshBackupsList()
    {
        Backups.Clear();
        if (SelectedSlot is null)
        {
            return;
        }

        foreach (var backup in _repository.ListBackups(SelectedSlot.SteamId))
        {
            Backups.Add(backup);
        }

        SelectedBackup = Backups.FirstOrDefault();
        LastBackupDisplay = Backups.FirstOrDefault() is { } newest
            ? $"Last backup: {newest.TakenAtUtc.LocalDateTime:g}"
            : "No backups yet.";
    }

    /// <summary>The spec's "Click a card to edit that character" — Overview card → Characters tab with that character selected.</summary>
    [RelayCommand]
    private void EditCharacter(SaveCharacterViewModel? character)
    {
        if (character is null)
        {
            return;
        }

        SelectedCharacter = character;
        SelectedTabIndex = 1;
    }

    /// <summary>
    /// Clones the selected character's own JsonObject wholesale (deep clone — a JsonNode can only
    /// ever belong to one parent, so the clone must be fully independent before it's added
    /// anywhere, same reasoning every other DeepClone call in this class already relies on). ChrSlot
    /// is the real identifier the game itself uses to keep characters distinct in Characters.json
    /// (confirmed against a real save: every character carries one, and the profile's own
    /// NextChrSlot is exactly the counter the game increments each time it hands out a new one) —
    /// two characters sharing a ChrSlot would be a genuine collision, not just a cosmetic one, so
    /// the duplicate gets a freshly allocated slot via AllocateNextChrSlot() rather than a copy of
    /// the source's. CharacterName carries no such uniqueness requirement in the format, but
    /// "(Copy)" is still appended so the new entry doesn't look identical to its source in every
    /// list this page shows it in (Overview cards, the Characters list).
    /// </summary>
    [RelayCommand]
    private void DuplicateCharacter(SaveCharacterViewModel? character)
    {
        if (character is null)
        {
            return;
        }

        var clone = character.Node.DeepClone().AsObject();
        clone["ChrSlot"] = AllocateNextChrSlot();
        var newName = $"{character.Name} (Copy)";
        clone["CharacterName"] = newName;

        var newCharacter = new SaveCharacterViewModel(clone, _gameNames, NotifyDirtyChanged);

        // Kept adjacent to the source in both lists — purely cosmetic (order has no meaning to the
        // game; SaveCharacters below writes the whole array regardless of order), but it keeps the
        // duplicate easy to find right next to what it was duplicated from.
        var nodeIndex = _characterNodes.IndexOf(character.Node);
        _characterNodes.Insert(nodeIndex >= 0 ? nodeIndex + 1 : _characterNodes.Count, clone);
        var vmIndex = Characters.IndexOf(character);
        Characters.Insert(vmIndex >= 0 ? vmIndex + 1 : Characters.Count, newCharacter);

        _charactersListDirty = true;
        SelectedCharacter = newCharacter;
        NotifyDirtyChanged();
        StatusMessage = $"Duplicated '{character.Name}' as '{newName}' (slot {clone["ChrSlot"]!.GetValue<int>()}) — click Save player data to make it permanent.";
    }

    /// <summary>
    /// The next ChrSlot value nothing currently on this profile is using. Starts from the
    /// profile's own NextChrSlot (exactly what the game itself would hand out next) but never
    /// trusts it blindly — same defensive posture as BuildAccountFlags/BuildBinaryFlags' own
    /// Math.Max fallbacks elsewhere in this class — in case a save's NextChrSlot is stale, missing,
    /// or a prior duplicate already consumed it without the profile having been reloaded. Also
    /// advances NextChrSlot past the value just handed out, so the game itself (or a second
    /// Duplicate right after this one) can't be handed the same slot next.
    /// </summary>
    private int AllocateNextChrSlot()
    {
        var used = _characterNodes.Select(n => n["ChrSlot"]?.GetValue<int>() ?? -1).Where(id => id >= 0).ToHashSet();
        var candidate = _profile?["NextChrSlot"]?.GetValue<int>() ?? (used.Count == 0 ? 0 : used.Max() + 1);
        while (used.Contains(candidate))
        {
            candidate++;
        }

        if (_profile is not null)
        {
            _profile["NextChrSlot"] = candidate + 1;
        }

        return candidate;
    }

    /// <summary>
    /// Deleting a whole character is far more consequential than any other destructive list-edit on
    /// this page (talents, flags, cosmetics, XP — the character's entire progress, gone), which is
    /// why this one confirms first via IDialogService — unlike DeleteMetaInventoryItem/DeleteMount,
    /// which mirror this page's usual "staged edit, Save's own confirm covers it" pattern, since
    /// losing one item or one mount is a much smaller loss to walk back from.
    /// </summary>
    [RelayCommand]
    private void DeleteCharacter(SaveCharacterViewModel? character)
    {
        if (character is null)
        {
            return;
        }

        var confirm = _dialogService.Confirm(
            $"Permanently remove '{character.Name}' from this save slot?\n\n"
            + "This deletes the character's entire progress — talents, flags, cosmetics, XP, everything — the next time you save.",
            "Delete character", ThemedConfirmSeverity.Warning);
        if (!confirm)
        {
            return;
        }

        _characterNodes.Remove(character.Node);
        Characters.Remove(character);
        if (ReferenceEquals(SelectedCharacter, character))
        {
            SelectedCharacter = Characters.FirstOrDefault();
        }

        _charactersListDirty = true;
        NotifyDirtyChanged();
        StatusMessage = $"Removed '{character.Name}' — click Save player data to make it permanent.";
    }

    [RelayCommand]
    private void BackupNow()
    {
        if (SelectedSlot is null)
        {
            return;
        }

        try
        {
            var zipPath = _repository.BackupSlot(SelectedSlot.SteamId);
            RefreshBackupsList();
            StatusMessage = $"Backed up to '{Path.GetFileName(zipPath)}'.";
            _activityLog.Log($"Backed up save slot {SelectedSlot.Display}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (SelectedSlot is null || SelectedBackup is null)
        {
            return;
        }

        if (IsGameRunning())
        {
            StatusMessage = "Icarus is running — close it before restoring, or the game will overwrite the restored files when it exits.";
            return;
        }

        // Via IDialogService, not ThemedMessageBox.Show directly (unlike OnSelectedSlotChanged's
        // own confirm above this method) — this method's real write below needs to be testable
        // without a live WPF Dispatcher, same reasoning DeleteCharacter's own confirm relies on.
        var confirm = _dialogService.Confirm(
            $"Replace this save slot's current files with the backup from {SelectedBackup.TakenAtUtc.LocalDateTime:g}?\n\n"
            + "A pre_restore safety zip of the slot as it is right now is written first, so this is itself undoable.",
            "Restore save backup", ThemedConfirmSeverity.Warning);
        if (!(confirm))
        {
            return;
        }

        try
        {
            // A SECOND, LATE check, immediately before the real write below — the first
            // IsGameRunning() check above (before the confirm dialog) can go stale for however long
            // the user takes to answer that dialog, and Icarus could be launched in that window.
            // This doesn't close the race entirely (no check here can, without an OS-level file
            // lock held across processes for the whole write) — it only shrinks the exposed window
            // down to "immediately before the write" instead of "however long the confirm dialog
            // was up", aborting late-and-safe (nothing has been touched yet) rather than
            // early-and-stale.
            if (IsGameRunning())
            {
                StatusMessage = "Icarus started running while this restore was about to write — nothing was touched. Close it and try again.";
                return;
            }

            _repository.RestoreSlot(SelectedSlot.SteamId, SelectedBackup.FilePath);
            await LoadSlotAsync();
            StatusMessage = "Restored. The replaced state was saved as a pre_restore zip.";
            _activityLog.Log($"Restored save slot {SelectedSlot.Display} from backup.", ActivityEntryKind.Warning);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
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

        try
        {
            foreach (var character in Characters)
            {
                character.ApplyToNode();
            }

            foreach (var currency in Currencies)
            {
                currency.ApplyToNode();
            }

            ApplyProfileEdits();

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

            // ONE backup for the whole save pass, taken up front — every SaveXxx call below passes
            // takeBackup: false. A single click used to be able to zip the same slot up to 6 times
            // back-to-back (Characters/Profile always, plus BinaryFlags/Accolades/Bestiary/
            // MetaInventory whenever dirty), each one blocking the UI in turn, when one pre-save
            // snapshot already covers everything this pass is about to change.
            _repository.BackupSlot(SelectedSlot.SteamId);

            // Characters first, then profile — keeps write ORDER consistent with before, even
            // though backup ordering no longer matters now that there's just the one upfront zip.
            _repository.SaveCharacters(SelectedSlot.SteamId, _characterNodes, takeBackup: false);
            _repository.SaveProfile(SelectedSlot.SteamId, _profile!, takeBackup: false);

            // The binary flags file is written only when actually edited — same minimal-diff
            // philosophy as the JSON arrays: the file's own ordering is kept for flags that stay
            // set, newly-set ones append.
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

            // Characters (unlike Mounts above) always writes unconditionally via _characterNodes,
            // so no write-gating is needed for _charactersListDirty — it only ever gated whether
            // the Save button itself was enabled, so it's cleared here alongside every other
            // dirty-tracking reset now that the write has actually happened.
            _charactersListDirty = false;

            OnPropertyChanged(nameof(HasUnsavedChanges));
            RefreshBackupsList();
            StatusMessage = "Saved. A backup of the previous state was kept.";
            _activityLog.Log($"Edited player save {SelectedSlot.Display}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenBackupsFolder()
    {
        if (Backups.FirstOrDefault() is not { } any)
        {
            StatusMessage = "No backups yet — take one first.";
            return;
        }

        UrlOpener.TryOpen(Path.GetDirectoryName(any.FilePath)!);
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
