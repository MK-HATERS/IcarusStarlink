using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Converters;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.InstallComparison;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Patches;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Diffing;
using IcarusStarlink.PakIO;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.GameplayToggles;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Patches;
using IcarusStarlink.PakIO.Rebuild;
using Microsoft.Win32;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// Phase 6 (see the plan's "Update (2026-08-21)" section): Profile bar (6.3) + merge queue +
/// Rebuild (6.1) + Install (6.2). Rebuild only ever writes to a staged pak under IcarusStarlink's
/// own folder; Install is the one action in this whole app that writes into the real game's
/// Content\Paks\mods — deliberately its own separate, explicit button rather than folded into
/// Rebuild, so a click there is never accidental.
/// </summary>
public sealed partial class MergeInstallViewModel : ObservableObject, IcarusStarlink.Core.Memory.IIdleReleasable
{
    private readonly ILibraryRepository _libraryRepository;
    private readonly IRebuildService _rebuildService;
    private readonly IInstallService _installService;
    private readonly IProfileStore _profileStore;
    private readonly IPatchService _patchService;
    private readonly IDaedalusCatalogClient _daedalusClient;
    private readonly IJimk72CatalogClient _jimk72Client;
    private readonly ISettingsService _settingsService;
    private readonly IPakCompareService _pakCompareService;
    private readonly PerformanceTracker _performanceTracker;
    private readonly IActivityLog _activityLog;
    private readonly string _dataFolder;
    private readonly string _outputPakPath;
    private readonly string _backupDirectory;

    /// <summary>
    /// Per-folder cache of ExmodFolder.Read's own result, keyed by LibraryEntry.FolderName plus a
    /// cheap on-disk fingerprint (see ComputeFolderFingerprint) — LoadQueuedPackagesAsync runs on
    /// every Queue mutation (both RecomputeConflictCountAsync and RecomputeValidationIssueCountAsync
    /// call it independently, per the Queue.CollectionChanged handler below), and until this existed
    /// EVERY queued mod's own .EXMOD got re-read and re-parsed from disk on every single one of
    /// those, even for a mod that wasn't the one that changed (e.g. reordering two OTHER mods, or
    /// adding a brand-new one). Guarded by _packageCacheLock: those two Recompute methods genuinely
    /// can run concurrently on background threads (both are fired, undebounced relative to each
    /// other, from the same handler), and Dictionary isn't thread-safe.
    /// </summary>
    private readonly Dictionary<string, (FolderFingerprint Fingerprint, ExmodPackageContents Package)> _packageCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly object _packageCacheLock = new();

    /// <summary>
    /// FindQueueValidationIssuesAsync's own base DataTableRowIndex.Build(_dataFolder) call — a full
    /// walk-and-parse of every extracted game-data JSON file (measured ~500ms-1.3s against this
    /// app's real ~300-file data folder) — used to run fresh on every single call, even though
    /// RecomputeValidationIssueCountAsync (its only real caller) fires on every debounced Queue
    /// mutation and _dataFolder's own contents only ever change on a real "Update data folder" run.
    /// GameDataIndexCache already solved this exact problem for the same data; this mirrors that
    /// same lazy-build-once-then-invalidate-on-WeeklyChangeReportUpdatedMessage pattern locally,
    /// under _packageCacheLock (reused rather than a second lock — both guard state read/written by
    /// the same pair of concurrently-fired Recompute methods). WithDeclaredRows below returns a
    /// fresh copy without mutating the cached base index, so caching it here is safe.
    /// </summary>
    private DataTableRowIndex? _cachedBaseDataTableIndex;

    /// <summary>Debounces the conflict/validation recompute that follows a Queue mutation — see the Queue.CollectionChanged handler below for why.</summary>
    private readonly DebounceTimer _recomputeDebounceTimer;

    /// <summary>
    /// Test-only seam (see DebounceTimerTests' own doc comment on why a real elapsed-time firing
    /// isn't observable without a live Dispatcher message pump): proves a Queue mutation defers to
    /// the debounced recompute rather than running it synchronously inline, without needing the
    /// timer to actually elapse. RecomputeConflictCountAsync/RecomputeValidationIssueCountAsync
    /// (internal for the same reason) let a test then invoke the exact same recompute the timer
    /// would have, on demand, to check it produces the right answer for the current queue.
    /// </summary>
    internal bool IsRecomputeDebouncePending => _recomputeDebounceTimer.IsRunning;

    /// <summary>A cheap-to-compute stand-in for "have this folder's own files changed since the cached package was read" — file count (catches an add/remove) plus the newest LastWriteTimeUtc among the folder's own files (catches an in-place edit, e.g. ExmodEditorViewModel.Save overwriting the same .EXMOD filename — a Windows directory's OWN LastWriteTimeUtc does NOT reliably bump just because a child file's content changed, only its own entry does, so the folder's own timestamp alone isn't safe to key on).</summary>
    private readonly record struct FolderFingerprint(int FileCount, DateTime MaxLastWriteUtc);

    public string Title => "Merge & Install";

    /// <summary>
    /// Mods queued for the next Rebuild — a mix of real EXMOD mods (field-merged against base game
    /// data) and opaque/prebuilt .pak entries (LibraryEntry.IsOpaquePak, no .EXMOD — unpacked and
    /// folded into the same merged pak instead, see RebuildService). Both live in one list since
    /// both end up in the same output; LoadQueuedPackagesAsync is what splits them back apart for
    /// RebuildService's own two-parameter signature. Mods reach this queue from Library's own
    /// multi-select + "Add to merge queue" action (AddToQueueByFolderNames) — this page no longer
    /// carries its own Library browsing pane, per the redesign-round-2 pilot.
    /// </summary>
    public ObservableCollection<LibraryEntry> Queue { get; } = [];

    /// <summary>Drives the empty-queue hint pointing the user at Library, now that this page has no browsing pane of its own to invite them into.</summary>
    public bool HasQueuedMods => Queue.Count > 0;

    /// <summary>A real computed property, not BoolToVisibilityConverter+ConverterParameter="Invert" — that shape has bitten this app before (see the boolean-converter mistake memory).</summary>
    public bool IsQueueEmpty => !HasQueuedMods;

    [ObservableProperty]
    private LibraryEntry? _selectedQueueEntry;

    /// <summary>
    /// While a build runs the queue, profile and merge options can't be changed (the build works from
    /// a snapshot taken when it starts, see BuildSnapshot; this keeps what's on screen matching it).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotRebuilding))]
    private bool _isRebuilding;

    public bool IsNotRebuilding => !IsRebuilding;

    /// <summary>A queue/profile edit arriving while a build runs: refused with a status line (true), so what's built is what's shown.</summary>
    private bool RefuseWhileRebuilding()
    {
        if (!IsRebuilding)
        {
            return false;
        }

        StatusMessage = "Merge & Install is building — wait for it to finish before changing the queue or profile.";
        return true;
    }

    [ObservableProperty]
    private int _rebuildProgressPercent;

    [ObservableProperty]
    private string? _rebuildStageText;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>Whether this app's own pak is currently installed in the real game folder — refreshed at construction and after a successful install/remove, never live-polled. Drives InstallButtonLabel; deliberately coarse (not "has the queue changed since the last install", which would need a live diff every queue edit) — see the combined Install button's own doc comment for why.</summary>
    [ObservableProperty]
    private bool _hasExistingInstall;

    public string InstallButtonLabel => HasExistingInstall ? "Update install" : "Install";

    partial void OnHasExistingInstallChanged(bool value) => OnPropertyChanged(nameof(InstallButtonLabel));

    [ObservableProperty]
    private string? _installStatusMessage;

    public ObservableCollection<string> Warnings { get; } = [];

    // --- Advanced conflict picker ---
    // Null means "no manual picks" (every conflict resolves via the registry's own default rule).
    // Invalidated (reset to null) whenever Queue changes at all — a pick's own index only means
    // the mod it meant when it was made; any Add/Remove/Move/Clear could change which mod that
    // index now refers to, or whether the field is even still a conflict at all.
    private IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? _manualPicks;

    /// <summary>
    /// Conflict winners (see Profile.ConflictWinners), loaded and saved with the profile: "file|row|field"
    /// → the winner's id, its Library folder name (FolderName/pak name for one pak of a prebuilt
    /// entry; OptionsWinnerId / ArcFissionWinnerId for those two). A winner still saved by display
    /// name (before ids) is moved to its id when the profile loads, where that's unambiguous.
    /// </summary>
    private Dictionary<string, string> _winners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The winner id of the built-in gameplay options (also their name in the conflict window).</summary>
    internal const string OptionsWinnerId = "Built-in gameplay options";

    /// <summary>The winner id of ArcFission Library's data pak (its file name's number changes with every rebuild it does).</summary>
    internal const string ArcFissionWinnerId = "ArcFission Library data";

    /// <summary>Test-only: the saved winners as the profile will store them.</summary>
    internal IReadOnlyDictionary<string, string> WinnersForTesting => _winners;

    /// <summary>How many conflict winners the user has picked for this profile.</summary>
    public int SavedWinnerCount => _winners.Count;

    /// <summary>Test-only seam — see AssemblyInfo.cs's own InternalsVisibleTo grant. Lets a test set up a stored manual pick and then assert whether a given invalidation trigger correctly cleared it, without a real conflict-picker dialog flow.</summary>
    internal IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? ManualPicksForTesting
    {
        get => _manualPicks;
        set => _manualPicks = value;
    }

    [ObservableProperty]
    private string? _conflictStatusMessage;

    /// <summary>Phase 10: a proactive conflict-count badge on the queue, recomputed in the background whenever Queue changes — Review conflicts still does the same computation synchronously for the authoritative picker flow; this is a best-effort preview, not load-bearing.</summary>
    [ObservableProperty]
    private int _conflictCount;

    public bool HasConflicts => ConflictCount > 0;

    partial void OnConflictCountChanged(int value) => OnPropertyChanged(nameof(HasConflicts));

    /// <summary>The bar's counts and wording; ConflictCount is its NeedsDecision. Set only through ApplyConflicts, which the review window's own count comes from too.</summary>
    [ObservableProperty]
    private MergeConflictTally _conflictTally = MergeConflictTally.None;

    /// <summary>
    /// Keyed by LibraryEntry.Name (matching ConflictCandidate.ModName's own "display text, not a
    /// folder identifier" convention — see FieldConflict's doc comment) — lets a queue row's own
    /// DataTemplate show which OTHER queued mods it conflicts with, without needing to open Review
    /// conflicts first. Recomputed alongside ConflictCount everywhere that already runs
    /// FindQueueConflictsAsync, so it never drifts out of sync with the badge/banner. Only values
    /// needing a decision mark a row: same-name new items are combined with nothing to pick.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _conflictingModNamesByMod = new Dictionary<string, IReadOnlyList<string>>();

    // --- Mod validation ("will this actually do anything in-game") ---
    [ObservableProperty]
    private string? _validationStatusMessage;

    /// <summary>Proactive badge on the queue, recomputed in the background whenever Queue changes — same shape as ConflictCount, but flags a mod setting a field name/type or a reference that isn't real in the currently-extracted game data (the merge and pak build both succeed either way, but the game's own importer silently ignores what it doesn't recognize, so the edit does nothing in-game).</summary>
    [ObservableProperty]
    private int _validationIssueCount;

    public bool HasValidationIssues => ValidationIssueCount > 0;

    partial void OnValidationIssueCountChanged(int value) => OnPropertyChanged(nameof(HasValidationIssues));

    // --- Profile bar (6.3) ---
    public ObservableCollection<string> ProfileNames { get; } = [];

    [ObservableProperty]
    private string? _selectedProfileName;

    [ObservableProperty]
    private string? _profileStatusMessage;

    // --- Export/Import patch (6.3) ---
    [ObservableProperty]
    private bool _isExportingPatch;

    /// <summary>Bound to the Export patch button's own IsEnabled — a real, if slow, network + file
    /// operation (catalog fetch, then a SaveFileDialog), so a fast double-click could otherwise
    /// start two overlapping exports.</summary>
    public bool IsNotExportingPatch => !IsExportingPatch;

    partial void OnIsExportingPatchChanged(bool value) => OnPropertyChanged(nameof(IsNotExportingPatch));

    [ObservableProperty]
    private string? _patchStatusMessage;

    // --- Gameplay-toggle merge options (6.4) ---
    public static IReadOnlyList<BoostLevel> BoostLevels { get; } = Enum.GetValues<BoostLevel>();

    public static IReadOnlyList<XpBoostLevel> XpBoostLevels { get; } = Enum.GetValues<XpBoostLevel>();

    public static IReadOnlyList<CraftCostReduction> CraftCostReductions { get; } = Enum.GetValues<CraftCostReduction>();

    [ObservableProperty]
    private BoostLevel _speedBoost;

    [ObservableProperty]
    private BoostLevel _playerBoost;

    [ObservableProperty]
    private XpBoostLevel _xpBoost;

    [ObservableProperty]
    private CraftCostReduction _craftCost;

    /// <summary>Fixed multiplier pills, not free text — classic IMM never documented an exact number for its own Stacks/Slots toggles, but discrete common values (matching the other pill selectors' own UX) beat a bare text box a user has to guess at. A real (non-nullable) enum, not a `double?` with null="Off": WPF's Selector conflates SelectedItem==null with "nothing selected" rather than "the null item is selected", so a nullable-double pill list never shows Off as highlighted on load — found live, the exact bug this enum avoids.</summary>
    public static IReadOnlyList<MultiplierLevel> MultiplierOptions { get; } = Enum.GetValues<MultiplierLevel>();

    [ObservableProperty]
    private MultiplierLevel _stacksMultiplierLevel;

    [ObservableProperty]
    private MultiplierLevel _slotsMultiplierLevel;

    /// <summary>0 = off/unchanged — the Slider's own natural "nothing set" value, so no separate null-state to track the way the multiplier pills need one.</summary>
    [ObservableProperty]
    private int _speedCraftingPercent;

    /// <summary>Same "0 = unchanged" Slider convention as SpeedCraftingPercent.</summary>
    [ObservableProperty]
    private int _tamingSpeedPercent;

    [ObservableProperty]
    private int _weightReductionPercent;

    [ObservableProperty]
    private bool _unlimitedAmmo;

    [ObservableProperty]
    private bool _disableTemperatures;

    [ObservableProperty]
    private bool _removeLevelCap;

    private static readonly GameplayLevelToShortLabelConverter LevelLabelConverter = new();

