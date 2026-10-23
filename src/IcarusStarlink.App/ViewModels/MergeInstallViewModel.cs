using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Patches;
using IcarusStarlink.PakIO.Rebuild;

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

    /// <summary>
    /// The talent-tree check's game tables (GameTableIndex reads and parses each table once, then
    /// keeps it). Made fresh on every validation pass before, so every queue change parsed the
    /// talent tables again. Same lifetime as _cachedBaseDataTableIndex, and let go with the
    /// package cache when the app sits idle.
    /// </summary>
    private GameTableIndex? _cachedGameTableIndex;

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
    private string? _statusMessage;

    public ObservableCollection<string> Warnings { get; } = [];

    // --- Mod validation ("will this actually do anything in-game") ---
    [ObservableProperty]
    private string? _validationStatusMessage;

    /// <summary>Proactive badge on the queue, recomputed in the background whenever Queue changes — same shape as ConflictCount, but flags a mod setting a field name/type or a reference that isn't real in the currently-extracted game data (the merge and pak build both succeed either way, but the game's own importer silently ignores what it doesn't recognize, so the edit does nothing in-game).</summary>
    [ObservableProperty]
    private int _validationIssueCount;

    public bool HasValidationIssues => ValidationIssueCount > 0;

    partial void OnValidationIssueCountChanged(int value) => OnPropertyChanged(nameof(HasValidationIssues));

    private readonly IMessenger _messenger;

    /// <summary>Null only in tests that don't care; the app always wires the real checker — see InstallAsync.</summary>
    private readonly IGameProcessChecker? _gameProcessChecker;

    /// <summary>Null only in tests that don't care; the app always wires it — see RebuildAsync's game-update guard.</summary>
    private readonly IGameDataFreshness? _gameDataFreshness;

    /// <summary>Every question and file picker this page shows (tests pass a fake).</summary>
    private readonly IDialogService _dialogs;

    /// <summary>A Yes/No question (message, title) → Yes.</summary>
    private bool Confirm(string message, string title) => _dialogs.Confirm(message, title, ThemedConfirmSeverity.Question);

    public MergeInstallViewModel(
        ILibraryRepository libraryRepository, IRebuildService rebuildService, IInstallService installService, IProfileStore profileStore,
        IPatchService patchService, IDaedalusCatalogClient daedalusClient, IJimk72CatalogClient jimk72Client,
        ISettingsService settingsService, IPakCompareService pakCompareService, PerformanceTracker performanceTracker, IActivityLog activityLog,
        string dataFolder, string outputPakPath, string backupDirectory, IMessenger? messenger = null, IGameProcessChecker? gameProcessChecker = null,
        IGameDataFreshness? gameDataFreshness = null, IDialogService? dialogService = null)
    {
        IcarusStarlink.Core.Memory.IdleMemory.Register(this);
        _dialogs = dialogService ?? new WpfDialogService();

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
                viewModel._cachedGameTableIndex = null;
                // A prebuilt pak's changes are its diff against the game data, which just changed.
                viewModel._prebuiltPakChangesCache.Clear();
                viewModel._prebuiltPakChangesGeneration++;
            }

            viewModel.InvalidateManualPicks();
        });

        // Closing the app, an update or Refresh game data mid-build or mid-install: asked first.
        _messenger.Register<BackgroundWorkRequestMessage>(this, (recipient, message) =>
        {
            foreach (var work in ((MergeInstallViewModel)recipient).DescribeWorkInProgress())
            {
                message.Reply(work);
            }
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
            if ((_packageCache.Count == 0 && _prebuiltPakChangesCache.Count == 0 && _cachedGameTableIndex is null) || DateTime.UtcNow - _packageCacheUsedUtc < idleFor)
            {
                return false;
            }

            _packageCache.Clear();
            _prebuiltPakChangesCache.Clear();
            _cachedGameTableIndex = null;
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

    private GameTableIndex GetOrCreateGameTableIndex()
    {
        lock (_packageCacheLock)
        {
            _packageCacheUsedUtc = DateTime.UtcNow;
            return _cachedGameTableIndex ??= new GameTableIndex(_dataFolder);
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
            foreach (var finding in TalentTreeChecker.Check([.. queuedForCollisionCheck.Select(q => (q.Name, q.package.Package))], GetOrCreateGameTableIndex()))
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
}