    /// <summary>A compact one-line summary of which gameplay options are currently on — shown in the collapsed "Merge options" Expander header so folding that panel away doesn't hide active state.</summary>
    public string ActiveOptionsSummary
    {
        get
        {
            var parts = BuildActiveOptionParts();
            return parts.Count == 0 ? "No options active" : string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// Builds ActiveOptionsSummary's own display text. RebuildAsync's own "is anything active"
    /// guard deliberately does NOT read from this list anymore — it reads GameplayOptions.IsAnyActive
    /// instead (the same classification GenerateFixedFieldChanges/RequiredCurrentFiles conceptually
    /// mirror), since re-deriving "is anything on" here independently is exactly the failure mode a
    /// real bug this session traced back to: a guard checking only a subset of options let Rebuild
    /// silently no-op while Install still shipped a stale pak, with no error. This method still
    /// needs its own per-option list (for the human-readable summary text), but a future option only
    /// needs adding to GameplayOptions.HasCategory1Active/HasCategory2Active for the guard to see it.
    /// </summary>
    private List<string> BuildActiveOptionParts()
    {
        var parts = new List<string>();
        if (SpeedBoost != BoostLevel.Off) parts.Add($"Speed Boost {ShortLabel(SpeedBoost)}");
        if (PlayerBoost != BoostLevel.Off) parts.Add($"Player Boost {ShortLabel(PlayerBoost)}");
        if (XpBoost != XpBoostLevel.Off) parts.Add($"XP Boost {ShortLabel(XpBoost)}");
        if (CraftCost != CraftCostReduction.Off) parts.Add($"Craft Cost {ShortLabel(CraftCost)}");
        if (StacksMultiplierLevel != MultiplierLevel.Off) parts.Add($"Stacks {ShortLabel(StacksMultiplierLevel)}");
        if (SlotsMultiplierLevel != MultiplierLevel.Off) parts.Add($"Slots {ShortLabel(SlotsMultiplierLevel)}");
        if (SpeedCraftingPercent > 0) parts.Add($"Speed Crafting {SpeedCraftingPercent}%");
        if (TamingSpeedPercent > 0) parts.Add($"Faster Taming {TamingSpeedPercent}%");
        if (WeightReductionPercent >= 100) parts.Add("Remove Weight");
        else if (WeightReductionPercent > 0) parts.Add($"Lighter Items {WeightReductionPercent}%");
        if (UnlimitedAmmo) parts.Add("Unlimited Ammo");
        if (DisableTemperatures) parts.Add("Disable Temperatures");
        if (RemoveLevelCap) parts.Add("Remove Level Cap");
        return parts;
    }

    private static string ShortLabel(object level) =>
        (string)LevelLabelConverter.Convert(level, typeof(string), null, CultureInfo.InvariantCulture);

    // Speed/Player/XP Boost and Disable Temperatures (Category 1 — the ones that became a real
    // FieldChange in Phase 1 of the EXMOD/merge-options plan) also invalidate any existing manual
    // conflict picks and recompute the badge — toggling one of these changes whether the "Built-in
    // gameplay options" synthetic entry even exists, which can change what a stored pick's index
    // means the same way an Add/Remove/Move/Clear on Queue already does. Category 2 options
    // (Craft Cost, Stacks/Slots multiplier, ...) stay their own separate post-merge pass (unchanged
    // by that plan) and only need the summary refresh, not a conflict recompute.
    partial void OnSpeedBoostChanged(BoostLevel value) { OnPropertyChanged(nameof(ActiveOptionsSummary)); InvalidateManualPicks(); }
    partial void OnPlayerBoostChanged(BoostLevel value) { OnPropertyChanged(nameof(ActiveOptionsSummary)); InvalidateManualPicks(); }
    partial void OnXpBoostChanged(XpBoostLevel value) { OnPropertyChanged(nameof(ActiveOptionsSummary)); InvalidateManualPicks(); }
    partial void OnDisableTemperaturesChanged(bool value) { OnPropertyChanged(nameof(ActiveOptionsSummary)); InvalidateManualPicks(); }
    partial void OnRemoveLevelCapChanged(bool value) { OnPropertyChanged(nameof(ActiveOptionsSummary)); InvalidateManualPicks(); }
    partial void OnCraftCostChanged(CraftCostReduction value) => OnPropertyChanged(nameof(ActiveOptionsSummary));
    partial void OnStacksMultiplierLevelChanged(MultiplierLevel value) => OnPropertyChanged(nameof(ActiveOptionsSummary));
    partial void OnSlotsMultiplierLevelChanged(MultiplierLevel value) => OnPropertyChanged(nameof(ActiveOptionsSummary));
    partial void OnSpeedCraftingPercentChanged(int value) => OnPropertyChanged(nameof(ActiveOptionsSummary));
    partial void OnTamingSpeedPercentChanged(int value) => OnPropertyChanged(nameof(ActiveOptionsSummary));
    partial void OnWeightReductionPercentChanged(int value) => OnPropertyChanged(nameof(ActiveOptionsSummary));
    partial void OnUnlimitedAmmoChanged(bool value) => OnPropertyChanged(nameof(ActiveOptionsSummary));

    private void InvalidateManualPicks()
    {
        _manualPicks = null;
        ConflictStatusMessage = null;
        _ = RecomputeConflictCountAsync();
    }

    // --- Installed vs this list (6.6) ---
    /// <summary>"+ Name" (would be added), "- Name" (would be removed), "= Name" (unchanged) — a preview of what clicking Install would actually do, not run automatically since it needs a real read of the game folder.</summary>
    public ObservableCollection<string> ComparisonEntries { get; } = [];

    [ObservableProperty]
    private string? _comparisonStatusMessage;

    private readonly IMessenger _messenger;

    /// <summary>Null only in tests that don't care; the app always wires the real checker — see InstallAsync.</summary>
    private readonly IGameProcessChecker? _gameProcessChecker;

    /// <summary>Null only in tests that don't care; the app always wires it — see RebuildAsync's game-update guard.</summary>
    private readonly IGameDataFreshness? _gameDataFreshness;

    /// <summary>A Yes/No question (message, title) → Yes. Tests swap in an answer.</summary>
    internal Func<string, string, bool> Confirm { get; set; } =
        (message, title) => ThemedMessageBox.Show(message, title, ThemedConfirmSeverity.Question);

    public MergeInstallViewModel(
        ILibraryRepository libraryRepository, IRebuildService rebuildService, IInstallService installService, IProfileStore profileStore,
        IPatchService patchService, IDaedalusCatalogClient daedalusClient, IJimk72CatalogClient jimk72Client,
        ISettingsService settingsService, IPakCompareService pakCompareService, PerformanceTracker performanceTracker, IActivityLog activityLog,
        string dataFolder, string outputPakPath, string backupDirectory, IMessenger? messenger = null, IGameProcessChecker? gameProcessChecker = null,
        IGameDataFreshness? gameDataFreshness = null)
    {
        IcarusStarlink.Core.Memory.IdleMemory.Register(this);

        // Injectable so tests can give each view model its own messenger — the shared Default one
        // made this class's tests interfere with each other under the parallel runner.
        _messenger = messenger ?? WeakReferenceMessenger.Default;
        _gameProcessChecker = gameProcessChecker;
        _gameDataFreshness = gameDataFreshness;
        _libraryRepository = libraryRepository;
        _rebuildService = rebuildService;
        _installService = installService;
        _profileStore = profileStore;
        _patchService = patchService;
        _daedalusClient = daedalusClient;
        _jimk72Client = jimk72Client;
        _settingsService = settingsService;
        _pakCompareService = pakCompareService;
        _performanceTracker = performanceTracker;
        _activityLog = activityLog;
        _dataFolder = dataFolder;
        _outputPakPath = outputPakPath;
        _backupDirectory = backupDirectory;

        // Fires once a burst of queue mutations (multi-select add, a fast drag-reorder) settles,
        // rather than once per individual mutation — each of those otherwise re-reads/re-parses
        // every queued mod's own EXMOD from disk (mitigated separately by _packageCache above, but
        // even a cache lookup plus a conflict/validation recompute per mutation is wasted work when
        // three more mutations are about to land in the next few hundred milliseconds anyway).
        _recomputeDebounceTimer = new DebounceTimer(TimeSpan.FromMilliseconds(250), () =>
        {
            _ = RecomputeConflictCountAsync();
            _ = RecomputeValidationIssueCountAsync();
        });

        ReloadProfileNames();

        // A fresh launch starts with no profile selected in this session — auto-restoring
        // "Default" (when it exists) rather than always starting on an empty queue is what makes
        // RebuildAsync's own auto-save into "Default" actually round-trip across a restart, for a
        // brand-new user who's never touched Profiles at all just as much as for anyone else who
        // works ad hoc. A fresh install with nothing built yet correctly stays on an empty queue
        // until the first Rebuild creates "Default" for the first time.
        if (SelectedProfileName is null && settingsService.Current.LastMergeProfileName is { } last && ProfileNames.Contains(last, StringComparer.OrdinalIgnoreCase))
        {
            SelectedProfileName = ProfileNames.First(n => string.Equals(n, last, StringComparison.OrdinalIgnoreCase));
        }
        else if (SelectedProfileName is null && ProfileNames.Contains("Default", StringComparer.OrdinalIgnoreCase))
        {
            SelectedProfileName = "Default";
        }

        PrependBaselineMods();

        // Any queue change at all invalidates existing conflict picks — see _manualPicks' own
        // comment for why an Add/Remove/Move/Clear can silently change what a pick's index means.
        // The actual conflict/validation recompute is debounced (_recomputeDebounceTimer, above)
        // rather than kicked off directly here — a multi-select "Add to merge queue" or a fast
        // drag-reorder fires this handler many times in a row, and each one used to independently
        // relaunch the full recompute path with only a version counter to discard the stale result;
        // the wasted work still happened. The cheap, synchronous resets below still happen on every
        // single mutation, so status text never lingers stale even mid-burst.
        Queue.CollectionChanged += (_, _) =>
        {
            _manualPicks = null;
            ConflictStatusMessage = null;
            OnPropertyChanged(nameof(HasQueuedMods));
            OnPropertyChanged(nameof(IsQueueEmpty));

            // Validation findings only ever depend on which mods are queued (unlike conflicts,
            // which also depend on gameplay options), so this recomputes on Queue changes only —
            // not from every gameplay-option toggle the way InvalidateManualPicks does.
            ValidationStatusMessage = null;

            _recomputeDebounceTimer.Restart();
            MarkWorkingStateChanged();
        };

        // Without this, the Install/"Update install" label goes stale after the user changes the
        // Icarus Content path in Settings — the check below only otherwise runs at construction
        // and after this page's own install/remove actions.
        // The message parameter needs a real name: naming it "_" would make it shadow the discard,
        // so "_ = task" inside would try assigning the Task to the message parameter itself.
        _messenger.Register<SettingsSavedMessage>(this, (recipient, message) =>
        {
            var viewModel = (MergeInstallViewModel)recipient;
            _ = viewModel.RefreshHasExistingInstallAsync();
            // The Content path may have changed: the Library's "In game" and the ArcFission prompt follow it.
            _ = viewModel.RefreshInstalledModNamesAsync();
        });

        // A queued mod's own files can change on disk without any Queue mutation at all (most
        // commonly the EXMOD editor's own Save, which sends this same message) — _packageCache's
        // own fingerprint check already catches that correctly on the NEXT recompute regardless,
        // but clearing it here too means a rename/re-import/delete under a REUSED folder name can
        // never serve another mod's stale cached content, however unlikely that coincidence is.
        // Also invalidates any manual conflict pick: MergeEngine.Merge applies a pick as a plain
        // index into that field's candidate list, rebuilt fresh from each queued mod's CURRENT
        // content at Rebuild time — if a queued mod's own EXMOD content just changed (this message
        // firing IS that signal), the same in-range index can now mean a different value entirely,
        // and there is no way to detect that from the index alone. Same reasoning already applied
        // to Queue.CollectionChanged/InvalidateManualPicks above; this was the one other place a
        // pick's own meaning can silently change out from under it.
        _messenger.Register<LibraryChangedMessage>(this, (recipient, _) =>
        {
            var viewModel = (MergeInstallViewModel)recipient;
            viewModel.InvalidatePackageCache();
            viewModel.InvalidateManualPicks();
            // A profile's missing mod imported again under its folder name takes its row back.
            viewModel.ResolveMissingQueueEntries();
        });

        // _dataFolder's own contents only ever change via a real "Update data folder" run — the
        // same trigger GameDataIndexCache and LibraryViewModel already invalidate their own base-data
        // caches on. Also invalidates any manual conflict pick, for the same reason as
        // LibraryChangedMessage's own handler above: MergeEngine.Merge re-resolves each field's
        // candidate list (which now starts from the FRESH base data this message signals) at
        // Rebuild time, and a pick stored as a plain index has no way to detect that its own
        // meaning may have shifted underneath it.
        _messenger.Register<WeeklyChangeReportUpdatedMessage>(this, (recipient, _) =>
        {
            var viewModel = (MergeInstallViewModel)recipient;
            lock (viewModel._packageCacheLock)
            {
                viewModel._cachedBaseDataTableIndex = null;
                // A prebuilt pak's changes are its diff against the game data, which just changed.
                viewModel._prebuiltPakChangesCache.Clear();
            }

            viewModel.InvalidateManualPicks();
        });

        _ = RefreshHasExistingInstallAsync();

        // The CollectionChanged subscription above is attached AFTER SelectedProfileName = "Default"
        // (and PrependBaselineMods) already populated Queue via Clear()+Add() during construction —
        // by design, so a 30+-mod profile load doesn't fire dozens of redundant recomputes while
        // the queue is still being built. But that also means neither badge's own proactive
        // recompute ever ran for the initial load — confirmed live: a real saved profile with a mod
        // known to have real field-validity findings showed no badge at all right after launch,
        // only after some LATER Add/Remove. One explicit recompute here, once the queue has fully
        // settled, covers exactly that gap without reordering the subscription itself.
        _ = RecomputeConflictCountAsync();
        _ = RecomputeValidationIssueCountAsync();

        // Any merge option changing raises the options summary: that's the cue to save too.
        _workingStateSaveTimer = new DebounceTimer(TimeSpan.FromMilliseconds(800), SaveWorkingState);
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ActiveOptionsSummary))
            {
                MarkWorkingStateChanged();
            }
        };
        _workingStateReady = true;
    }

    private DebounceTimer? _workingStateSaveTimer;
    private bool _workingStateReady;
    private bool _loadingProfile;
    private string? _unsavedProfileName;

    /// <summary>
    /// The queue and options save themselves into the profile in use (or "Default" when none is
    /// picked yet) a moment after each change, so ticking mods, reordering and options survive a
    /// restart or an update without Save profile. Save profile still makes a backup to go back to.
    /// </summary>
    private void MarkWorkingStateChanged()
    {
        if (!_workingStateReady || _loadingProfile)
        {
            return;
        }

        _unsavedProfileName = SelectedProfileName ?? "Default";
        _workingStateSaveTimer?.Restart();
    }

    /// <summary>Writes any pending change now (before switching profile, and from the timer).</summary>
    internal void SaveWorkingState()
    {
        _workingStateSaveTimer?.Cancel();
        if (_unsavedProfileName is not { } name)
        {
            return;
        }

        _unsavedProfileName = null;
        try
        {
            _profileStore.SaveWithoutBackup(new Profile
            {
                Name = name,
                MergeQueueFolderNames = [.. Queue.Select(e => e.FolderName)],
                Options = BuildGameplayOptionsFromUi(),
                ConflictWinners = new(_winners),
            });

            if (SelectedProfileName is null)
            {
                // The first change ever: "Default" now exists, so show it as the profile in use.
                _loadingProfile = true;
                try
                {
                    ReloadProfileNames();
                    SelectedProfileName = name;
                }
                finally
                {
                    _loadingProfile = false;
                }
            }
        }
        catch (Exception)
        {
            // Best-effort: Save profile still works and says why if the folder can't be written.
        }
    }

    /// <summary>The queue, options and conflict winners as they are now, as the profile in use (or "Default") — what Update everything's snapshot keeps.</summary>
    internal Profile CurrentWorkingProfile() => new()
    {
        Name = SelectedProfileName ?? "Default",
        MergeQueueFolderNames = [.. Queue.Select(e => e.FolderName)],
        Options = BuildGameplayOptionsFromUi(),
        ConflictWinners = new(_winners),
    };

    /// <summary>
    /// Roll back after a game update: the profile as the snapshot saved it (queue order, options,
    /// conflict winners) is saved again (its version from now backed up first, as Save profile does)
    /// and loaded. False while a build runs.
    /// </summary>
    internal bool RestoreWorkingState(Profile profile)
    {
        if (RefuseWhileRebuilding())
        {
            return false;
        }

        // Anything changed but not yet saved goes into the profile in use before it's replaced.
        SaveWorkingState();
        _profileStore.Save(profile);
        if (string.Equals(SelectedProfileName, profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            _loadingProfile = true;
            try
            {
                LoadProfileIntoUi(profile.Name, verb: "Rolled back");
            }
            finally
            {
                _loadingProfile = false;
            }
        }
        else
        {
            ReloadProfileNames();
            SelectedProfileName = ProfileNames.FirstOrDefault(n => string.Equals(n, profile.Name, StringComparison.OrdinalIgnoreCase)) ?? profile.Name;
        }

        _activityLog.Log($"Put profile '{profile.Name}' back as it was before the game update run.", ActivityEntryKind.Success);
        return true;
    }

    /// <summary>A ticked mod that's in the Library, or a merge option: something for a build to make.</summary>
    internal bool HasAnythingToBuild => Queue.Any(IsBuildable) || BuildGameplayOptionsFromUi().IsAnyActive;

    /// <summary>
    /// The mods the installed merged pak was built from, ticked again in the order they were merged
    /// — for when the queue was lost (an older version that didn't save it, a profile switch) while
    /// the game still has them. Returns how many were ticked.
    /// </summary>
    public int RestoreQueueFromGame()
    {
        if (RefuseWhileRebuilding())
        {
            return 0;
        }

        var library = _libraryRepository.GetAll();
        var restored = new List<LibraryEntry>();
        var opaqueByPak = library.Where(e => e.IsOpaquePak && e.PakFileNames.Count > 0 && e.MergedPackModNames is null).ToList();
        foreach (var name in LastInstalledModNames)
        {
            var entry = library.FirstOrDefault(e => !e.IsOpaquePak && (string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase) || string.Equals(e.FolderName, name, StringComparison.OrdinalIgnoreCase)))
                ?? opaqueByPak.FirstOrDefault(e => e.PakFileNames.Any(p => string.Equals(Path.GetFileNameWithoutExtension(p), name, StringComparison.OrdinalIgnoreCase)));
            if (entry is not null && !restored.Contains(entry))
            {
                restored.Add(entry);
            }
        }

        if (restored.Count == 0)
        {
            return 0;
        }

        foreach (var entry in restored.Where(e => !Queue.Any(q => string.Equals(q.FolderName, e.FolderName, StringComparison.OrdinalIgnoreCase))))
        {
            Queue.Add(entry);
        }

        StatusMessage = $"Ticked the {restored.Count} mod(s) that are in the game, in the order they were merged.";
        _activityLog.Log(StatusMessage, ActivityEntryKind.Success);
        return restored.Count;
    }

    /// <summary>
    /// Checks for the real installed pak's own file directly, not GetInstalledStateAsync's parsed
    /// mod-name list — a gameplay-options-only install (an empty queue, per the spec's own "add
    /// merge options to game with no mods selected") still produces a real pak but lists zero mod
    /// names in the manifest, which would make a Count-based check wrongly say nothing's installed.
    /// </summary>
    internal Task RefreshHasExistingInstallAsync() => Task.Run(() =>
    {
        // A staged build on disk is all Copy to game needs — independent of whether anything is
        // currently installed in the game folder.
        CanCopyToGame = HasCompleteStagedBuild();

        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            HasExistingInstall = false;
            return;
        }

        try
        {
            // The merged pak under an earlier name (left by an older version) is an install too:
            // it still loads, and Install/Remove clear it.
            var modsDirectory = Path.Combine(_settingsService.Current.IcarusContentPath!, "Paks", "mods");
            HasExistingInstall = InstallManifestNames.AllMergedPakFileNames
                .Prepend(Path.GetFileName(_outputPakPath))
                .Any(name => File.Exists(Path.Combine(modsDirectory, name)));
        }
        catch (Exception)
        {
            // Best-effort — the button label just stays whatever it already was.
        }
    });

    /// <summary>
    /// The staged pak AND its mod list — RebuildService writes the list only after a verified build,
    /// so a pak without one is a leftover of a failed build, not something to install (and its
    /// install would leave the Library's "In game" reading the previous build's list).
    /// </summary>
    private bool HasCompleteStagedBuild() =>
        File.Exists(_outputPakPath)
        && File.Exists(Path.Combine(Path.GetDirectoryName(_outputPakPath)!, InstallManifestNames.PakManifest));

    private void ReloadProfileNames()
    {
        ProfileNames.Clear();
        foreach (var name in _profileStore.ProfileNames)
        {
            ProfileNames.Add(name);
        }
    }

    private GameplayOptions BuildGameplayOptionsFromUi() => new()
    {
        SpeedBoost = SpeedBoost,
        PlayerBoost = PlayerBoost,
        XpBoost = XpBoost,
        CraftCost = CraftCost,
        StacksMultiplier = StacksMultiplierLevel.ToMultiplier(),
        SlotsMultiplier = SlotsMultiplierLevel.ToMultiplier(),
        SpeedCraftingReductionPercent = SpeedCraftingPercent > 0 ? SpeedCraftingPercent : null,
        TamingSpeedReductionPercent = TamingSpeedPercent > 0 ? TamingSpeedPercent : null,
        RemoveWeight = WeightReductionPercent >= 100,
        WeightReductionPercent = WeightReductionPercent is > 0 and < 100 ? WeightReductionPercent : null,
        UnlimitedAmmo = UnlimitedAmmo,
        DisableTemperatures = DisableTemperatures,
        RemoveLevelCap = RemoveLevelCap,
    };

    private void LoadGameplayOptionsIntoUi(GameplayOptions options)
    {
        SpeedBoost = options.SpeedBoost;
        PlayerBoost = options.PlayerBoost;
        XpBoost = options.XpBoost;
        CraftCost = options.CraftCost;
        StacksMultiplierLevel = MultiplierLevelExtensions.FromMultiplier(options.StacksMultiplier);
        SlotsMultiplierLevel = MultiplierLevelExtensions.FromMultiplier(options.SlotsMultiplier);
        SpeedCraftingPercent = options.SpeedCraftingReductionPercent is { } percent ? (int)Math.Round(percent) : 0;
        TamingSpeedPercent = options.TamingSpeedReductionPercent is { } tamingPercent ? (int)Math.Round(tamingPercent) : 0;
        // Older profiles saved Remove Weight as on/off — that's 100% on the slider.
        WeightReductionPercent = options.RemoveWeight ? 100 : options.WeightReductionPercent is { } lighter ? (int)Math.Round(Math.Clamp(lighter, 0, 100)) : 0;
        UnlimitedAmmo = options.UnlimitedAmmo;
        DisableTemperatures = options.DisableTemperatures;
        RemoveLevelCap = options.RemoveLevelCap;
    }

    /// <summary>Selecting a profile replaces the current queue with that profile's own saved one — a profile *is* "a saved merge list" per the spec, not something merged alongside the current queue.</summary>
    partial void OnSelectedProfileNameChanging(string? value)
    {
        // Whatever was changed in the profile being left goes into that profile, not the next.
        if (!_loadingProfile)
        {
            SaveWorkingState();
        }
    }

    partial void OnSelectedProfileNameChanged(string? value)
    {
        OnPropertyChanged(nameof(CanRestoreProfileBackup));
        if (value is null || _loadingProfile)
        {
            return;
        }

        try
        {
            _settingsService.Current.LastMergeProfileName = value;
            _settingsService.Save();
        }
        catch (Exception)
        {
            // Only a convenience for the next start.
        }

        _loadingProfile = true;
        try
        {
            LoadProfileIntoUi(value, verb: "Loaded");
        }
        finally
        {
            _loadingProfile = false;
        }
    }

    /// <summary>
    /// Reads a profile's own file fresh from disk and populates Queue/options from it — shared by
    /// the ComboBox selection handler above and RestoreProfileBackup below, which needs the exact
    /// same "re-read what's now on disk" step after replacing the file with a backup.
    /// </summary>
    private void LoadProfileIntoUi(string name, string verb)
    {
        try
        {
            var profile = _profileStore.Load(name);
            LoadGameplayOptionsIntoUi(profile.Options);
            var (winners, prunedWinners) = MigrateWinners(profile.ConflictWinners ?? []);
            _winners = winners;
            OnPropertyChanged(nameof(SavedWinnerCount));

            Queue.Clear();
            var missingCount = 0;
            var islPacks = new List<string>();
            var byFolder = LibraryByFolder();
            foreach (var folderName in profile.MergeQueueFolderNames)
            {
                if (!byFolder.TryGetValue(folderName, out var entry))
                {
                    // Kept as a greyed row, so the profile keeps the name (the next auto-save writes
                    // it back) and the mod takes its place again once it's back in the Library.
                    Queue.Add(MissingEntry(folderName));
                    missingCount++;
                }
                else if (IsIslBuiltPack(entry))
                {
                    islPacks.Add(entry.Name);
                }
                else
                {
                    Queue.Add(entry);
                }
            }

            PrependBaselineMods();

            var notes = new List<string>();
            if (missingCount > 0)
            {
                notes.Add($"{missingCount} mod(s) from this profile are no longer in your Library (shown greyed, left out of the build until they're back)");
            }

            if (islPacks.Count > 0)
            {
                notes.Add($"left out {string.Join(", ", islPacks)}: a pack Merge & Install built, which can't be merged into a new one");
            }

            if (prunedWinners > 0)
            {
                notes.Add($"{prunedWinners} saved conflict pick(s) for mods no longer in your Library were removed");
            }

            ProfileStatusMessage = notes.Count > 0 ? $"{verb} '{name}' — {string.Join("; ", notes)}." : $"{verb} '{name}'.";
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else: a corrupt/unreadable profile file shows a
            // status message rather than crashing the app out of a ComboBox selection change.
            ProfileStatusMessage = $"Couldn't load profile: {ex.Message}";
        }
    }

    private Dictionary<string, LibraryEntry> LibraryByFolder() =>
        _libraryRepository.GetAll()
            .GroupBy(e => e.FolderName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The greyed queue row for a profile's mod that isn't in the Library.</summary>
    private static LibraryEntry MissingEntry(string folderName) => new()
    {
        FolderName = folderName,
        Name = folderName,
        Author = "",
        Version = "",
        Description = "",
        FileName = folderName,
        IsMissingFromLibrary = true,
    };

    /// <summary>
    /// A pack Merge &amp; Install itself built (its Library entry came with an ISL-Merged.txt, or it
    /// has the merged pak's name). It can't go into the queue: it would fold the previous merge into
    /// the new one, and apply the merge options (Stacks x2…) a second time on top of the first.
    /// </summary>
    internal bool IsIslBuiltPack(LibraryEntry entry) =>
        entry.MergedPackModNames is not null
        || entry.MergedPackOptionDescriptions is not null
        || InstallManifestNames.AllMergedPakFileNames
            .Prepend(Path.GetFileName(_outputPakPath))
            .Any(n => string.Equals(entry.FolderName, Path.GetFileNameWithoutExtension(n), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Saved winners moved to ids (see _winners): a winner already an id is kept, one saved by display
    /// name becomes the folder name of the one Library mod (or prebuilt pak) with that name. A name
    /// several mods share is kept as it is (it still works by name while that's unambiguous in the
    /// queue); a winner that matches nothing in the Library any more is dropped and counted. With an
    /// empty Library (not loaded yet) nothing is dropped.
    /// </summary>
    private (Dictionary<string, string> Winners, int Pruned) MigrateWinners(IReadOnlyDictionary<string, string> saved)
    {
        var library = _libraryRepository.GetAll();
        var migrated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var pruned = 0;
        foreach (var (key, winner) in saved)
        {
            if (library.Count == 0 || winner is OptionsWinnerId or ArcFissionWinnerId)
            {
                migrated[key] = winner;
                continue;
            }

            if (InstallManifestNames.IsArcFissionDataPak(winner + ".pak"))
            {
                migrated[key] = ArcFissionWinnerId;
                continue;
            }

            var slash = winner.IndexOf('/');
            if (library.Any(e => !e.IsOpaquePak && string.Equals(e.FolderName, winner, StringComparison.OrdinalIgnoreCase))
                || (slash > 0 && library.Any(e => e.IsOpaquePak && string.Equals(e.FolderName, winner[..slash], StringComparison.OrdinalIgnoreCase))))
            {
                migrated[key] = winner;
                continue;
            }

            var byName = library.Where(e => !e.IsOpaquePak && string.Equals(e.Name, winner, StringComparison.Ordinal)).ToList();
            var pakOwners = library.Where(e => e.IsOpaquePak
                && e.PakFileNames.Any(p => string.Equals(Path.GetFileNameWithoutExtension(p), winner, StringComparison.OrdinalIgnoreCase))).ToList();
            if (byName.Count == 1 && pakOwners.Count == 0)
            {
                migrated[key] = byName[0].FolderName;
            }
            else if (pakOwners.Count == 1 && byName.Count == 0)
            {
                migrated[key] = $"{pakOwners[0].FolderName}/{winner}";
            }
            else if (byName.Count + pakOwners.Count > 1)
            {
                migrated[key] = winner;
            }
            else
            {
                pruned++;
            }
        }

        return (migrated, pruned);
    }

    /// <summary>Replaces each greyed missing row whose mod is in the Library again with the real entry, in place.</summary>
    private void ResolveMissingQueueEntries()
    {
        if (!Queue.Any(e => e.IsMissingFromLibrary))
        {
            return;
        }

        var byFolder = LibraryByFolder();
        for (var i = 0; i < Queue.Count; i++)
        {
            if (Queue[i].IsMissingFromLibrary && byFolder.TryGetValue(Queue[i].FolderName, out var entry) && !IsIslBuiltPack(entry))
            {
                Queue[i] = entry;
            }
        }
    }

    /// <summary>Asks for a profile name (title, starting text) → the name, or null when cancelled. Tests swap in an answer.</summary>
    internal Func<string, string, string?> PromptForProfileName { get; set; } = (title, current) =>
    {
        var dialog = new Views.RenameModDialog(current, "A name for this saved set of queued mods and merge options.", title: title, fieldLabel: "Profile name", showReset: false)
        {
            Owner = Application.Current?.MainWindow,
        };
        return dialog.ShowDialog() == true ? dialog.NewDisplayName : null;
    };

    /// <summary>Captures whatever's currently in the queue under a new profile name — lets a user build a queue first, then decide to save it, rather than New always starting empty.</summary>
    [RelayCommand]
    private void NewProfile()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        // Asked every time: a name kept from a refused try (it already existed) used to skip the
        // question on every later click, so New profile failed the same way until a restart.
        var name = PromptForProfileName("New profile", "")?.Trim();
        if (name is null)
        {
            return;
        }

        if (name.Length == 0)
        {
            ProfileStatusMessage = "Give the profile a name.";
            return;
        }

        if (ProfileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            ProfileStatusMessage = $"A profile named '{name}' already exists.";
            return;
        }

        try
        {
            _profileStore.Save(new Profile
            {
                Name = name,
                MergeQueueFolderNames = [.. Queue.Select(e => e.FolderName)],
                Options = BuildGameplayOptionsFromUi(),
                ConflictWinners = new(_winners),
            });
            ReloadProfileNames();
            SelectedProfileName = name;
            ProfileStatusMessage = $"Created '{name}'.";
            _activityLog.Log($"Created profile '{name}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't create profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        if (SelectedProfileName is not { } name)
        {
            ProfileStatusMessage = "Select or create a profile first.";
            return;
        }

        try
        {
            _profileStore.Save(new Profile
            {
                Name = name,
                MergeQueueFolderNames = [.. Queue.Select(e => e.FolderName)],
                Options = BuildGameplayOptionsFromUi(),
                ConflictWinners = new(_winners),
            });
            ProfileStatusMessage = $"Saved '{name}'.";
            _activityLog.Log($"Saved profile '{name}'.", ActivityEntryKind.Success);
            OnPropertyChanged(nameof(CanRestoreProfileBackup));
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't save profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RenameProfile()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        if (SelectedProfileName is not { } oldName)
        {
            ProfileStatusMessage = "Select a profile first.";
            return;
        }

        if (PromptForProfileName("Rename profile", oldName)?.Trim() is not { } newName)
        {
            return;
        }

        if (newName.Length == 0 || string.Equals(newName, oldName, StringComparison.Ordinal))
        {
            ProfileStatusMessage = "Give the profile a new name.";
            return;
        }

        if (ProfileNames.Contains(newName, StringComparer.OrdinalIgnoreCase))
        {
            ProfileStatusMessage = $"A profile named '{newName}' already exists.";
            return;
        }

        try
        {
            // A pending auto-save would otherwise write the old name back as a second profile.
            SaveWorkingState();
            _profileStore.Rename(oldName, newName);
            ReloadProfileNames();
            // Backing field, not the property: the property setter's change hook reloads the profile
            // from disk into the UI, which threw away any queue/option edits not yet saved (an audit
            // finding) — a rename changes only the name.
            _selectedProfileName = newName;
            OnPropertyChanged(nameof(SelectedProfileName));
            OnPropertyChanged(nameof(CanRestoreProfileBackup));
            // The change hook is skipped above, so the next start's profile is remembered here.
            try
            {
                _settingsService.Current.LastMergeProfileName = newName;
                _settingsService.Save();
            }
            catch (Exception)
            {
                // Only a convenience for the next start.
            }

            ProfileStatusMessage = $"Renamed '{oldName}' to '{newName}'.";
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't rename profile: {ex.Message}";
        }
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        if (SelectedProfileName is not { } name)
        {
            ProfileStatusMessage = "Select a profile first.";
            return;
        }

        try
        {
            // A pending auto-save of this profile must not write it back once it's deleted (or, with
            // no profile selected, into "Default").
            _workingStateSaveTimer?.Cancel();
            _unsavedProfileName = null;

            _profileStore.Delete(name);
            ReloadProfileNames();

            if (!string.Equals(name, "Default", StringComparison.OrdinalIgnoreCase)
                && ProfileNames.FirstOrDefault(n => string.Equals(n, "Default", StringComparison.OrdinalIgnoreCase)) is { } defaultName)
            {
                // Loads "Default" as saved, replacing the deleted profile's queue and options.
                SelectedProfileName = defaultName;
            }
            else
            {
                // Nothing to fall back to: start empty, without saving the deleted profile's queue,
                // options or picks anywhere (the next change saves into a fresh "Default").
                _loadingProfile = true;
                try
                {
                    SelectedProfileName = null;
                    LoadGameplayOptionsIntoUi(new GameplayOptions());
                    _winners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    OnPropertyChanged(nameof(SavedWinnerCount));
                    Queue.Clear();
                    PrependBaselineMods();
                }
                finally
                {
                    _loadingProfile = false;
                }

                _workingStateSaveTimer?.Cancel();
                _unsavedProfileName = null;
            }

            ProfileStatusMessage = $"Deleted '{name}'.";
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't delete profile: {ex.Message}";
        }
    }

    /// <summary>Whether the currently-selected profile has a backup to restore — drives the Restore backup button's own IsEnabled, mirroring how Library's "Restore latest backup" only enables once HasModBackup is true.</summary>
    public bool CanRestoreProfileBackup => SelectedProfileName is { } name && _profileStore.HasBackup(name);

    /// <summary>
    /// Replaces the selected profile's file with its own most recent backup — the mirror of
    /// Library's "Restore latest backup" for a Profile, since ProfileStore.Save already backs up
    /// whatever was there before overwriting it (a bad edit, an accidental Clear + Save, or
    /// MergeInstallViewModel's own auto-save of an unnamed queue into "Default" right before every
    /// Rebuild — see RebuildAsync's own comment on that). Re-loads the queue/options from the
    /// restored content immediately, the same way selecting the profile fresh would.
    /// </summary>
    [RelayCommand]
    private void RestoreProfileBackup()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        if (SelectedProfileName is not { } name)
        {
            ProfileStatusMessage = "Select a profile first.";
            return;
        }

        try
        {
            if (!_profileStore.RestoreLatestBackup(name))
            {
                ProfileStatusMessage = $"'{name}' has no backup to restore.";
                return;
            }

            LoadProfileIntoUi(name, verb: "Restored");
            _activityLog.Log($"Restored profile '{name}' from its own latest backup.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            ProfileStatusMessage = $"Couldn't restore profile: {ex.Message}";
        }
    }

    /// <summary>
    /// "Bundles mods with no download URL, and mods you edited locally" per the spec — this app
    /// has no way to tell a locally-edited mod apart from a downloaded one yet (that needs the
    /// EXMOD editor, Phase 7), so the proxy used here is "not found in the community catalog by
    /// (Name, Author)": a friend can get anything catalog-listed from Downloads themselves, so
    /// only what they genuinely couldn't get elsewhere needs to travel inside the patch file
    /// itself. Exports whatever's currently in Queue (not a fresh reload of the saved profile),
    /// matching SaveProfile's own live-state semantics.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ExportPatchAsync()
    {
        if (IsExportingPatch)
        {
            return;
        }

        if (SelectedProfileName is not { } profileName)
        {
            PatchStatusMessage = "Select or save a profile first.";
            return;
        }

        if (Queue.Count == 0)
        {
            PatchStatusMessage = "Add at least one mod to the queue first.";
            return;
        }

        IsExportingPatch = true;
        PatchStatusMessage = "Checking which mods are in the community catalog…";

        try
        {
            // Isolated per-source, same as Downloads' own catalog refresh — a transient blip in
            // either source shouldn't fail the whole export; a mod from the source that failed
            // just gets treated as "not in catalog" (bundled) instead.
            var failedSources = new List<string>();
            var daedalusTask = CatalogSourceFetch.FetchAsync(_daedalusClient.FetchAsync, "Daedalus", failedSources);
            var jimk72Task = CatalogSourceFetch.FetchAsync(_jimk72Client.FetchAsync, "Jimk72", failedSources);
            await Task.WhenAll(daedalusTask, jimk72Task);
            var catalogKeys = daedalusTask.Result.Concat(jimk72Task.Result)
                .Select(e => CatalogKey.Normalize(e.Name, e.Author))
                .ToHashSet();

            var mods = new List<PatchModEntry>();
            var bundledContents = new Dictionary<string, ExmodPackageContents>();
            // A prebuilt/opaque .pak has no .EXMOD to bundle — ExmodFolder.Read on its folder throws,
            // which used to fail the whole export (an audit finding). Listed in the status instead.
            var skippedPaks = Queue.Where(e => e.IsOpaquePak).Select(e => e.Name).ToList();
            foreach (var entry in Queue)
            {
                if (entry.IsOpaquePak || entry.IsMissingFromLibrary)
                {
                    continue;
                }

                var isInCatalog = catalogKeys.Contains(CatalogKey.Normalize(entry.Name, entry.Author));
                mods.Add(new PatchModEntry
                {
                    FolderName = entry.FolderName, Name = entry.Name, Author = entry.Author, Version = entry.Version,
                    Bundled = !isInCatalog,
                });
                if (!isInCatalog)
                {
                    bundledContents[entry.FolderName] = ExmodFolder.Read(_libraryRepository.GetFolderPath(entry.FolderName));
                }
            }

            if (mods.Count == 0)
            {
                PatchStatusMessage = "Nothing to export — every queued mod is a prebuilt .pak, and a patch can only carry EXMOD mods.";
                return;
            }

            var extension = bundledContents.Count == 0 ? "json" : "zip";
            var dialog = new SaveFileDialog
            {
                Title = "Export patch",
                FileName = $"ISL-Patch-{profileName}.{extension}",
                Filter = extension == "json" ? "IcarusStarlink patch (*.json)|*.json" : "IcarusStarlink patch (*.zip)|*.zip",
            };
            // ShowDialog() blocks until the user responds, so the "Checking…" message above would
            // otherwise sit on screen — stale-looking — for as long as they take to pick a location.
            PatchStatusMessage = "Choose where to save…";
            if (dialog.ShowDialog() != true)
            {
                PatchStatusMessage = null;
                return;
            }

            var manifest = new PatchManifest { ProfileName = profileName, Mods = mods, Options = BuildGameplayOptionsFromUi() };
            await _patchService.ExportAsync(manifest, bundledContents, dialog.FileName);

            PatchStatusMessage = bundledContents.Count > 0
                ? $"Exported '{profileName}' to '{dialog.FileName}' — {bundledContents.Count} mod(s) bundled, " +
                    $"{mods.Count - bundledContents.Count} referenced from the community catalog."
                : $"Exported '{profileName}' to '{dialog.FileName}' — every mod is in the community catalog, nothing bundled.";
            if (skippedPaks.Count > 0)
            {
                PatchStatusMessage += $" Not included: {skippedPaks.Count} prebuilt .pak mod(s) ({string.Join(", ", skippedPaks)}) — a patch can only carry EXMOD mods.";
            }

            if (failedSources.Count > 0)
            {
                // Not a failure — a mod from the unreachable source was conservatively treated as
                // "not in catalog" and bundled instead, so the export is still complete and correct,
                // just possibly larger than it needed to be.
                PatchStatusMessage += $" ({string.Join(", ", failedSources)} couldn't be reached — affected mods were bundled rather than referenced.)";
            }

            _activityLog.Log($"Exported '{profileName}' to '{dialog.FileName}'.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            PatchStatusMessage = $"Export failed: {ex.Message}";
            _activityLog.Log(PatchStatusMessage, ActivityEntryKind.Warning);
        }
        finally
        {
            IsExportingPatch = false;
        }
    }

    /// <summary>
    /// Loads a classic-IMM merge list (LastMergedMods.txt / IMM_Merged_Mod.txt — or this app's own
    /// ISL-Merged.txt, same format) and rebuilds the queue from it, in the list's own order (merge
    /// priority). REPLACES the current queue, matching classic IMM's own "Load Merge list"
    /// semantics — the file IS the intended list, not an addition to whatever happened to be
    /// queued. Names that don't match any Library entry land in the Warnings list rather than
    /// being silently dropped, so the user can resolve them (import the missing mod, or Rename/
    /// Link an existing one) and re-import.
    ///
    /// Public and called only by Settings' own classic-IMM migration (which finishes the job it
    /// starts: bringing the mods over is only half a migration — the user's actual merge list, in
    /// its real order, is the other half) — this page no longer has its own separate file-dialog
    /// entry point for it (removed as a redundant-looking near-duplicate of the Settings flow).
    /// </summary>
    public void LoadModListFromFile(string modListPath)
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        try
        {
            var names = ModListText.ParseNames(File.ReadAllText(modListPath));
            if (names.Count == 0)
            {
                StatusMessage = "That file has no mod names in it.";
                return;
            }

            var result = ModListMatcher.Match(names, _libraryRepository.GetAll());

            Queue.Clear();
            foreach (var entry in result.Matched.Where(e => !IsIslBuiltPack(e)))
            {
                Queue.Add(entry);
            }

            Warnings.Clear();
            foreach (var name in result.Unmatched)
            {
                Warnings.Add($"No Library match for '{name}' — import it first, then re-import this list.");
            }

            foreach (var pack in result.Matched.Where(IsIslBuiltPack))
            {
                Warnings.Add($"'{pack.Name}' is a pack Merge & Install built, so it wasn't ticked — tick the mods it was built from instead.");
            }

            StatusMessage = result.Unmatched.Count == 0
                ? $"Queued all {result.Matched.Count} mod(s) from the list."
                : $"Queued {result.Matched.Count} of {names.Count} — {result.Unmatched.Count} not in your Library yet (see warnings below).";
            _activityLog.Log($"Imported IMM mod list — {result.Matched.Count} of {names.Count} matched.",
                result.Unmatched.Count == 0 ? ActivityEntryKind.Success : ActivityEntryKind.Warning);
        }
        catch (Exception ex)
        {
            // Same UI boundary as ImportPatchAsync below — an unreadable/locked file shows a
            // status message rather than crashing the app.
            StatusMessage = $"Couldn't import the list: {ex.Message}";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// "Import + Sync reinstalls listed mods (force refresh; does not skip same version)" per the
    /// spec — a bundled mod always overwrites whatever's already in the Library under the same
    /// folder name, rather than skipping it as "already have it", since the whole point is syncing
    /// exactly what the patch carries. A referenced (non-bundled) mod is matched by (Name, Author)
    /// against the local Library; if it's missing, this reports it rather than failing the whole
    /// import — same SkipWithWarning philosophy as everywhere else the merge pipeline can hit a
    /// gap. ImportAsync itself doesn't need the network (unlike Export), but this still awaits it
    /// properly rather than blocking the UI thread on a zip read/extract that can take real time
    /// for a patch with bundled EXMODZ content.
    /// </summary>
    /// <summary>Asks for the patch file to import → its path, or null when cancelled. Tests swap in an answer.</summary>
    internal Func<string?> PickPatchFile { get; set; } = () =>
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a patch",
            Filter = "IcarusStarlink patch (*.json;*.zip)|*.json;*.zip",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    };

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ImportPatchAsync()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        if (PickPatchFile() is not { } patchPath)
        {
            return;
        }

        try
        {
            var contents = await _patchService.ImportAsync(patchPath);
            var resolvedFolderNames = new List<string>();
            var missing = new List<string>();

            foreach (var mod in contents.Manifest.Mods)
            {
                if (mod.Bundled)
                {
                    resolvedFolderNames.Add(ImportBundledMod(contents.BundledMods[mod.FolderName]));
                    continue;
                }

                var modKey = CatalogKey.Normalize(mod.Name, mod.Author);
                var existing = _libraryRepository.GetAll().FirstOrDefault(e => CatalogKey.Normalize(e.Name, e.Author) == modKey);
                if (existing is not null)
                {
                    resolvedFolderNames.Add(existing.FolderName);
                }
                else
                {
                    missing.Add($"{mod.Name} by {mod.Author}");
                }
            }

            // A pending auto-save of the profile in use is written first: written after the import,
            // it put the old queue back over a patch imported under the same profile name.
            SaveWorkingState();
            _profileStore.Save(new Profile { Name = contents.Manifest.ProfileName, MergeQueueFolderNames = resolvedFolderNames, Options = contents.Manifest.Options });
            _messenger.Send(new LibraryChangedMessage());
            var importedName = contents.Manifest.ProfileName;
            if (string.Equals(SelectedProfileName, importedName, StringComparison.OrdinalIgnoreCase))
            {
                // Already selected, so setting it again changes nothing: load it here, or the page
                // kept showing (and the next auto-save wrote back) the queue from before the import.
                _loadingProfile = true;
                try
                {
                    LoadProfileIntoUi(SelectedProfileName!, verb: "Imported");
                }
                finally
                {
                    _loadingProfile = false;
                }
            }
            else
            {
                ReloadProfileNames();
                // Triggers OnSelectedProfileNameChanged, which populates Queue from the just-saved
                // profile and reports its own "N mod(s) no longer in your Library" count — reused as-is
                // rather than duplicating that population logic here.
                SelectedProfileName = ProfileNames.FirstOrDefault(n => string.Equals(n, importedName, StringComparison.OrdinalIgnoreCase)) ?? importedName;
            }

            PatchStatusMessage = missing.Count > 0
                ? $"Imported '{contents.Manifest.ProfileName}' — {missing.Count} mod(s) missing, get them from Library's IMM Database tab first: {string.Join(", ", missing)}."
                : $"Imported '{contents.Manifest.ProfileName}'.";
            _activityLog.Log(PatchStatusMessage, missing.Count > 0 ? ActivityEntryKind.Warning : ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            PatchStatusMessage = $"Import failed: {ex.Message}";
            _activityLog.Log(PatchStatusMessage, ActivityEntryKind.Warning);
        }
    }

    private string ImportBundledMod(ExmodPackageContents contents)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.EXMODZ");
        try
        {
            ExmodzArchive.Write(tempPath, contents);

            var existing = _libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, contents.Package.FileName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                _libraryRepository.Delete(existing.FolderName);
            }

            return _libraryRepository.Import(tempPath).FolderName;
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// The "standard/minimum" baseline list — classic IMM's own "create a standard list and then
    /// always merge that list with your current one" (ver 2.4.9). Saves the CURRENT queue as the
    /// baseline; an empty queue clears it. Baseline mods are then prepended (lowest merge
    /// priority, so a profile's own mods win conflicts) into every future queue load — visibly,
    /// as real queue rows, never as an invisible union at rebuild time.
    /// </summary>
    [RelayCommand]
    private void SetBaseline()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        _settingsService.Current.BaselineModFolderNames = [.. Queue.Select(e => e.FolderName)];
        _settingsService.Save();
        StatusMessage = Queue.Count == 0
            ? "Baseline cleared — no mods are auto-added anymore."
            : $"Baseline set — these {Queue.Count} mod(s) will be auto-added to every profile's queue.";
        _activityLog.Log(Queue.Count == 0 ? "Cleared the baseline mod list." : $"Set baseline mod list ({Queue.Count} mod(s)).", ActivityEntryKind.Info);
    }

    /// <summary>
    /// Reorders the queue's real EXMOD mods so the ones making fewer genuine edits (compared to
    /// current base game data) sit later — later wins conflicts by default, so a small, deliberate
    /// field change no longer loses to a big overhaul-style mod that happens to also touch the
    /// same field, often as a stale whole-item-copy artifact rather than a deliberate one. See
    /// MergeEngine.CountChangesDifferingFromBase's own doc comment for why raw field count alone
    /// isn't a safe enough signal on its own — a mod's declared field count can be inflated by
    /// unchanged copied fields an old-style extractor carried along.
    ///
    /// Opaque paks keep their own exact queue position — there's no per-field diff to rank them by
    /// without a real UnrealPak extraction — only the EXMOD entries around them get reordered into
    /// the slots they already occupied. This only ever suggests; nothing is applied without an
    /// explicit confirmation naming the exact new order.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task SuggestQueueOrderAsync()
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        // A synchronous, no-I/O count (IsOpaquePak is a plain LibraryEntry field) rather than
        // LoadQueuedPackagesAsync's own real disk read — that happens exactly once, inside
        // ComputeSuggestedQueueOrderAsync below, not a second time redundantly just for this gate.
        if (Queue.Count(e => !e.IsOpaquePak && !e.IsMissingFromLibrary) < 2)
        {
            StatusMessage = "Nothing to reorder — need at least 2 real (non-prebuilt-pak) mods in the queue.";
            return;
        }

        var (newOrder, changed) = await ComputeSuggestedQueueOrderAsync();

        if (!changed)
        {
            StatusMessage = "The queue is already in a reasonable order — no change suggested.";
            return;
        }

        // The queue saves itself into the profile in use anyway (MarkWorkingStateChanged); with a
        // profile selected, the explicit "Apply this order? Yes" also makes a Save profile, so the
        // order before it is kept as that profile's backup.
        var saveNote = SelectedProfileName is { } selectedProfileName
            ? $" and save it to '{selectedProfileName}'"
            : "";
        var preview = string.Join("\n", newOrder.Select((e, i) => $"{i + 1}. {e.Name}"));
        var confirmed = Confirm(
            "Suggests putting mods that make fewer real edits (compared to current game data) later in the queue, " +
            "so a small, deliberate change doesn't lose to a bigger mod that happens to also touch the same field.\n\n" +
            $"New order:\n{preview}\n\nApply this order{saveNote}?",
            "Suggested order");
        if (!confirmed)
        {
            return;
        }

        Queue.Clear();
        foreach (var entry in newOrder)
        {
            Queue.Add(entry);
        }

        if (SelectedProfileName is { } name)
        {
            SaveProfile();
            StatusMessage = $"Applied the suggested order and saved it to '{name}'.";
        }
        else
        {
            StatusMessage = "Applied the suggested order.";
        }

        _activityLog.Log("Applied a suggested queue order.", ActivityEntryKind.Info);
    }

    /// <summary>
    /// SuggestQueueOrderAsync's own reordering algorithm, extracted so a test can exercise it
    /// directly — the rest of that method (the confirmation preview/apply/save) goes through
    /// ThemedMessageBox.Show, a real blocking WPF dialog with no live Application in a test host.
    /// Internal, not private, purely for that seam. Reads the live Queue/package cache itself
    /// (same as the caller above did inline) rather than taking them as parameters, so a test calls
    /// this exactly the way SuggestQueueOrderAsync itself does.
    /// </summary>
    internal async Task<(List<LibraryEntry> NewOrder, bool Changed)> ComputeSuggestedQueueOrderAsync()
    {
        var loaded = await LoadQueuedPackagesAsync();
        var (entries, packages) = (loaded.Entries, loaded.Packages);
        var classifier = new DefaultSemanticClassifier();
        var currentQueue = Queue.ToList();

        return await Task.Run(() =>
        {
            var modChangesByFolder = entries.Zip(packages, (entry, package) =>
                    (entry.FolderName, Changes: ExmodFieldChangeMapper.ToFieldChanges(package.Package, classifier)))
                .ToDictionary(x => x.FolderName, x => x.Changes, StringComparer.OrdinalIgnoreCase);

            // Best-effort, matching FindQueueConflictsAsync's own fallback — no filtering (falls
            // back to raw field count) rather than blocking the suggestion on a locked/unreadable
            // base file.
            IReadOnlyDictionary<string, JsonObject>? baseTablesByFile = null;
            try
            {
                var requiredFiles = modChangesByFolder.Values.SelectMany(c => c).Select(c => c.CurrentFile).Distinct();
                baseTablesByFile = _rebuildService.ReadKeyedBaseTables(requiredFiles, _dataFolder, new MergeReport());
            }
            catch (Exception)
            {
                // Best-effort — see the comment above.
            }

            var realChangeCountByFolder = modChangesByFolder.ToDictionary(
                kv => kv.Key,
                kv => baseTablesByFile is not null
                    ? MergeEngine.CountChangesDifferingFromBase(kv.Value, baseTablesByFile)
                    : kv.Value.Count,
                StringComparer.OrdinalIgnoreCase);

            // Sort descending by real change count (most sweeping first/lowest priority, fewest
            // changes last/highest priority — so a targeted edit wins), then slot the sorted EXMOD
            // entries back into their own original positions, leaving any opaque pak exactly where
            // it was.
            var sortedExmodEntries = currentQueue
                .Where(e => realChangeCountByFolder.ContainsKey(e.FolderName))
                .OrderByDescending(e => realChangeCountByFolder[e.FolderName])
                .ToList();

            var nextSortedIndex = 0;
            var newOrder = currentQueue
                .Select(e => realChangeCountByFolder.ContainsKey(e.FolderName) ? sortedExmodEntries[nextSortedIndex++] : e)
                .ToList();

            var changed = !newOrder.Select(e => e.FolderName)
                .SequenceEqual(currentQueue.Select(e => e.FolderName), StringComparer.OrdinalIgnoreCase);
            return (newOrder, changed);
        });
    }

    /// <summary>
    /// Inserts baseline mods missing from the queue at its front, preserving the baseline's own
    /// stored order (index 0 = lowest merge priority, so the queue's own mods override a baseline
    /// mod on any conflicting field). A baseline mod no longer in the Library, or a pack Merge &amp;
    /// Install built (see IsIslBuiltPack), is skipped silently — the queue rows themselves are the
    /// visible truth of what will merge.
    /// </summary>
    private void PrependBaselineMods()
    {
        var insertAt = 0;
        foreach (var folderName in _settingsService.Current.BaselineModFolderNames)
        {
            if (Queue.Any(q => string.Equals(q.FolderName, folderName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var entry = _libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase));
            if (entry is null || IsIslBuiltPack(entry))
            {
                continue;
            }

            Queue.Insert(insertAt, entry);
            insertAt++;
        }
    }

    /// <summary>
    /// Called from Library's own "Add to merge queue" action (Ctrl/Shift-click multi-select +
    /// right-click, or the toolbar button once 2+ rows are selected) — the only way mods reach this
    /// queue now that this page's own Library browsing pane is gone (redesign-round-2 pilot). Each
    /// folder name is looked up fresh via the repository rather than requiring Library to hold onto
    /// a LibraryEntry reference of its own.
    /// </summary>
    public void AddToQueueByFolderNames(IReadOnlyList<string> folderNames)
    {
        if (folderNames.Count == 0 || RefuseWhileRebuilding())
        {
            return;
        }

        // Mods already in the queue are no request at all (a tick that was already on): nothing to say.
        folderNames = [.. folderNames.Where(f => !Queue.Any(q => !q.IsMissingFromLibrary && string.Equals(q.FolderName, f, StringComparison.OrdinalIgnoreCase)))];
        if (folderNames.Count == 0)
        {
            return;
        }

        var byFolder = _libraryRepository.GetAll().ToDictionary(e => e.FolderName, StringComparer.OrdinalIgnoreCase);
        var addedCount = 0;
        string? lastRefusal = null;
        foreach (var folderName in folderNames)
        {
            if (byFolder.TryGetValue(folderName, out var entry))
            {
                if (AddEntryToQueue(entry))
                {
                    addedCount++;
                }
                else
                {
                    lastRefusal = StatusMessage;
                }
            }
        }

        // The reason a mod wasn't added stays readable (it used to be overwritten right here).
        StatusMessage = addedCount == folderNames.Count
            ? $"Added {addedCount} mod(s) to the merge queue."
            : addedCount == 0 && lastRefusal is not null
                ? lastRefusal
                : $"Added {addedCount} of {folderNames.Count} mod(s) to the merge queue.{(lastRefusal is null ? "" : $" {lastRefusal}")}";
        _activityLog.Log($"Added {addedCount} mod(s) to the merge queue from Library.", ActivityEntryKind.Info);
    }

    /// <summary>Whether this Library folder is in the merge queue — the Library's own "On" checkbox for a pak/EXMOD mod reads this.</summary>
    public bool IsQueued(string folderName) =>
        Queue.Any(q => string.Equals(q.FolderName, folderName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Library's "On" checkbox unticked: the mod leaves the queue (the next Merge &amp; Install drops it from the game).</summary>
    public void RemoveFromQueueByFolderNames(IReadOnlyList<string> folderNames)
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        foreach (var entry in Queue.Where(q => folderNames.Contains(q.FolderName, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            Queue.Remove(entry);
        }
    }

    /// <summary>
    /// The mod names the installed merged pak's own manifest lists (ISL-Merged.txt), as last read —
    /// what the Library's Status column compares each enabled mod against ("In game" vs "Needs
    /// Merge &amp; Install"). Empty until RefreshInstalledModNamesAsync has run, or when no Content
    /// path is set. Raises PropertyChanged so the Library can recompute.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<string> _lastInstalledModNames = [];

    /// <summary>
    /// ArcFission Library's newest data pak in the game's mods folder, if any: written by the game
    /// at start from the game's data plus its mods' rows (WirelessPower…). Merged in at each build,
    /// but only while ArcFission Library itself is installed and on: a pak it left behind holds
    /// whole tables from the game version it was written for (see LeftoverArcFissionDataPaks).
    /// </summary>
    internal string? ArcFissionDataPakPath() =>
        _settingsService.Current.IcarusContentPath is { Length: > 0 } content && InstallManifestNames.IsArcFissionLibraryActive(content)
            ? InstallManifestNames.NewestArcFissionDataPak(Path.Combine(content, "Paks", "mods"))
            : null;

    /// <summary>ArcFission data paks in the game's mods folder while ArcFission Library isn't installed or is off: old data nothing writes again, offered for removal at Install.</summary>
    private static List<string> LeftoverArcFissionDataPaks(string contentPath)
    {
        var modsDirectory = Path.Combine(contentPath, "Paks", "mods");
        if (!Directory.Exists(modsDirectory) || InstallManifestNames.IsArcFissionLibraryActive(contentPath))
        {
            return [];
        }

        return [.. Directory.GetFiles(modsDirectory).Select(Path.GetFileName).OfType<string>().Where(InstallManifestNames.IsArcFissionDataPak)];
    }

    /// <summary>
    /// ArcFission Library rebuilt its data since the installed merge (an ArcFission mod added or
    /// updated, or a game update): its newest pak isn't one the merge was built from, so its new
    /// rows aren't in the game until the next Merge &amp; Install.
    /// </summary>
    [ObservableProperty]
    private bool _arcFissionNeedsMerge;

    /// <summary>
    /// Re-reads the installed merge's manifest (LastInstalledModNames) and whether ArcFission Library
    /// wrote newer data since (ArcFissionNeedsMerge). Runs when the Library subscribes, after
    /// Install/Remove, after Settings are saved, and when the app's window is activated again (the
    /// game may have run meanwhile and ArcFission written a new pak).
    /// </summary>
    public async Task RefreshInstalledModNamesAsync()
    {
        var contentPath = _settingsService.Current.IcarusContentPath;
        if (string.IsNullOrWhiteSpace(contentPath))
        {
            LastInstalledModNames = [];
            ArcFissionNeedsMerge = false;
            return;
        }

        try
        {
            var installed = await _installService.GetInstalledStateAsync(contentPath);
            // Only with a merge installed (its manifest lists mods): with none, there's nothing for
            // ArcFission's newer rows to be missing from.
            ArcFissionNeedsMerge = installed.ModNames.Count > 0
                && ArcFissionDataPakPath() is { } arcFission
                && !installed.ModNames.Contains(Path.GetFileNameWithoutExtension(arcFission), StringComparer.OrdinalIgnoreCase);
            LastInstalledModNames = installed.ModNames;
        }
        catch (Exception)
        {
            // A missing/unreadable manifest just means "nothing known to be installed".
            LastInstalledModNames = [];
            ArcFissionNeedsMerge = false;
        }
    }

    /// <summary>
    /// Adds one Library mod to the end of the queue: a pack Merge &amp; Install built is refused (see
    /// IsIslBuiltPack), a mod already queued is left where it is, and a greyed missing row for it
    /// takes the real mod back in its place. Returns whether the entry actually got added, so a bulk
    /// caller can tally a real count; the reason it wasn't is in StatusMessage.
    /// </summary>
    private bool AddEntryToQueue(LibraryEntry entry)
    {
        // ISL's own merged pack (the Library keeps a copy of every build): queuing it folded the
        // previous merge into the new one and applied the merge options a second time on top.
        if (IsIslBuiltPack(entry))
        {
            StatusMessage = $"'{entry.Name}' is a pack Merge & Install built from your queue, so it can't be ticked itself: "
                + "tick the mods it was built from (merging it would put the old build into the new one and apply the merge options twice).";
            return false;
        }

        var existingIndex = Queue.ToList().FindIndex(q => string.Equals(q.FolderName, entry.FolderName, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            if (Queue[existingIndex].IsMissingFromLibrary)
            {
                // The profile's missing mod is back: it takes its old place.
                Queue[existingIndex] = entry;
                return true;
            }

            StatusMessage = $"'{entry.Name}' is already in the queue.";
            return false;
        }

        Queue.Add(entry);
        return true;
    }

    [RelayCommand]
    private void RemoveFromQueue()
    {
        if (!RefuseWhileRebuilding() && SelectedQueueEntry is { } entry)
        {
            Queue.Remove(entry);
        }
    }

    [RelayCommand]
    private void ClearQueue()
    {
        if (!RefuseWhileRebuilding())
        {
            Queue.Clear();
        }
    }

    [RelayCommand]
    private void MoveQueueEntryUp()
    {
        if (RefuseWhileRebuilding() || SelectedQueueEntry is not { } entry)
        {
            return;
        }

        var index = Queue.IndexOf(entry);
        if (index > 0)
        {
            Queue.Move(index, index - 1);
        }
    }

    [RelayCommand]
    private void MoveQueueEntryDown()
    {
        if (RefuseWhileRebuilding() || SelectedQueueEntry is not { } entry)
        {
            return;
        }

        var index = Queue.IndexOf(entry);
        if (index >= 0 && index < Queue.Count - 1)
        {
            Queue.Move(index, index + 1);
        }
    }

    /// <summary>Drag-and-drop reordering (MergeInstallView's own code-behind drives the actual drag
    /// gesture — WPF has no clean MVVM path for it) — same Queue.Move the up/down buttons already
    /// use, so it triggers the identical CollectionChanged-driven manual-pick invalidation above
    /// for free.</summary>
    public void ReorderQueueEntry(LibraryEntry entry, int targetIndex)
    {
        if (RefuseWhileRebuilding())
        {
            return;
        }

        var currentIndex = Queue.IndexOf(entry);
        if (currentIndex < 0 || targetIndex < 0 || targetIndex >= Queue.Count || currentIndex == targetIndex)
        {
            return;
        }

        Queue.Move(currentIndex, targetIndex);
    }

    /// <summary>
    /// Big-plan item 8b: opens the pak-vs-pak comparison window (non-modal, like the editor's own
    /// research windows) — built for verifying this app's rebuilt pak against classic IMM's own
    /// installed merged pak during migration, but works for any two paks.
    /// </summary>
    [RelayCommand]
    private void ComparePaks()
    {
        var viewModel = new PakCompareViewModel(_pakCompareService, _settingsService, _outputPakPath);
        var window = new PakCompareWindow(viewModel) { Owner = Application.Current.MainWindow };
        window.Show();
    }

    /// <summary>
    /// "Installed vs this list" — reads what this app can positively identify as currently
    /// installed (its own pak manifest in the real game folder) and diffs it against the current
    /// queue plus any attached prebuilt paks (both now folded into the one merged pak by Rebuild,
    /// so both are listed in the same manifest), so a user can see exactly what Install would
    /// change before clicking it. Gameplay options aren't part of this comparison — they're not a
    /// "mod" with a name to diff, they overwrite specific fields regardless of what's currently
    /// there. UE4SS mods aren't part of this either (Phase 8.5) — see Library's own UE4SS tab,
    /// which shows their real state directly.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CompareToInstalledAsync()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            ComparisonStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        try
        {
            var installed = await _installService.GetInstalledStateAsync(_settingsService.Current.IcarusContentPath!);

            // For an opaque entry, the name RebuildService's own WriteManifest actually records is
            // Path.GetFileNameWithoutExtension of its resolved .pak file, not its (possibly Nexus-
            // enriched) display Name — the comparison below is a literal string match against what
            // the real manifest on disk says, so opaque entries need that same derivation here.
            // An opaque entry can hold several paks (a bundle) — the manifest lists each one, so
            // the comparison expands the entry the same way.
            var currentList = Queue.Where(e => !e.IsMissingFromLibrary).SelectMany(entry =>
            {
                if (!entry.IsOpaquePak)
                {
                    return [entry.Name];
                }

                var pakPaths = Directory.GetFiles(_libraryRepository.GetFolderPath(entry.FolderName), "*.pak").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
                return pakPaths.Count > 0 ? pakPaths.Select(Path.GetFileNameWithoutExtension).Select(n => n!) : [entry.Name];
            }).ToList();
            // Rebuild folds ArcFission Library's data pak in too, and the manifest lists it.
            if (ArcFissionDataPakPath() is { } arcFission)
            {
                currentList.Insert(0, Path.GetFileNameWithoutExtension(arcFission));
            }

            var comparison = InstalledVsListComparer.Compare(installed.ModNames, currentList);

            ComparisonEntries.Clear();
            foreach (var entry in comparison.OrderBy(e => e.State).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            {
                var symbol = entry.State switch
                {
                    InstalledComparisonState.Added => "+",
                    InstalledComparisonState.Removed => "-",
                    _ => "=",
                };
                ComparisonEntries.Add($"{symbol}  {entry.Name}");
            }

            ComparisonStatusMessage = ComparisonEntries.Count == 0 ? "Nothing installed yet to compare against." : null;
        }
        catch (Exception ex)
        {
            ComparisonStatusMessage = $"Compare failed: {ex.Message}";
        }
    }

    /// <summary>
    /// The one visible button for turning the current queue into a real install — merges Rebuild
    /// and Install into a single click rather than requiring two, per direct user feedback ("why
    /// can't it just be an install and it merges and installs"). Internally still two distinct
    /// steps (RebuildAsync, a safe local-only pak build; InstallAsync, the one action in this whole
    /// app that writes into the real game folder, gated behind its own explicit Yes/No confirmation)
    /// — chaining them, rather than merging their bodies into one method, keeps each independently
    /// callable/testable and means the confirmation dialog still only appears right before the
    /// actual risky write, not before the safe rebuild step that precedes it. Skips the install
    /// step (and never shows its confirmation dialog at all) if the rebuild itself failed.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task RebuildAndInstallAsync()
    {
        // Gates on RebuildAsync's own success THIS call, not merely "a pak file happens to exist
        // on disk" — a stale pak from an earlier successful build stays on disk after a later
        // Rebuild is skipped (the guard below) or fails (an exception, a missing UnrealPak.exe
        // path), and File.Exists alone can't tell "just built" apart from "leftover from before".
        // Found live: enabling a Category-1-only option (e.g. Remove Level Cap) with an empty
        // queue tripped the guard below (it only checked Category-2's RequiredCurrentFiles), so
        // Rebuild silently no-opped — and the combined button then went ahead and installed the
        // OLD pak from a previous test, with no error shown, silently not reflecting what was
        // actually configured. That guard is fixed too (see GameplayOptions.IsAnyActive), but this
        // is the deeper fix: Install must never run off a rebuild that didn't actually happen.
        if (await RebuildAsync())
        {
            await InstallAsync();
        }
    }

    // No [RelayCommand] — RebuildAndInstallAsync above is the only caller since the combined
    // Install button replaced the separate Rebuild/Install pair; the generated commands would be
    // dead bindings nothing in XAML references anymore.
    /// <returns>True only if this call actually produced a fresh pak — RebuildAndInstallAsync uses this, not File.Exists(_outputPakPath), to decide whether it's safe to Install (see its own comment for why that distinction is load-bearing).</returns>
    /// <remarks>Internal, not private — RebuildAndInstallAsync's own orchestration (does Install only run when Rebuild really succeeded, does it never run when Rebuild guards out or throws) is exactly the thing worth testing directly against fakes for IRebuildService/IInstallService, without ever reaching InstallAsync's own ThemedMessageBox confirmation (a real blocking WPF dialog with no live Application in a test host).</remarks>
    internal async Task<bool> RebuildAsync()
    {
        // Gameplay options can apply on their own, with an empty queue, per the spec ("the ability
        // to add merge options to game with no mods selected") — only block if there's genuinely
        // nothing to do at all. Queue can hold opaque pak entries too now (folded into the same
        // output by Rebuild, not field-merged), so a queue containing only those is still real work.
        // GameplayOptions.IsAnyActive, not a separately-rederived check — reads the SAME
        // classification GenerateFixedFieldChanges/RequiredCurrentFiles conceptually mirror, so
        // this guard can't miss an option the way it once did (Category 2's RequiredCurrentFiles
        // alone used to miss a Category-1-only option enabled with an empty queue, silently
        // skipping the rebuild those options needed). A greyed missing row is nothing to build.
        if (IsRebuilding)
        {
            return false;
        }

        if (!Queue.Any(IsBuildable) && !BuildGameplayOptionsFromUi().IsAnyActive)
        {
            StatusMessage = Queue.Count > 0
                ? "None of the queued mods are in your Library — add mods to the queue, or enable a gameplay option, first."
                : "Add at least one mod to the queue, or enable a gameplay option, first.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_settingsService.Current.UnrealPakExePath))
        {
            StatusMessage = "Set UnrealPak.exe path in Settings first.";
            return false;
        }

        // From here on the queue, profile and options are locked (RefuseWhileRebuilding, and the
        // page's controls), including while game data refreshes below.
        IsRebuilding = true;
        try
        {
            // The merge writes whole tables from the extracted game data: built after a game update
            // but before Refresh game data, it puts the old version of every table a mod touches back
            // into the game, undoing the update there.
            if (!await EnsureGameDataCurrentAsync())
            {
                return false;
            }

            // Everything this build is made from, taken once: a queue, profile or option change made
            // while it runs (from the Library's ON box, say) can't change which mod a conflict pick
            // means, what's merged, what's logged or which profile the built pack is filed under.
            var snapshot = TakeSnapshot();
            if (snapshot.Queue.Count == 0 && !snapshot.Options.IsAnyActive)
            {
                StatusMessage = "Add at least one mod to the queue, or enable a gameplay option, first.";
                return false;
            }

            // The queue saves itself into "Default" a moment after the first change
            // (MarkWorkingStateChanged); a build started before any change (or before that save)
            // still files what it built under a real, reloadable "Default" profile here, without
            // ever overriding a profile the user picked themselves. Best-effort: a failure here must
            // never block the real Rebuild the user actually asked for.
            if (SelectedProfileName is null)
            {
                try
                {
                    _profileStore.Save(new Profile { Name = "Default", MergeQueueFolderNames = [.. Queue.Select(e => e.FolderName)], Options = snapshot.Options, ConflictWinners = new(snapshot.Winners) });
                    ReloadProfileNames();
                }
                catch (Exception)
                {
                }
            }

            StatusMessage = "Rebuilding…";
            RebuildProgressPercent = 0;
            RebuildStageText = null;
            Warnings.Clear();

            // A mod folder that exists on disk but can't be read never becomes a Library entry at all,
            // so it can't be queued and silently contributes nothing to the merge. Until now the only
            // hint was a line on the Library page — meaning a user could rebuild and install a pack
            // quietly missing a mod they believe is included. Surfaced here, where it actually matters.
            // Added after the Clear above, or it would be wiped before anyone saw it.
            foreach (var folderName in _libraryRepository.UnreadableFolders)
            {
                Warnings.Add(
                    $"'{folderName}' is in your mods folder but couldn't be read, so it is NOT part of this build. "
                    + "Check that mod's own files (a corrupt .EXMOD is the usual cause).");
            }

            foreach (var missing in snapshot.SkippedMissing)
            {
                Warnings.Add($"'{missing}' is in this profile but not in your Library, so it is NOT part of this build. Import it again, or remove it from the queue.");
            }

            using var perfScope = _performanceTracker.Track("Rebuild");
            var loaded = await LoadQueuedPackagesAsync(snapshot.Queue);
            // ArcFission Library's data pak: its rows go into the merge (which outranks its pak),
            // as the lowest-priority input so any queued mod or option wins a clash with it.
            List<string> lowPriorityPaks = snapshot.ArcFissionPakPath is { } arcFission ? [arcFission] : [];

            // Progress<T>'s own callback marshals back through the SynchronizationContext captured
            // at construction time — safe to update these bound properties directly from it since
            // this constructor runs on the UI thread (a RelayCommand invoked from a button click),
            // even though RebuildAsync's own pipeline reports from a background Task.Run.
            var progress = new Progress<RebuildStageProgress>(p =>
            {
                RebuildStageText = p.Stage;
                RebuildProgressPercent = p.PercentComplete;
            });

            var (picks, unmatchedPicks) = await ResolveWinnerPicksAsync(snapshot, loaded);
            if (unmatchedPicks > 0)
            {
                Warnings.Add(
                    $"{unmatchedPicks} saved conflict pick(s) no longer match what the queued mods change (a mod was updated or reordered into agreement), so they weren't used. "
                    + "Review conflicts to pick again.");
            }

            // Advanced log: exactly what this build was made from.
            Serilog.Log.Debug("Merge & Install build: queue (lowest priority first) {Queue}; prebuilt paks {Paks} at {Positions}; ArcFission/low-priority {Low}; {Picks} conflict pick(s) {PickList}; options {Options}",
                string.Join(" > ", snapshot.Queue.Select(e => e.Name)), loaded.PrebuiltPakFilePaths.Select(Path.GetFileName), loaded.PrebuiltPakQueuePositions,
                lowPriorityPaks.Select(Path.GetFileName),
                picks?.Count ?? 0, picks?.Select(p => $"{p.Key.CurrentFile}/{p.Key.ItemName}.{p.Key.FieldName}=#{p.Value}"), snapshot.OptionsSummary);
            var result = await _rebuildService.RebuildAsync(
                loaded.Packages, snapshot.Options, _dataFolder, snapshot.UnrealPakExePath, _outputPakPath, loaded.PrebuiltPakFilePaths, picks, progress,
                lowPriorityPrebuiltPakFilePaths: lowPriorityPaks, prebuiltPakQueuePositions: loaded.PrebuiltPakQueuePositions);

            var foldedInCount = loaded.PrebuiltPakFilePaths.Count + lowPriorityPaks.Count;
            var prebuiltNote = foldedInCount > 0 ? $", {foldedInCount} prebuilt pak(s) folded in" : "";
            StatusMessage = $"Built '{result.OutputPakPath}' — {result.PackedFileCount} files packed, "
                + $"{result.MergedFileCount} data table(s) merged{prebuiltNote}.";
            foreach (var warning in result.Warnings)
            {
                Warnings.Add(warning);
            }

            // Notes go in the same list, prefixed rather than styled separately: they're read at
            // the same moment for the same reason ("did anything about this build need my
            // attention?"), and the prefix keeps "this is fine" distinguishable from "this failed".
            foreach (var note in result.Notes)
            {
                Warnings.Add($"Note: {note}");
            }

            CanCopyToGame = true;

            // The Server page's "Install my merged pak" shows the new build without a page switch.
            _messenger.Send(new MergedPakBuiltMessage());

            // The Library import below sends LibraryChangedMessage, and this VM's own handler for
            // that message invalidates manual conflict picks — right for an external change (a
            // queued mod's content moved under the picks), wrong here, where the only change is the
            // app's own built pack landing in the Library, which no pick refers to. An audit
            // finding: every successful Rebuild silently discarded the picks, so a second Install
            // of the same queue resolved last-wins instead of what the user had reviewed. The picks
            // are carried across the self-sent message. And the import is bookkeeping AFTER a
            // verified pak exists, so its own failure (a locked ISL-Merged folder, antivirus) is a
            // warning, never "Rebuild failed" with Install withheld.
            var picksBeforeImport = _manualPicks;
            var pickStatusBeforeImport = ConflictStatusMessage;
            try
            {
                ImportBuiltPakIntoLibrary(snapshot.ProfileName);
            }
            catch (Exception ex)
            {
                Warnings.Add($"The built pack couldn't be added to your Library ({ex.Message}) — the pak itself is fine and can still be installed.");
            }
            finally
            {
                _manualPicks = picksBeforeImport;
                ConflictStatusMessage = pickStatusBeforeImport;
            }

            var pickNote = picks is { Count: > 0 } ? $", {picks.Count} conflict(s) won by your pick" : "";
            _activityLog.Log($"Rebuilt pack with {snapshot.Queue.Count} mod(s) — {result.PackedFileCount} files packed{pickNote}.", ActivityEntryKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else in this app: a missing base data file, a
            // UnrealPak failure, or a malformed queued mod should show a status message, not
            // crash the app.
            StatusMessage = $"Rebuild failed: {ex.Message}";
            _activityLog.Log(StatusMessage, ActivityEntryKind.Warning);
            // Whatever is (or isn't) at _outputPakPath now is not a build the user asked for — the
            // standalone Copy to game must not offer it (an audit finding: it installed a stale or
            // partial pak after a failed rebuild).
            CanCopyToGame = false;
            return false;
        }
        finally
        {
            IsRebuilding = false;
        }
    }

    /// <summary>
    /// The game-update guard before a build: when Icarus was updated since the last Refresh game
    /// data, asks to refresh first (Yes runs it, then the build goes on with the fresh data) and
    /// stops with the reason otherwise. True when the build can go ahead.
    /// </summary>
    private async Task<bool> EnsureGameDataCurrentAsync()
    {
        if (_gameDataFreshness is null)
        {
            return true;
        }

        bool outdated;
        try
        {
            outdated = await _gameDataFreshness.IsGameDataOutdatedAsync();
        }
        catch (Exception)
        {
            // Can't tell: not a reason to refuse a build.
            return true;
        }

        if (!outdated)
        {
            return true;
        }

        var refresh = Confirm(
            "Icarus has been updated since your last Refresh game data.\n\n"
            + "Building now would put the old version of every game table your mods change back into the game, undoing the update there.\n\n"
            + "Refresh game data now, then build?",
            "Game data is out of date");
        if (!refresh)
        {
            StatusMessage = "Not built: Icarus was updated since your last Refresh game data. Refresh game data (Settings), then Install again.";
            return false;
        }

        StatusMessage = "Refreshing game data…";
        RebuildStageText = "Refreshing game data…";
        bool refreshed;
        try
        {
            refreshed = await _gameDataFreshness.RefreshGameDataAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Not built: Refresh game data failed ({ex.Message}).";
            return false;
        }

        if (!refreshed)
        {
            StatusMessage = "Not built: Refresh game data didn't finish — see Settings for why, then Install again.";
            return false;
        }

        _activityLog.Log("Refreshed game data before building (Icarus had been updated).", ActivityEntryKind.Info);
        return true;
    }

    /// <summary>
    /// Everything one build or conflict check is made from, taken at once from the page: the queue
    /// in order (only rows that can build — no greyed missing rows, no ISL-built packs), the options,
    /// the conflict winners, ArcFission's data pak and the profile name.
    /// </summary>
    internal sealed record BuildSnapshot(
        IReadOnlyList<LibraryEntry> Queue,
        IReadOnlyList<string> SkippedMissing,
        GameplayOptions Options,
        string OptionsSummary,
        IReadOnlyDictionary<string, string> Winners,
        IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? ManualPicks,
        string? ArcFissionPakPath,
        string ProfileName,
        string UnrealPakExePath);

    private bool IsBuildable(LibraryEntry entry) => !entry.IsMissingFromLibrary && !IsIslBuiltPack(entry);

    private BuildSnapshot TakeSnapshot() => new(
        [.. Queue.Where(IsBuildable)],
        [.. Queue.Where(e => e.IsMissingFromLibrary).Select(e => e.Name)],
        BuildGameplayOptionsFromUi(),
        ActiveOptionsSummary,
        new Dictionary<string, string>(_winners, StringComparer.OrdinalIgnoreCase),
        _manualPicks,
        ArcFissionDataPakPath(),
        SelectedProfileName ?? "Default",
        _settingsService.Current.UnrealPakExePath ?? "");

    /// <summary>
    /// Imports the just-built pak into Library so it's a real, inspectable, re-mountable entry the
    /// moment it exists — not only after a full Install. Build and Install (Copy to game) are two
    /// genuinely separate steps under the hood already (RebuildAsync only ever writes to this app's
    /// own Staged_Build folder; InstallAsync is the one thing that touches the real game folder,
    /// gated behind its own confirmation) — this closes the one gap that made Build not feel like
    /// its own first-class result: previously the pak only became a Library entry after a successful
    /// Install, so a build the user didn't (yet) choose to install had nowhere to be seen or reused
    /// from later.
    ///
    /// "Replace, not accumulate": ImportPak would otherwise derive a fresh "_2"/"_3"-suffixed folder
    /// name every time (its own collision-avoidance rule), leaving one stale Library entry behind
    /// per build instead of one that stays current.
    /// </summary>
    private void ImportBuiltPakIntoLibrary(string profileName)
    {
        // The built pack under the merged pak's earlier name is replaced too.
        foreach (var legacy in InstallManifestNames.LegacyMergedPakFileNames.Select(Path.GetFileNameWithoutExtension))
        {
            if (_libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, legacy, StringComparison.OrdinalIgnoreCase)) is { IsOpaquePak: true } old
                && old.MergedPackModNames is not null)
            {
                _libraryRepository.Delete(old.FolderName);
            }
        }

        var builtFolderName = Path.GetFileNameWithoutExtension(_outputPakPath);
        var existing = _libraryRepository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, builtFolderName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _libraryRepository.Delete(existing.FolderName);
        }
        // profileName is "Default" when none was selected (RebuildAsync's own auto-save uses the same
        // effective name, without changing SelectedProfileName itself), and for a build it's the
        // profile the build started from, not whatever is selected by the time it finishes.
        _libraryRepository.ImportPak(_outputPakPath, mergedPackProfileName: profileName);
        _messenger.Send(new LibraryChangedMessage());
    }

    /// <summary>
    /// A queue's mods read from disk. Entries/Packages are the EXMOD mods, 1:1; each prebuilt pak
    /// file (an opaque entry can hold several) comes with its queue position (how many EXMOD mods
    /// are ahead of it) and its entry. MergeParticipants turns the positions into the true order.
    /// </summary>
    internal sealed record LoadedQueue(
        List<LibraryEntry> Entries,
        List<ExmodPackageContents> Packages,
        List<string> PrebuiltPakFilePaths,
        List<int> PrebuiltPakQueuePositions,
        List<LibraryEntry> PrebuiltPakEntries);

    /// <summary>
    /// The live queue's buildable rows (see BuildSnapshot), read from disk. Queue order = merge
    /// priority (index 0 lowest, matching MergeEngine's own convention). Each EXMOD entry's own
    /// package is served from _packageCache when that folder's own on-disk fingerprint hasn't
    /// changed since it was last read (see GetOrReadPackage); the queue itself is copied on the UI
    /// thread first, so an edit mid-read can't race with the disk reads, which run off-thread.
    /// </summary>
    private Task<LoadedQueue> LoadQueuedPackagesAsync() => LoadQueuedPackagesAsync([.. Queue.Where(IsBuildable)]);

    private async Task<LoadedQueue> LoadQueuedPackagesAsync(IReadOnlyList<LibraryEntry> queueSnapshot)
    {
        return await Task.Run(() =>
        {
            var loaded = new LoadedQueue([], [], [], [], []);
            foreach (var entry in queueSnapshot)
            {
                if (!entry.IsOpaquePak)
                {
                    loaded.Entries.Add(entry);
                    loaded.Packages.Add(GetOrReadPackage(entry));
                    continue;
                }

                // An opaque pak folder holds one .pak (ImportPak) or several (ImportPaks — one
                // download that shipped a bundle); every one of them is folded in. A folder whose
                // paks vanished since being queued (Library entry deleted/moved) is silently skipped
                // rather than failing the whole rebuild.
                var folder = _libraryRepository.GetFolderPath(entry.FolderName);
                var paks = Directory.Exists(folder)
                    ? Directory.GetFiles(folder, "*.pak").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList()
                    : [];
                foreach (var pak in paks)
                {
                    loaded.PrebuiltPakFilePaths.Add(pak);
                    loaded.PrebuiltPakQueuePositions.Add(loaded.Packages.Count);
                    loaded.PrebuiltPakEntries.Add(entry);
                }
            }

            return loaded;
        });
    }

    /// <summary>
    /// ExmodFolder.Read is real disk I/O plus a full JSON parse of the mod's own .EXMOD — this
    /// serves a cached result whenever entry.FolderName's own on-disk fingerprint (file count plus
    /// the newest LastWriteTimeUtc among its files) hasn't changed since the last read, avoiding
    /// that cost for every OTHER queued mod on a mutation that only actually touched one of them.
    /// Runs on the same background thread LoadQueuedPackagesAsync already does its I/O on (never
    /// called from the UI thread) — the fingerprint computation itself is still a real, if cheap,
    /// disk walk (Directory.EnumerateFiles + a stat per file), not a time-based assumption.
    /// </summary>
    private ExmodPackageContents GetOrReadPackage(LibraryEntry entry)
    {
        var folderPath = _libraryRepository.GetFolderPath(entry.FolderName);
        var fingerprint = ComputeFolderFingerprint(folderPath);

        lock (_packageCacheLock)
        {
            _packageCacheUsedUtc = DateTime.UtcNow;
            if (_packageCache.TryGetValue(entry.FolderName, out var cached) && cached.Fingerprint == fingerprint)
            {
                return cached.Package;
            }
        }

        var package = ExmodFolder.Read(folderPath);

        lock (_packageCacheLock)
        {
            _packageCache[entry.FolderName] = (fingerprint, package);
        }

        return package;
    }

    /// <summary>When a queued mod was last read through the cache (under _packageCacheLock).</summary>
    private DateTime _packageCacheUsedUtc;

    /// <summary>
    /// Every queued mod read from disk (about 50 MB for a 25-mod queue) is let go while the app
    /// sits unused; the next build or conflict check reads them again.
    /// </summary>
    public bool ReleaseIfIdle(TimeSpan idleFor)
    {
        lock (_packageCacheLock)
        {
            if ((_packageCache.Count == 0 && _prebuiltPakChangesCache.Count == 0) || DateTime.UtcNow - _packageCacheUsedUtc < idleFor)
            {
                return false;
            }

            _packageCache.Clear();
            _prebuiltPakChangesCache.Clear();
            return true;
        }
    }

    private static FolderFingerprint ComputeFolderFingerprint(string folderPath)
    {
        if (!Directory.Exists(folderPath))
        {
            return default;
        }

        var fileCount = 0;
        var maxWriteUtc = DateTime.MinValue;
        foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
        {
            fileCount++;
            var writeUtc = File.GetLastWriteTimeUtc(filePath);
            if (writeUtc > maxWriteUtc)
            {
                maxWriteUtc = writeUtc;
            }
        }

        return new FolderFingerprint(fileCount, maxWriteUtc);
    }

    /// <summary>
    /// Drops every cached package — the fingerprint check in GetOrReadPackage already catches a
    /// queued mod's own on-disk change correctly on its own (that's its whole point), so this is
    /// belt-and-suspenders: cheap insurance against a rename/delete/re-import reusing a FolderName
    /// (LibraryChangedMessage's own trigger) ever serving another mod's stale cached content, however
    /// unlikely that exact coincidence is. Not itself a trigger for a fresh recompute — the next one
    /// that runs (from an actual Queue mutation) just won't read stale cached content when it does.
    /// </summary>
    private void InvalidatePackageCache()
    {
        lock (_packageCacheLock)
        {
            _packageCache.Clear();
        }
    }

    /// <summary>
    /// Builds DataTableRowIndex.Build(_dataFolder) — a full walk-and-parse of every extracted
    /// game-data JSON file — at most once, reused by every later call until a real "Update data
    /// folder" run invalidates it (see the WeeklyChangeReportUpdatedMessage registration in the
    /// constructor). Safe to cache the base build: WithDeclaredRows (the only thing callers do with
    /// the result) copies before mutating, never touching this cached instance.
    /// </summary>
    private DataTableRowIndex GetOrBuildBaseDataTableIndex()
    {
        lock (_packageCacheLock)
        {
            return _cachedBaseDataTableIndex ??= DataTableRowIndex.Build(_dataFolder);
        }
    }

    /// <summary>One conflict check: what it found, and who took part, in merge order (Ids[i] is the winner id of the mod shown as Names[i]).</summary>
    internal sealed record QueueConflicts(
        IReadOnlyList<FieldConflict> Conflicts,
        IReadOnlyList<NewItemNameCollision> NewItemCollisions,
        IReadOnlyList<string> Names,
        IReadOnlyList<string> Ids)
    {
        public static QueueConflicts None { get; } = new([], [], [], []);

        public string? NameOf(string id) => Ids.ToList().IndexOf(id) is >= 0 and var i ? Names[i] : null;

        public string? IdOf(string name) => Names.ToList().IndexOf(name) is >= 0 and var i ? Ids[i] : null;
    }

    /// <summary>
    /// Per prebuilt pak (and ArcFission's data pak): its field changes as last extracted, keyed by
    /// path, with the file's size and write time — so the background conflict badge counts the same
    /// set the review window and the build do without unpacking every pak on every queue change.
    /// Cleared when the game data changes (a pak's changes are its diff against it).
    /// </summary>
    private readonly Dictionary<string, (long Length, DateTime WriteUtc, IReadOnlyList<FieldChange> Changes)> _prebuiltPakChangesCache =
        new(StringComparer.OrdinalIgnoreCase);

    private async Task<IReadOnlyList<FieldChange>> GetPrebuiltPakChangesAsync(string pakPath, string unrealPakExePath)
    {
        var file = new FileInfo(pakPath);
        var stamp = (file.Exists ? file.Length : -1, file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue);
        lock (_packageCacheLock)
        {
            if (_prebuiltPakChangesCache.TryGetValue(pakPath, out var cached) && (cached.Length, cached.WriteUtc) == stamp)
            {
                return cached.Changes;
            }
        }

        var results = await _rebuildService.ComputePrebuiltPakFieldChangesAsync([pakPath], _dataFolder, unrealPakExePath, new MergeReport());
        IReadOnlyList<FieldChange> changes = results.Count > 0 ? results[0].Changes : [];
        lock (_packageCacheLock)
        {
            _prebuiltPakChangesCache[pakPath] = (stamp.Item1, stamp.Item2, changes);
        }

        return changes;
    }

    /// <summary>
    /// The one conflict check behind the badge, the review window, and the build's saved-winner
    /// picks, so all three count the same set: every queued EXMOD mod and prebuilt pak in true queue
    /// order (the order RebuildService merges in, see its prebuiltPakQueuePositions), ArcFission
    /// Library's data pak first (lowest priority) and the "Built-in gameplay options" synthetic entry
    /// last, so a candidate's index here is the index Rebuild uses. A pak's changes are its diff
    /// against the game data with its sure old copies left out, as Rebuild does
    /// (ComputePrebuiltPakFieldChangesAsync; cached per pak). Paks are left out only when UnrealPak
    /// isn't set up, which a build can't run without either. Display names are made unique (a
    /// " (folder)" suffix) so a pick by name always means one mod.
    ///
    /// Also runs MergeEngine.FindNewItemNameCollisions over the exact same (names, orderedModChanges,
    /// baseTablesByFile) — a genuinely different question from FieldConflict's own "do two mods
    /// disagree on one field's value" (see NewItemNameCollision's own doc comment), surfaced through
    /// the same picker rather than a parallel mechanism.
    /// </summary>
    private async Task<QueueConflicts> FindQueueConflictsAsync(BuildSnapshot snapshot, LoadedQueue loaded)
    {
        var classifier = new DefaultSemanticClassifier();
        var unrealPak = snapshot.UnrealPakExePath;
        var canUnpack = !string.IsNullOrWhiteSpace(unrealPak);
        var orderedModChanges = new List<IReadOnlyList<FieldChange>>();
        var names = new List<string>();
        var ids = new List<string>();

        var exmodChanges = await Task.Run(
            () => loaded.Packages.Select(p => ExmodFieldChangeMapper.ToFieldChanges(p.Package, classifier)).ToList());
        var arcFission = canUnpack ? snapshot.ArcFissionPakPath : null;
        // MergeParticipants' order, the same one RebuildService merges in.
        foreach (var participant in MergeParticipants.Build(
                     arcFission is null ? 0 : 1, loaded.Packages.Count, canUnpack ? loaded.PrebuiltPakFilePaths.Count : 0,
                     canUnpack ? loaded.PrebuiltPakQueuePositions : null))
        {
            switch (participant.Kind)
            {
                case MergeParticipantKind.LowPriorityPak:
                    var arcFissionChanges = await GetPrebuiltPakChangesAsync(arcFission!, unrealPak);
                    if (arcFissionChanges.Count > 0)
                    {
                        orderedModChanges.Add(arcFissionChanges);
                        names.Add(Path.GetFileNameWithoutExtension(arcFission!));
                        ids.Add(ArcFissionWinnerId);
                    }

                    break;
                case MergeParticipantKind.Mod:
                    orderedModChanges.Add(exmodChanges[participant.Index]);
                    names.Add(loaded.Entries[participant.Index].Name);
                    ids.Add(loaded.Entries[participant.Index].FolderName);
                    break;
                case MergeParticipantKind.Pak:
                    var pakPath = loaded.PrebuiltPakFilePaths[participant.Index];
                    var pakChanges = await GetPrebuiltPakChangesAsync(pakPath, unrealPak);
                    if (pakChanges.Count > 0)
                    {
                        var pakName = Path.GetFileNameWithoutExtension(pakPath);
                        orderedModChanges.Add(pakChanges);
                        names.Add(pakName);
                        ids.Add($"{loaded.PrebuiltPakEntries[participant.Index].FolderName}/{pakName}");
                    }

                    break;
                case MergeParticipantKind.Options:
                    var fixedOptionChanges = GameplayOptionsFieldChangeGenerator.GenerateFixedFieldChanges(snapshot.Options, _dataFolder, new MergeReport());
                    if (fixedOptionChanges.Count > 0)
                    {
                        orderedModChanges.Add(fixedOptionChanges);
                        names.Add(OptionsWinnerId);
                        ids.Add(OptionsWinnerId);
                    }

                    break;
            }
        }

        // Two mods with the same display name would make a pick by name ambiguous.
        var duplicateNames = names.GroupBy(n => n, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < names.Count; i++)
        {
            if (duplicateNames.Contains(names[i]))
            {
                names[i] = $"{names[i]} ({ids[i]})";
            }
        }

        var (conflicts, newItemCollisions) = await Task.Run(() =>
        {
            // Base-aware filtering (see MergeEngine.Merge's own doc comment) needs real current
            // base values for whatever files this preview's own candidates touch — genuinely
            // best-effort: a report here is thrown away, not surfaced, and any read failure (a
            // locked/permission-denied base file — ReadBaseTables' own File.ReadAllText has no
            // catch for that, unlike a merely-missing file) falls back to no filtering rather than
            // blocking the conflict badge/picker entirely. The same baseTablesByFile is what
            // lets FindNewItemNameCollisions tell a genuinely new item apart from an existing one
            // every real EXMOD-sourced FieldChange otherwise always marks IsNewItem: true (see its
            // own doc comment) — without it, this would fall back to that unreliable raw flag.
            IReadOnlyDictionary<string, JsonObject>? baseTablesByFile = null;
            try
            {
                var requiredFiles = orderedModChanges.SelectMany(c => c).Select(c => c.CurrentFile).Distinct();
                baseTablesByFile = _rebuildService.ReadKeyedBaseTables(requiredFiles, _dataFolder, new MergeReport());
            }
            catch (Exception)
            {
                // Best-effort — see the comment above.
            }

            return (
                MergeEngine.FindConflicts(names, orderedModChanges, baseTablesByFile),
                MergeEngine.FindNewItemNameCollisions(names, orderedModChanges, baseTablesByFile));
        });
        return new QueueConflicts(conflicts, newItemCollisions, names, ids);
    }

    /// <summary>Guards RecomputeConflictCountAsync against two overlapping runs (the queue/options changing again while a previous background recompute is still in flight) finishing out of order — same "only the newest request's result is applied" shape as NexusCatalogViewModel's own _loadVersion.</summary>
    private int _conflictCountVersion;

    /// <summary>Best-effort background refresh of ConflictCount — a malformed queued mod here just leaves the badge at its last value; ReviewConflictsAsync/Rebuild surface the real error when the user actually acts. Internal (not private) purely so a test can invoke the exact same recompute the debounce timer would have, on demand — see IsRecomputeDebouncePending's own doc comment.</summary>
    internal async Task RecomputeConflictCountAsync()
    {
        var version = ++_conflictCountVersion;

        var snapshot = TakeSnapshot();
        if (snapshot.Queue.Count == 0)
        {
            ApplyConflicts(QueueConflicts.None);
            return;
        }

        try
        {
            var conflicts = await FindQueueConflictsAsync(snapshot, await LoadQueuedPackagesAsync(snapshot.Queue));
            if (version == _conflictCountVersion)
            {
                ApplyConflicts(conflicts);
            }
        }
        catch (Exception)
        {
            // Left at its last value on purpose (see the doc comment above): zeroing it here read as
            // "the conflict is gone" the moment a queued mod's file was briefly locked mid-save.
        }
    }

    private QueueConflicts _lastConflicts = QueueConflicts.None;

    /// <summary>The bar, the badge and the ⚠ rows from one conflict check — counted by MergeConflictTally, the same as the review window.</summary>
    private void ApplyConflicts(QueueConflicts found)
    {
        _lastConflicts = found;
        ConflictTally = MergeConflictTally.From(found.Conflicts, found.NewItemCollisions);
        ConflictCount = ConflictTally.NeedsDecision;
        ConflictingModNamesByMod = MergeEngine.GroupConflictsByMod(found.Conflicts);
    }

    /// <summary>Test-only: the review window Review conflicts would open for the last conflict check.</summary>
    internal MergeConflictsViewModel CreateConflictReviewForTesting() =>
        new(_lastConflicts.Conflicts, _lastConflicts.NewItemCollisions, ToDisplayWinners(_winners, _lastConflicts));

    /// <summary>
    /// The saved winners (by id) as the review window shows them (by the name in this check). A
    /// winner still saved by display name (ambiguous at migration) works while one mod here has it.
    /// Winners for mods not in this check are left out (kept aside by FromDisplayWinners).
    /// </summary>
    private static Dictionary<string, string> ToDisplayWinners(IReadOnlyDictionary<string, string> winners, QueueConflicts found)
    {
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, winner) in winners)
        {
            if (found.NameOf(winner) is { } name)
            {
                display[key] = name;
            }
            else if (found.IdOf(winner) is not null)
            {
                display[key] = winner;
            }
        }

        return display;
    }

    /// <summary>
    /// The review window's winners (by name) back to ids, plus the saved winners it wasn't shown
    /// (a mod out of the queue for a while) for values not in conflict right now. A value in
    /// conflict now was decided in the window, so an unshown old pick for it goes.
    /// </summary>
    private static Dictionary<string, string> FromDisplayWinners(
        IReadOnlyDictionary<string, string> result, IReadOnlyDictionary<string, string> previous,
        IReadOnlyDictionary<string, string> shown, QueueConflicts found)
    {
        var inConflictNow = found.Conflicts
            .Select(c => MergeConflictsViewModel.WinnerKey(c.CurrentFile, c.ItemName, c.FieldName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var winners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, winner) in previous)
        {
            if (!shown.ContainsKey(key) && !inConflictNow.Contains(key))
            {
                winners[key] = winner;
            }
        }

        foreach (var (key, name) in result)
        {
            winners[key] = found.IdOf(name) ?? name;
        }

        return winners;
    }

    /// <summary>Keeps the winners with the selected profile at once (only the winners: its queue and options stay as saved).</summary>
    private void SaveWinnersToProfile()
    {
        if (SelectedProfileName is not { } name)
        {
            // No profile yet: the auto-save creates "Default" with the queue, options and winners.
            MarkWorkingStateChanged();
            return;
        }

        try
        {
            var profile = _profileStore.Load(name);
            profile.ConflictWinners = new(_winners);
            _profileStore.Save(profile);
        }
        catch (Exception ex)
        {
            ConflictStatusMessage = $"Couldn't save the picks with '{name}': {ex.Message}";
        }
    }

    /// <summary>
    /// The snapshot's winners turned into the merge's per-field picks for its queue (a pick's
    /// position among the mods changing that value, counted the way Rebuild merges: see
    /// FindQueueConflictsAsync), plus how many saved winners for mods in this build no longer match
    /// a conflict (the value isn't changed differently any more, or that mod doesn't change it). A
    /// test's ManualPicksForTesting is used when nothing was picked.
    /// </summary>
    private async Task<(IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? Picks, int Unmatched)> ResolveWinnerPicksAsync(
        BuildSnapshot snapshot, LoadedQueue loaded)
    {
        if (snapshot.Winners.Count == 0)
        {
            return (snapshot.ManualPicks, 0);
        }

        var found = await FindQueueConflictsAsync(snapshot, loaded);
        var picks = new Dictionary<(string CurrentFile, string ItemName, string FieldName), int>(FieldChangeKeyComparer.Instance);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var conflict in found.Conflicts)
        {
            var key = MergeConflictsViewModel.WinnerKey(conflict.CurrentFile, conflict.ItemName, conflict.FieldName);
            if (snapshot.Winners.TryGetValue(key, out var winner)
                && (found.NameOf(winner) ?? (found.IdOf(winner) is not null ? winner : null)) is { } name)
            {
                var index = conflict.Candidates.ToList().FindIndex(c => string.Equals(c.ModName, name, StringComparison.Ordinal));
                if (index >= 0)
                {
                    picks[(conflict.CurrentFile, conflict.ItemName, conflict.FieldName)] = index;
                    used.Add(key);
                }
            }
        }

        var unmatched = snapshot.Winners.Count(w => !used.Contains(w.Key) && (found.NameOf(w.Value) is not null || found.IdOf(w.Value) is not null));
        return (picks.Count > 0 ? picks : null, unmatched);
    }

    /// <summary>
    /// The advanced conflict picker's own entry point — computes which fields two or more queued
    /// mods change differently and, if any, opens a window to let the user pick a winner per field
    /// (or leave it on Default — usually last-mod-wins, but a combine rule instead unions every
    /// candidate's own value for some field shapes; see MergeConflictViewModel). Winners are saved
    /// with the profile by mod (folder) id and used by every later build.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ReviewConflictsAsync()
    {
        var snapshot = TakeSnapshot();
        if (snapshot.Queue.Count == 0)
        {
            ConflictStatusMessage = "Add mods to the queue first.";
            return;
        }

        try
        {
            var found = await FindQueueConflictsAsync(snapshot, await LoadQueuedPackagesAsync(snapshot.Queue));
            var (conflicts, newItemCollisions, modNames) = (found.Conflicts, found.NewItemCollisions, found.Names);
            // The window and the bar count from the same tally (the window's own), so they agree.
            var shownWinners = ToDisplayWinners(snapshot.Winners, found);
            var conflictsViewModel = new MergeConflictsViewModel(conflicts, newItemCollisions, shownWinners);
            ApplyConflicts(found);

            if (conflicts.Count == 0 && newItemCollisions.Count == 0)
            {
                _manualPicks = null;
                ConflictStatusMessage = $"No conflicts among {modNames.Count} mod(s) — every changed field is touched by only one mod, or they all agree.";
                return;
            }

            var collisionNote = newItemCollisions.Count > 0 ? $" ({newItemCollisions.Count} same-name new item(s) combined automatically)" : "";
            _activityLog.Log($"{conflicts.Count} conflict(s){collisionNote} found among {modNames.Count} queued mod(s).", ActivityEntryKind.Warning);

            var window = new MergeConflictsWindow(conflictsViewModel) { Owner = Application.Current.MainWindow };
            if (window.ShowDialog() == true)
            {
                _winners = FromDisplayWinners(window.ResultWinners!, snapshot.Winners, shownWinners, found);
                OnPropertyChanged(nameof(SavedWinnerCount));
                SaveWinnersToProfile();
                var pickedNow = conflictsViewModel.PickedCount;
                ConflictStatusMessage = pickedNow > 0
                    ? $"{conflicts.Count} conflict(s){collisionNote}, {pickedNow} won by your pick (saved with the profile). Merge & Install to apply."
                    // "Default" per line: the mod lower in the queue wins, or a list takes everyone's additions.
                    : $"{conflicts.Count} conflict(s){collisionNote}, all left on default.";
            }
        }
        catch (Exception ex)
        {
            // Same UI boundary as Rebuild itself — a malformed queued mod shouldn't crash the app.
            ConflictStatusMessage = $"Couldn't check for conflicts: {ex.Message}";
        }
    }

    /// <summary>
    /// Shared by ReviewValidationIssuesAsync and the background badge computation. Runs three
    /// checks over the queue, all answering the same real underlying question ("will this actually
    /// do something in-game") from a different angle, combined into one report rather than three
    /// near-identical warning strips in this page: ExmodFieldValidityChecker ("is this field
    /// name/type real", per mod), ExmodReferenceChecker ("does this reference point at something
    /// real", per mod), ExmodAssetCollisionChecker ("do two queued mods silently overwrite each
    /// other's binary asset", across the whole queue — unlike the other two, this one only makes
    /// sense computed once over every mod together, not mod-by-mod), and TalentTreeChecker (the
    /// talent/blueprint/workshop trees as the whole queue leaves them: nodes on top of each other,
    /// circles, nodes needing another tree's).
    ///
    /// Deliberately scoped to only the real EXMOD mods in the queue (LoadQueuedPackagesAsync's own
    /// Packages) — an attached prebuilt pak's own compiled DataTable JSON was already written by
    /// whatever tool built that pak, not authored here, and the built-in gameplay options write
    /// hardcoded, app-defined field names that already have their own dedicated "zero rows
    /// matched" warning in GameplayOptionsApplier, so re-checking either through this
    /// general-purpose path would be redundant, not a gap.
    /// </summary>
    private async Task<IReadOnlyList<ValidationIssueRowViewModel>> FindQueueValidationIssuesAsync()
    {
        var loaded = await LoadQueuedPackagesAsync();
        var (entries, packages) = (loaded.Entries, loaded.Packages);

        return await Task.Run(() =>
        {
            var schemaCache = new Dictionary<string, Dictionary<string, System.Text.Json.JsonValueKind>?>();

            // Layer EVERY queued mod's own declared rows into ONE combined index before checking
            // any of them — not just each package's own rows, which is all ExmodReferenceChecker.
            // Check's own internal WithDeclaredRows call does on its own. Without this, Mod B
            // referencing a row only Mod A (also queued) declares produced a false "broken
            // reference": the base-game index alone has no way to know about a row a DIFFERENT
            // queued mod is adding. Built once per validation pass, not once per package (the
            // IEnumerable<ExmodPackage> overload copies the base tables exactly once), so this
            // scales with the queue, not the queue squared.
            //
            // The REVERSE direction (a queued mod's own edit removing or renaming a row some OTHER
            // queued mod's reference depends on) does NOT need the same treatment — confirmed by
            // reading the format, not assumed: there is no mechanism anywhere in this pipeline by
            // which a queued mod can make that happen.
            //   - No row can be deleted at all. TableDiffer's own doc comment is explicit: "no
            //     invented row deletion semantics... there's no evidence that's a real modding
            //     pattern" — confirmed all the way through TableApplier.Apply too, which only ever
            //     creates a row or edits/removes a FIELD within one; nothing removes a row itself.
            //   - No row can be renamed either, because a row's identity IS its own "Name" key, and
            //     "Name" categorically cannot travel through the sparse EXMOD field-change format:
            //     ReservedFieldNames.EnsureFieldNameAllowed rejects it as an ordinary field outright
            //     (ExmodFieldChangeMapper.FromFieldChanges/ExmodJson.ToJsonObject both enforce this
            //     on write), and even a hand-crafted .EXMOD file that snuck a "Name" key into one
            //     File_Item's own object would have it silently skipped on read — see
            //     ExmodJson.ParseRow's own `if (key == "Name") continue;`.
            //   - A mod CAN remove an individual FIELD from a row it declares (FieldChange.
            //     IsFieldRemoved), but that never removes the row's own Name from THIS index —
            //     AddDeclaredRows below adds item.Name unconditionally, regardless of which fields
            //     that item does or doesn't carry — so a field-level edit from one queued mod can
            //     never make another queued mod's row-existence reference look broken.
            // Net: nothing a queued mod can actually express invalidates another queued mod's
            // reference, so there is no reverse-direction gap here to detect. (A row genuinely
            // renamed/removed by an upstream GAME PATCH is a real, different case — already handled
            // by TableApplier's own "item no longer exists in base data" warning at Rebuild time,
            // orthogonal to this queue-vs-queue check.)
            var queueWideReferenceIndex = GetOrBuildBaseDataTableIndex().WithDeclaredRows(packages.Select(p => p.Package));
            var rows = new List<ValidationIssueRowViewModel>();

            for (var i = 0; i < packages.Count; i++)
            {
                var modName = entries[i].Name;
                var package = packages[i].Package;

                foreach (var finding in ExmodFieldValidityChecker.Check(package, _dataFolder, schemaCache))
                {
                    rows.Add(new ValidationIssueRowViewModel(
                        "Field", modName, finding.CurrentFile.Replace('-', '/'), finding.ItemName, finding.FieldName, finding.Reason));
                }

                foreach (var finding in ExmodReferenceChecker.Check(package, _dataFolder, queueWideReferenceIndex))
                {
                    rows.Add(new ValidationIssueRowViewModel(
                        "Reference", modName, finding.CurrentFile.Replace('-', '/'), finding.ItemName, finding.FieldPath, finding.Reason));
                }
            }

            var queuedForCollisionCheck = entries.Zip(packages, (entry, package) => (entry.Name, package)).ToList();
            foreach (var collision in ExmodAssetCollisionChecker.Check(queuedForCollisionCheck))
            {
                // Last-in-queue-order wins on a literal asset path collision — same convention
                // RebuildService's own raw-copy-through pass already uses for field conflicts.
                var reason = collision.AreIdentical
                    ? $"{collision.ModNames.Count} queued mods bundle this exact same file (byte-identical) — harmless, but worth knowing."
                    : $"{collision.ModNames.Count} queued mods bundle DIFFERENT versions of this file — '{collision.ModNames[^1]}' silently wins, the others are discarded.";
                rows.Add(new ValidationIssueRowViewModel(
                    "Asset collision", string.Join(", ", collision.ModNames), collision.RelativePath, "—", "—", reason));
            }

            // The talent, blueprint and workshop trees as the whole queue leaves them (later mods
            // win, as in the merge): nodes on top of each other, circles, another tree's nodes.
            foreach (var finding in TalentTreeChecker.Check([.. queuedForCollisionCheck.Select(q => (q.Name, q.package.Package))], new GameTableIndex(_dataFolder)))
            {
                var field = finding.Kind switch
                {
                    TalentTreeChecker.FindingKind.Overlap => "Position",
                    TalentTreeChecker.FindingKind.MissingTree => "TalentTree",
                    _ => "RequiredTalents",
                };
                rows.Add(new ValidationIssueRowViewModel(
                    "Talent tree", string.Join(", ", finding.Mods), "Talents/D_Talents.json", finding.Node, field, finding.Reason));
            }

            return (IReadOnlyList<ValidationIssueRowViewModel>)rows;
        });
    }

    /// <summary>Guards RecomputeValidationIssueCountAsync the same way _conflictCountVersion guards its own sibling — see that field's own comment for why.</summary>
    private int _validationIssueCountVersion;

    /// <summary>Best-effort background refresh of ValidationIssueCount — a malformed queued mod here just leaves the badge at its last value; ReviewValidationIssuesAsync surfaces the real error when the user actually acts. Internal (not private) for the same test-seam reason as RecomputeConflictCountAsync — see IsRecomputeDebouncePending's own doc comment.</summary>
    internal async Task RecomputeValidationIssueCountAsync()
    {
        var version = ++_validationIssueCountVersion;

        if (Queue.Count == 0)
        {
            ValidationIssueCount = 0;
            return;
        }

        try
        {
            var rows = await FindQueueValidationIssuesAsync();
            if (version == _validationIssueCountVersion)
            {
                ValidationIssueCount = rows.Count;
            }
        }
        catch (Exception)
        {
            // Left at its last value on purpose — same reasoning as RecomputeConflictCountAsync.
        }
    }

    /// <summary>
    /// Checks every queued mod's own declared fields and references against the currently-
    /// extracted real game data and opens a read-only report of anything that looks wrong — a
    /// field name that isn't real (typo, or renamed/removed by a game update), a value whose JSON
    /// type doesn't match what that field normally holds, or a reference (e.g. a recipe input)
    /// pointing at a row that doesn't exist. None of these make the merge or the pak build fail;
    /// Icarus's own importer just silently ignores what it doesn't recognize, so this is the one
    /// place this app can tell you before you install that a change is quietly going to do nothing
    /// in-game.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task ReviewValidationIssuesAsync()
    {
        if (Queue.Count == 0)
        {
            ValidationStatusMessage = "Add mods to the queue first.";
            return;
        }

        try
        {
            var rows = await FindQueueValidationIssuesAsync();
            ValidationIssueCount = rows.Count;

            if (rows.Count == 0)
            {
                ValidationStatusMessage = $"No issues found among {Queue.Count} queued mod(s).";
                return;
            }

            _activityLog.Log($"{rows.Count} validation issue(s) found among {Queue.Count} queued mod(s).", ActivityEntryKind.Warning);
            ValidationStatusMessage = $"{rows.Count} issue(s) found — see the report window.";

            var window = new ValidationIssueReportWindow(rows) { Owner = Application.Current.MainWindow };
            window.Show();
        }
        catch (Exception ex)
        {
            // Same UI boundary as ReviewConflictsAsync — a malformed queued mod shouldn't crash the app.
            ValidationStatusMessage = $"Couldn't validate mods: {ex.Message}";
        }
    }

    /// <summary>Whether Icarus is running; a failing process query counts as not running.</summary>
    private bool IsGameRunning()
    {
        try
        {
            return _gameProcessChecker?.IsRunning() == true;
        }
        catch (Exception)
        {
            // A process query failing must never block an install on its own.
            return false;
        }
    }

    /// <summary>How an install ended: in the game, not done (the user said No, or the game started meanwhile), or failed. The reason is in InstallStatusMessage.</summary>
    internal enum InstallOutcome
    {
        Installed,
        NotDone,
        Failed,
    }

    /// <summary>
    /// The one method in this whole app that writes into the real game's Content\Paks\mods.
    /// No [RelayCommand] — reached through RebuildAndInstallAsync's chain (the Install button),
    /// Copy to game (the staged pak again, without rebuilding) and Update everything.
    /// </summary>
    internal async Task<InstallOutcome> InstallAsync()
    {
        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            InstallStatusMessage = "Set the Icarus Content folder in Settings first.";
            return InstallOutcome.Failed;
        }

        if (!HasCompleteStagedBuild())
        {
            InstallStatusMessage = "Nothing built yet — click Install to build one.";
            return InstallOutcome.Failed;
        }

        // The same guard the saves editor and the UE4SS tab already have, and the one place it was
        // missing: Icarus holds its Paks folder open while running, so a pak copied under it is the
        // one way a user can leave their game with a half-replaced mod pack. Checked here and again
        // right before the copy, since the confirm dialog gives the game time to start.
        if (IsGameRunning())
        {
            InstallStatusMessage = "Icarus is running — close it before installing, or the game keeps loading the old pack and the copy can fail halfway.";
            return InstallOutcome.NotDone;
        }

        // Content\Paks\mods is meant to hold exactly one active merged pak — named here rather than
        // a bare "overwriting whatever's currently installed" claim, since that claim used to be
        // false: InstallService only ever touched its own two specifically-named files, silently
        // leaving anything else (a classic IMM merged pak, a stray leftover) loaded alongside the
        // new one. Matches the same "name every file before deleting it" transparency the FTP
        // "Install merged pak to server" action already established.
        var modsDirectory = Path.Combine(_settingsService.Current.IcarusContentPath!, "Paks", "mods");
        var targetPakFileName = Path.GetFileName(_outputPakPath);

        // ArcFission Library's data pak is kept (its rows are in the merge, and the library would
        // only write it again) — unless the library itself is gone or off: then nothing rewrites it,
        // and it holds whole tables from an older game, so removing it is offered.
        var leftoverArcFission = LeftoverArcFissionDataPaks(_settingsService.Current.IcarusContentPath!);
        var keepArcFission = leftoverArcFission.Count == 0 || !Confirm(
            $"ArcFission Library isn't installed (or is turned off), but its data pak is still in the mods folder:\n"
            + string.Join('\n', leftoverArcFission.Select(n => $"  - {n}"))
            + "\n\nNothing updates it any more: it holds whole game tables from the game version it was written for, so it can put old values back in game.\n\n"
            + "Back it up and remove it?",
            "Leftover ArcFission data");

        var otherFilesToClear = Directory.Exists(modsDirectory)
            ? Directory.GetFiles(modsDirectory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(name => !string.Equals(name, targetPakFileName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, InstallManifestNames.PakManifest, StringComparison.OrdinalIgnoreCase)
                    && !(keepArcFission && InstallManifestNames.IsArcFissionDataPak(name)))
                .ToList()
            : [];
        var clearNote = otherFilesToClear.Count > 0
            ? $"\n\nIt will also remove {otherFilesToClear.Count} other file(s) already there (backed up first, not part of this merge):\n"
                + string.Join('\n', otherFilesToClear.Select(n => $"  - {n}"))
            : "";

        // Same explicit Yes/No gate as every other real-machine write this app makes (the nxm://
        // registry registration, the UE4SS loader install) — this one specifically overwrites the
        // user's actual installed mod pack, so it shouldn't ever be reachable via a single
        // accidental click.
        if (!Confirm(
            $"This copies the staged pak into '{_settingsService.Current.IcarusContentPath}\\Paks\\mods', overwriting whatever's currently installed there.\n\n" +
            $"The existing pak is backed up first (last {BackupCleanup.MergedPakBackupsKept} kept).{clearNote}\n\nContinue?",
            "Install to Icarus"))
        {
            InstallStatusMessage = "Not installed: you chose not to.";
            return InstallOutcome.NotDone;
        }

        if (IsGameRunning())
        {
            InstallStatusMessage = "Icarus started while the confirmation was open — close it, then Install again.";
            return InstallOutcome.NotDone;
        }

        InstallStatusMessage = "Installing…";

        try
        {
            using var perfScope = _performanceTracker.Track("Install");
            var result = await _installService.InstallAsync(
                _outputPakPath, _settingsService.Current.IcarusContentPath!, _backupDirectory, keepArcFissionDataPak: keepArcFission);

            // Also done right after a successful RebuildAsync now, so this is redundant when
            // reached through the combined Install button (nothing about the pak's own content
            // changes between the two) — but InstallAsync is also reachable on its own via Copy to
            // game, without a fresh Rebuild immediately before it, where this is the only thing that
            // keeps the Library entry current. The pak is in the game by now: a failure here (a locked
            // Library folder) is a note, not "Install failed" with the install left unrecorded.
            string? libraryNote = null;
            try
            {
                ImportBuiltPakIntoLibrary(SelectedProfileName ?? "Default");
            }
            catch (Exception ex)
            {
                libraryNote = $" The built pack couldn't be added to your Library ({ex.Message}).";
            }

            InstallStatusMessage = (result.BackupPakPath is not null
                ? $"Installed to '{result.InstalledPakPath}'. Backed up the previous pak to '{result.BackupPakPath}'."
                : $"Installed to '{result.InstalledPakPath}'.") + libraryNote;
            _activityLog.Log($"Installed pack to {_settingsService.Current.IcarusContentPath}\\Paks\\mods.", ActivityEntryKind.Success);
            HasExistingInstall = true;
            _ = RefreshInstalledModNamesAsync();

            // Read back on the NEXT app launch (SettingsViewModel) to check whether a real game
            // crash happened since — see AppSettings.LastInstallAtUtc's own doc comment for why.
            _settingsService.Current.LastInstallAtUtc = DateTimeOffset.UtcNow;
            _settingsService.Save();
            return InstallOutcome.Installed;
        }
        catch (IOException ex)
        {
            // Overwhelmingly the "Icarus is still running" case, which is worth naming rather than
            // showing a raw file-sharing error — and it's exactly when Copy to game (no rebuild)
            // is the right next click, so offer it.
            InstallStatusMessage =
                $"Install failed — the pak is in use: {ex.Message} "
                + "Close Icarus if it's running, then click Copy to game under Advanced actions (no need to rebuild).";
            _activityLog.Log(InstallStatusMessage, ActivityEntryKind.Warning);
            CanCopyToGame = true;
            return InstallOutcome.Failed;
        }
        catch (Exception ex)
        {
            // Same UI boundary as everywhere else — a missing/wrong Content path or a permissions
            // issue should show a status message, not crash the app.
            InstallStatusMessage = $"Install failed: {ex.Message}";
            _activityLog.Log(InstallStatusMessage, ActivityEntryKind.Warning);
            return InstallOutcome.Failed;
        }
    }

    /// <summary>
    /// Copy to game, Remove from game and Restore previous install while a build runs: refused with
    /// a reason (true). The build is rewriting the staged pak, so Copy to game could put a half-written
    /// pak in the game, and the other two would be undone by the install that follows the build.
    /// </summary>
    private bool RefuseInstallWhileRebuilding()
    {
        if (!IsRebuilding)
        {
            return false;
        }

        InstallStatusMessage = "Merge & Install is building — wait for it to finish first.";
        return true;
    }

    /// <summary>Shown once an install has failed on a locked file, or whenever a staged pak already exists — the spec's own "Copy built pack to game" / Retry affordance.</summary>
    [ObservableProperty]
    private bool _canCopyToGame;

    /// <summary>
    /// The spec's "Copy built pack to game": installs the already-staged pak WITHOUT remerging.
    /// Mechanically the same as Install's second half — it shares InstallAsync rather than
    /// duplicating it — but reachable on its own, which matters in the case it exists for: the
    /// game held the file open, nothing about the staged pak changed, and rebuilding it again to
    /// retry the copy is pure waste.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task CopyToGameAsync()
    {
        if (RefuseInstallWhileRebuilding())
        {
            return;
        }

        if (!HasCompleteStagedBuild())
        {
            InstallStatusMessage = "Nothing built yet — click Install to build one.";
            return;
        }

        await InstallAsync();
    }

    /// <summary>
    /// A standalone uninstall — deletes the real installed pak from Content\Paks\mods without
    /// Installing anything in its place. Deliberately leaves the Library entry Install's own
    /// "Replace, not accumulate" step created untouched: removing a mod from the live game isn't
    /// the same request as deleting it from the local library, so this only ever touches the real
    /// game folder.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task RemoveFromGameAsync()
    {
        if (RefuseInstallWhileRebuilding())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            InstallStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        // Icarus holds its paks open while it runs: the same guard Install has, before and after the
        // confirmation (which gives the game time to start).
        if (IsGameRunning())
        {
            InstallStatusMessage = "Icarus is running — close it before removing the pack, or the delete can fail halfway.";
            return;
        }

        // Same explicit Yes/No gate InstallAsync itself already uses — this deletes a real file
        // from the user's actual game folder, so it shouldn't be reachable via a single accidental
        // click any more than overwriting it already isn't.
        if (!Confirm(
            $"This removes IcarusStarlink's installed pak from '{_settingsService.Current.IcarusContentPath}\\Paks\\mods'.\n\n" +
            $"It's backed up first (last {BackupCleanup.MergedPakBackupsKept} kept), and your Library copy of this mod isn't touched — only the real game install.\n\nContinue?",
            "Remove from Icarus"))
        {
            return;
        }

        if (IsGameRunning())
        {
            InstallStatusMessage = "Icarus started while the confirmation was open — close it, then Remove again.";
            return;
        }

        InstallStatusMessage = "Removing…";

        try
        {
            var removed = await _installService.RemoveInstalledPakAsync(
                Path.GetFileName(_outputPakPath), _settingsService.Current.IcarusContentPath!, _backupDirectory);

            InstallStatusMessage = removed
                ? "Removed from Icarus — backed up first."
                : "Nothing installed by IcarusStarlink was there to remove.";
            if (removed)
            {
                _activityLog.Log($"Removed installed pack from {_settingsService.Current.IcarusContentPath}\\Paks\\mods.", ActivityEntryKind.Success);
                HasExistingInstall = false;
            }

            // The manifest is gone too, so the Library's "In game" status has to drop with it.
            await RefreshInstalledModNamesAsync();
        }
        catch (Exception ex)
        {
            InstallStatusMessage = $"Remove failed: {ex.Message}";
            _activityLog.Log(InstallStatusMessage, ActivityEntryKind.Warning);
        }
    }

    /// <summary>
    /// Puts the install before the current one back: the newest merged pak backup and the mod list
    /// backed up with it (Install and Remove back both up first). What's installed now is backed up
    /// first, so running this again undoes it.
    /// </summary>
    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
    private async Task RestorePreviousInstallAsync()
    {
        if (RefuseInstallWhileRebuilding())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_settingsService.Current.IcarusContentPath))
        {
            InstallStatusMessage = "Set the Icarus Content folder in Settings first.";
            return;
        }

        if (IsGameRunning())
        {
            InstallStatusMessage = "Icarus is running — close it before restoring an earlier install.";
            return;
        }

        try
        {
            if (await _installService.FindPreviousInstallAsync(_backupDirectory) is not { } previous)
            {
                InstallStatusMessage = "There's no earlier install backed up to restore.";
                return;
            }

            var listNote = previous.ManifestBackupPath is null
                ? "\n\nIts mod list wasn't backed up with it, so the Library won't show which mods are in the game until the next Merge & Install."
                : "";
            if (!Confirm(
                $"This puts back the merged pak that was installed before (backed up {previous.BackedUpAtUtc.ToLocalTime():g}), with its mod list.{listNote}\n\n"
                + "What's installed now is backed up first, so you can switch back the same way.\n\nContinue?",
                "Restore previous install"))
            {
                return;
            }

            if (IsGameRunning())
            {
                InstallStatusMessage = "Icarus started while the confirmation was open — close it, then try again.";
                return;
            }

            InstallStatusMessage = "Restoring…";
            var restored = await _installService.RestorePreviousInstallAsync(
                Path.GetFileName(_outputPakPath), _settingsService.Current.IcarusContentPath!, _backupDirectory);
            if (restored is null)
            {
                InstallStatusMessage = "There's no earlier install backed up to restore.";
                return;
            }

            InstallStatusMessage = $"Restored the install from {restored.BackedUpAtUtc.ToLocalTime():g}. What was installed is backed up.";
            _activityLog.Log($"Restored the previous install ({Path.GetFileName(restored.PakBackupPath)}).", ActivityEntryKind.Success);
            HasExistingInstall = true;
            await RefreshInstalledModNamesAsync();
        }
        catch (Exception ex)
        {
            InstallStatusMessage = $"Restore failed: {ex.Message}";
            _activityLog.Log(InstallStatusMessage, ActivityEntryKind.Warning);
        }
    }
}

/// <summary>
/// The Stacks/Slots multiplier pills' own fixed values — a UI-only concept, not part of
/// GameplayOptions itself (that stays a free `double?`, since a hand-edited profile or an imported
/// patch could carry any positive multiplier, not just one of these five presets). A real enum,
/// not a `double?` list with null="Off": WPF's Selector treats SelectedItem==null as "nothing
/// selected" rather than "the null entry is selected", so a nullable-double pill never showed Off
/// as highlighted on load — a real bug, found live, that this enum sidesteps entirely by giving
/// "Off" a real, non-null value like every other pill selector on this page already has.
/// </summary>
public enum MultiplierLevel
{
    Off,
    Times2,
    Times3,
    Times4,
    Times5,
    Times10,
}

public static class MultiplierLevelExtensions
{
    public static double? ToMultiplier(this MultiplierLevel level) => level switch
    {
        MultiplierLevel.Times2 => 2,
        MultiplierLevel.Times3 => 3,
        MultiplierLevel.Times4 => 4,
        MultiplierLevel.Times5 => 5,
        MultiplierLevel.Times10 => 10,
        _ => null,
    };

    /// <summary>The closest preset for a stored value — an exact match for anything this UI itself ever wrote. A value from outside the preset set (hand-edited JSON, an imported patch) falls back to Off rather than guessing a nearest match, since silently rounding someone's real number to a different one would be worse than just not pre-selecting a pill for it.</summary>
    public static MultiplierLevel FromMultiplier(double? value) => value switch
    {
        2 => MultiplierLevel.Times2,
        3 => MultiplierLevel.Times3,
        4 => MultiplierLevel.Times4,
        5 => MultiplierLevel.Times5,
        10 => MultiplierLevel.Times10,
        _ => MultiplierLevel.Off,
    };
}
