using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
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
        ["Biomass"] = "Biomass",
        ["Biomass_Converter"] = "Flux",
        ["Exotic_Uranium"] = "Uranium",
        ["Licence"] = "Licences",
        ["Refund"] = "Respec Points",
    };

    private readonly ISaveRepository _repository;
    private readonly IActivityLog _activityLog;
    /// <summary>SaveModSources.Read: where the save's mod rows come from (null: not labelled).</summary>
    private readonly Func<SaveModSources.Index>? _readModSources;

    /// <summary>PlayerGrowthReader.Read: the player's level cap and point curves as installed (null: not shown).</summary>
    private readonly Func<IcarusStarlink.PakIO.Assets.PlayerGrowth?>? _readPlayerGrowth;

    private IcarusStarlink.PakIO.Assets.PlayerGrowth? _playerGrowth;

    private readonly PlayerPointsMod? _pointsMod;
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
        Func<IcarusStarlink.PakIO.Assets.PlayerGrowth?>? readPlayerGrowth = null, PlayerPointsMod? pointsMod = null)
    {
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

    // --- Flags (character flags on the selected character; account flags on the profile) and the
    //     slot's flags_<SteamID>.dat, which is NOT flags: the game's cached list of owned DLC ---

    public ObservableCollection<SaveFlagViewModel> AccountFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> BinaryFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredCharacterFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredAccountFlags { get; } = [];

    public ObservableCollection<SaveFlagViewModel> FilteredBinaryFlags { get; } = [];

    /// <summary>The DLC section shows only when the game itself has created the file.</summary>
    public bool HasBinaryFlags => BinaryFlags.Count > 0;

    /// <summary>"21 of 28 owned".</summary>
    public string OwnedDlcSummary => $"{BinaryFlags.Count(f => f.IsUnlocked)} of {BinaryFlags.Count} owned";

    [ObservableProperty]
    private string _flagSearchText = "";

    partial void OnFlagSearchTextChanged(string value) => RefreshFlagFilter();

    // The Unlocks tab (was Flags), grouped by what each flag is for (SaveGameNames.FlagInfo,
    // researched 2026-10-23): only mission rewards and bonus talent points are worth editing;
    // markers and prompts sit under Advanced; flags nothing uses hide unless asked for.

    /// <summary>Mission rewards that unlock blueprints / workshop items (account).</summary>
    public ObservableCollection<SaveFlagViewModel> RewardFlags { get; } = [];

    /// <summary>Flags that give bonus talent points (account).</summary>
    public ObservableCollection<SaveFlagViewModel> TalentPointFlags { get; } = [];

    /// <summary>One-off markers and "has seen" prompts (account), and the character's own flags worth showing.</summary>
    public ObservableCollection<SaveFlagViewModel> AdvancedFlags { get; } = [];

    /// <summary>Deprecated, tooltip-only, test and never-granted flags — listed only with Show unused flags on.</summary>
    public ObservableCollection<SaveFlagViewModel> UnusedFlags { get; } = [];

    [ObservableProperty]
    private bool _showUnusedFlags;

    partial void OnShowUnusedFlagsChanged(bool value) => RefreshFlagFilter();

    /// <summary>How many unused flags are hidden, for the switch's label.</summary>
    public string ShowUnusedFlagsLabel => $"Show flags the game doesn't use ({AccountFlags.Concat(SelectedCharacter?.Flags ?? []).Count(f => f.Kind is FlagKind.Unused or FlagKind.Unknown)})";

    /// <summary>
    /// For each mission reward: the blueprints it gates that the selected character has already
    /// learned. With the flag off they still count in game (the user's report, 2026-10-23: rewards
    /// shown off here were unlocked on the in-game blueprint screen), so the row says so.
    /// </summary>
    private void UpdateLearnedNotes()
    {
        var learned = SelectedCharacter is { } character
            ? character.Talents.Where(t => t.Rank > 0).ToDictionary(t => t.RowName, t => t.DisplayName, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var flag in AccountFlags)
        {
            var have = _gameNames.TalentsGatedByFlag.GetValueOrDefault(flag.RowName, [])
                .Where(learned.ContainsKey)
                .Select(t => learned[t])
                .Distinct()
                .ToList();
            flag.LearnedNote = have.Count == 0 || SelectedCharacter is null
                ? null
                : $"Off in the save, but {SelectedCharacter.Name} already learned {string.Join(", ", have)} — the game shows those as unlocked.";
        }

        // DLC packs: the file is only the game's cache of what it last saw; the game also checks
        // DLC itself (Steam, or an unlocker), so a pack that's off here can still be on in game
        // (the user's report, 2026-10-23). The save's own content from a pack says so.
        var researched = WorkshopTalents.Where(t => t.Rank > 0).ToDictionary(t => t.RowName, t => t.DisplayName, StringComparer.OrdinalIgnoreCase);
        foreach (var pack in BinaryFlags)
        {
            var have = _gameNames.TalentsGatedByFlag.GetValueOrDefault(pack.RowName, [])
                .Select(t => learned.GetValueOrDefault(t) ?? researched.GetValueOrDefault(t))
                .OfType<string>()
                .Distinct()
                .ToList();
            pack.LearnedNote = have.Count == 0
                ? null
                : $"Off in the cache, but this save already has {string.Join(", ", have.Take(4))}{(have.Count > 4 ? $" and {have.Count - 4} more" : "")} — the game sees the pack itself.";
        }
    }

    private void RefreshFlagFilter()
    {
        // 45 + 100 rows — a straight rebuild per keystroke is cheaper than filter plumbing.
        RefreshFilter(SelectedCharacter?.Flags ?? [], FilteredCharacterFlags, FlagSearchText, null, f => f.Name);
        RefreshFilter(AccountFlags, FilteredAccountFlags, FlagSearchText, null, f => f.Name);
        RefreshFilter(BinaryFlags, FilteredBinaryFlags, FlagSearchText, null, f => f.Name);

        var characterFlags = SelectedCharacter?.Flags ?? [];
        UpdateLearnedNotes();
        RefreshFilter(AccountFlags, RewardFlags, FlagSearchText, f => f.Kind == FlagKind.Reward, f => f.Name, f => f.Detail);
        RefreshFilter(AccountFlags, TalentPointFlags, FlagSearchText, f => f.Kind == FlagKind.TalentPoints, f => f.Name, f => f.Detail);
        RefreshFilter(AccountFlags.Concat(characterFlags), AdvancedFlags, FlagSearchText,
            f => f.Kind is FlagKind.Marker or FlagKind.Prompt or FlagKind.FromTalent, f => f.Name, f => f.Detail);
        RefreshFilter(AccountFlags.Concat(characterFlags), UnusedFlags, FlagSearchText,
            f => ShowUnusedFlags && f.Kind is FlagKind.Unused or FlagKind.Unknown, f => f.Name, f => f.Detail);
        OnPropertyChanged(nameof(ShowUnusedFlagsLabel));
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

    /// <summary>The Talents tab's "Show": All, Talents (the Player trees), Blueprints, Solo.</summary>
    public IReadOnlyList<string> TalentKindOptions { get; } = ["All", "Talents", "Blueprints", "Solo"];

    [ObservableProperty]
    private string _talentKind = "All";

    partial void OnTalentKindChanged(string value) => RefreshTalentFilter();

    private bool MatchesTalentKind(SaveTalentViewModel talent) => TalentKind switch
    {
        "Talents" => talent.Model == "Player",
        "Blueprints" => talent.Model == "Blueprint",
        "Solo" => talent.Model == "Solo",
        _ => true,
    };

    // --- Points used and available (the user's ask, 2026-10-23: their own points are modded, so
    //     budgets come from the curves as installed, mods and Remove Level Cap included). ---

    private Task _pendingPlayerGrowth = Task.CompletedTask;

    internal Task WaitForPlayerGrowthAsync() => _pendingPlayerGrowth;

    private async Task ReadPlayerGrowthAsync()
    {
        if (_readPlayerGrowth is not { } read)
        {
            return;
        }

        try
        {
            _playerGrowth = await Task.Run(read);
        }
        catch (Exception)
        {
            _playerGrowth = null;
        }

        OnPropertyChanged(nameof(TalentRanksSummary));
        OnPropertyChanged(nameof(PointsNotes));
        OnPropertyChanged(nameof(HasPointsNotes));
        BuildPointsEditors();
        RefreshOverBudget();

        // Over-budget notes need the curves.
        RunSaveCheck();
    }

    // --- Change points per level: a "Starlink Player Points" Library mod (PlayerPointsMod). ---

    public ObservableCollection<PointsTreeEditor> PointsEditors { get; } = [];

    public bool CanChangePoints => _pointsMod is not null && PointsEditors.Count > 0;

    /// <summary>What happened last ("Made Starlink Player Points: ..."), or what's installed.</summary>
    [ObservableProperty]
    private string? _pointsModStatus;

    /// <summary>The Library folder of the mod just made, for "Turn on and go to Merge & Install".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TurnOnPointsModCommand))]
    private string? _pointsModFolder;

    private void BuildPointsEditors()
    {
        PointsEditors.Clear();
        if (_playerGrowth is { } growth)
        {
            void Add(string field, string label, IcarusStarlink.PakIO.Assets.GrowthCurve? installed)
            {
                if (growth.GameCurvesByField.GetValueOrDefault(field) is { } gameCurve)
                {
                    PointsEditors.Add(new PointsTreeEditor(field, label, gameCurve, installed, () => SelectedLevel, () => field == "TalentPointsPerLevel" ? TalentPointBonus : 0));
                }
            }

            Add("TalentPointsPerLevel", "Talents", growth.TalentPoints);
            Add("BlueprintPointsPerLevel", "Blueprints", growth.BlueprintPoints);
            Add("SoloPointsPerLevel", "Solo", growth.SoloPoints);
        }

        PointsModFolder = _pointsMod?.Existing?.FolderName;
        PointsModStatus = PointsModFolder is null ? null : $"{PlayerPointsMod.DisplayName} is in your Library.";
        OnPropertyChanged(nameof(CanChangePoints));
    }

    private void RefreshPointsEditors()
    {
        RefreshOverBudget();
        foreach (var editor in PointsEditors)
        {
            editor.Refresh();
        }
    }

    [RelayCommand]
    private void MakePointsMod()
    {
        if (_pointsMod is null || _playerGrowth is not { } growth)
        {
            return;
        }

        if (PointsEditors.FirstOrDefault(e => !e.IsValid) is { } bad)
        {
            PointsModStatus = $"{bad.Label}: enter a number above 0.";
            return;
        }

        try
        {
            var folder = _pointsMod.Build([.. PointsEditors.Select(e => new PointsCurveChange(e.Field, e.Multiplier, e.KeepRising))], growth);
            PointsModFolder = folder;
            WeakReferenceMessenger.Default.Send(new LibraryChangedMessage());
            PointsModStatus = folder is null
                ? "Everything matches the game's own points, so there's no mod to make (an old one was taken out of the Library)."
                : $"Made {PlayerPointsMod.DisplayName} in your Library. Turn it on in Merge & Install and rebuild; it counts in game, and here, once installed.";
            _activityLog.Log(PointsModStatus, ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            PointsModStatus = $"Couldn't make the mod: {ex.Message}";
            _activityLog.Log(PointsModStatus, ActivityEntryKind.Warning);
        }
    }

    private bool CanTurnOnPointsMod() => PointsModFolder is not null;

    [RelayCommand(CanExecute = nameof(CanTurnOnPointsMod))]
    private void TurnOnPointsMod()
    {
        if (PointsModFolder is { } folder)
        {
            WeakReferenceMessenger.Default.Send(new TurnOnModMessage(folder));
        }
    }

    /// <summary>The selected character's level, from its XP on the installed XP curve; null when it can't be worked out.</summary>
    private int? SelectedLevel => SelectedCharacter is { } character ? LevelOf(character) : null;

    private int? LevelOf(SaveCharacterViewModel character) =>
        _playerGrowth is { Experience: { } xpCurve } growth && long.TryParse(character.XpText, out var xp)
            ? xpCurve.LevelFor(xp, growth.MaxLevel > 0 ? growth.MaxLevel : int.MaxValue)
            : null;

    /// <summary>Each tree's ranks used and points available for a character; null when the curves couldn't be read.</summary>
    internal IReadOnlyList<(string Label, int Used, int Available)>? BudgetOf(SaveCharacterViewModel character)
    {
        if (LevelOf(character) is not { } level || _playerGrowth is not { } growth)
        {
            return null;
        }

        var budget = new List<(string, int, int)>();
        void Add(string label, string model, IcarusStarlink.PakIO.Assets.GrowthCurve? curve, int bonus)
        {
            if (curve is not null)
            {
                budget.Add((label, character.Talents.Where(t => t.Model == model).Sum(t => t.Rank), (int)Math.Floor(curve.Evaluate(level)) + bonus));
            }
        }

        Add("Talents", "Player", growth.TalentPoints, TalentPointBonus);
        Add("Blueprints", "Blueprint", growth.BlueprintPoints, 0);
        Add("Solo", "Solo", growth.SoloPoints, 0);
        return budget;
    }

    /// <summary>"Talents by 477, Solo by 15"; null when every tree is within its budget (or it can't be told).</summary>
    internal string? OverBudgetOf(SaveCharacterViewModel character) =>
        BudgetOf(character)?.Where(b => b.Used > b.Available).ToList() is { Count: > 0 } over
            ? string.Join(", ", over.Select(b => $"{b.Label} by {b.Used - b.Available:N0}"))
            : null;

    /// <summary>The banner on the Talents tab when the selected character has more ranks than points (the user's ask, 2026-10-23).</summary>
    public string? OverBudgetNote =>
        SelectedCharacter is { } character && OverBudgetOf(character) is { } over
            ? $"Over budget: {over}. The game keeps every rank you have, but those trees show no points left, so nothing more can be bought there in game until you're under. Raise the budget with Change points per level (How these points work, below), or lower ranks here."
            : null;

    private void RefreshOverBudget()
    {
        foreach (var character in Characters)
        {
            character.OverBudget = OverBudgetOf(character);
        }

        OnPropertyChanged(nameof(OverBudgetNote));
    }

    /// <summary>Bonus talent points from the account's mission rewards that are on (D_PlayerTalentModifiers).</summary>
    private int TalentPointBonus => AccountFlags.Where(f => f.IsUnlocked).Sum(f => _gameNames.TalentPointModifiers.GetValueOrDefault(f.RowName));

    private int RanksSpent(string model) => (SelectedCharacter?.Talents ?? []).Where(t => t.Model == model).Sum(t => t.Rank);

    /// <summary>"Level 313 · Talents 583 of 111 (over) · Blueprints 701 of 1,096 · Solo 45 of 30 (over)" for the selected character.</summary>
    public string TalentRanksSummary
    {
        get
        {
            if (SelectedLevel is not { } level || _playerGrowth is not { } growth)
            {
                return $"Ranks spent: Talents {RanksSpent("Player"):N0} · Blueprints {RanksSpent("Blueprint"):N0} · Solo {RanksSpent("Solo"):N0}";
            }

            string Part(string label, string model, IcarusStarlink.PakIO.Assets.GrowthCurve? curve, int bonus)
            {
                var used = RanksSpent(model);
                if (curve is null)
                {
                    return $"{label} {used:N0} used";
                }

                var available = (int)Math.Floor(curve.Evaluate(level)) + bonus;
                return $"{label} {used:N0} of {available:N0}{(used > available ? " (over)" : "")}";
            }

            return $"Level {level:N0} · {Part("Talents", "Player", growth.TalentPoints, TalentPointBonus)} · "
                + $"{Part("Blueprints", "Blueprint", growth.BlueprintPoints, 0)} · {Part("Solo", "Solo", growth.SoloPoints, 0)}";
        }
    }

    public bool HasPointsNotes => PointsNotes.Count > 0;

    /// <summary>Where the numbers above come from, in plain words.</summary>
    public IReadOnlyList<string> PointsNotes
    {
        get
        {
            if (SelectedLevel is not { } level || _playerGrowth is not { Experience: { } xpCurve } growth || SelectedCharacter is not { } character)
            {
                return [];
            }

            var notes = new List<string>();
            var xp = long.TryParse(character.XpText, out var parsed) ? parsed : 0;
            notes.Add($"Level {level:N0} comes from {xp:N0} XP. "
                + (xpCurve.SlopeAfter > 0 ? $"Past level {xpCurve.Last.Time:0} each level takes {xpCurve.SlopeAfter:N0} XP. " : "")
                + $"Levels go up to {growth.MaxLevel:N0}; the game's menus show up to {growth.MaxDisplayLevel:N0}."
                + (growth.IsStarlinkLevelCap ? " That's Starlink's Remove Level Cap (Merge & Install), which is installed."
                    : growth.LevelCapChanged ? $" An installed mod changes it (the game's own cap is {growth.GameMaxLevel:N0}); it isn't Starlink's Remove Level Cap."
                    : " That's the game's own cap: no installed mod changes it."));

            void Curve(string label, IcarusStarlink.PakIO.Assets.GrowthCurve? curve)
            {
                if (curve is null)
                {
                    return;
                }

                notes.Add(curve.SlopeAfter > 0
                    ? $"{label} keep rising past level {curve.Last.Time:0} ({curve.Last.Value:N0} there), {curve.SlopeAfter:0.##} a level."
                    : $"{label} stop rising at level {curve.Last.Time:0}: {curve.Last.Value:N0} at most.");
            }

            Curve("Talent points", growth.TalentPoints);
            Curve("Blueprint points", growth.BlueprintPoints);
            Curve("Solo points", growth.SoloPoints);
            if (TalentPointBonus > 0)
            {
                notes.Add($"+{TalentPointBonus} talent points from mission rewards that are on (Unlocks tab, bonus talent points).");
            }

            notes.Add(growth.CurvesFromMods.Count > 0
                ? $"From an installed mod: {string.Join(", ", growth.CurvesFromMods)}. The rest are the game's own."
                : "All four curves are the game's own; no installed mod changes them.");
            if (TalentRanksSummary.Contains("(over)", StringComparison.Ordinal))
            {
                notes.Add("Over: more ranks than the budget. The save keeps every rank and the game doesn't take any away; the tree just shows no points left to spend.");
            }

            return notes;
        }
    }

    private void RefreshTalentFilter()
    {
        // "Only what this character has" includes bDefaultUnlocked talents — the game grants those
        // from the start without writing them into the save, so rank alone under-reports.
        RefreshFilter(
            (SelectedCharacter?.Talents ?? []).OrderBy(t => t.Group, StringComparer.OrdinalIgnoreCase), FilteredTalents, TalentSearchText,
            t => (ShowUnlearnedTalents || t.IsUnlockedInGame || t.IsDirty) && MatchesTalentKind(t),
            t => t.DisplayName, t => t.RowName, t => t.Group);
        OnPropertyChanged(nameof(TalentRanksSummary));
        OnPropertyChanged(nameof(PointsNotes));
        OnPropertyChanged(nameof(HasPointsNotes));
        RefreshPointsEditors();
    }

    // --- Workshop research and Missions: both live in the profile's Talents list (WorkshopTalents
    //     holds them all and is what Save writes); the tabs show its Workshop and Prospect parts. ---

    public ObservableCollection<SaveTalentViewModel> FilteredWorkshop { get; } = [];

    public ObservableCollection<SaveTalentViewModel> FilteredMissions { get; } = [];

    [ObservableProperty]
    private string _workshopSearchText = "";

    [ObservableProperty]
    private string _missionSearchText = "";

    partial void OnWorkshopSearchTextChanged(string value) => RefreshAccountTalentFilters();

    partial void OnMissionSearchTextChanged(string value) => RefreshAccountTalentFilters();

    private static bool IsMission(SaveTalentViewModel t) => t.Model == "Prospect" || t.RowName.StartsWith("Prospect_", StringComparison.OrdinalIgnoreCase);

    /// <summary>The developers' own test missions (the Prospect_Dev tree): kept in the save, never listed.</summary>
    private static bool IsDevMission(SaveTalentViewModel t) => t.Tree.Equals("Prospect_Dev", StringComparison.OrdinalIgnoreCase) && t.Rank == 0;

    public string WorkshopSummary
    {
        get
        {
            var research = WorkshopTalents.Where(t => !IsMission(t)).ToList();
            return $"{research.Count(t => t.Rank > 0)} of {research.Count} researched";
        }
    }

    public string MissionsSummary
    {
        get
        {
            var missions = WorkshopTalents.Where(t => IsMission(t) && !IsDevMission(t)).ToList();
            return $"{missions.Count(t => t.Rank > 0)} of {missions.Count} done";
        }
    }

    private void RefreshAccountTalentFilters()
    {
        RefreshFilter(WorkshopTalents.Where(t => !IsMission(t)).OrderBy(t => t.Group, StringComparer.OrdinalIgnoreCase), FilteredWorkshop, WorkshopSearchText, null,
            t => t.DisplayName, t => t.RowName, t => t.Group);
        RefreshFilter(WorkshopTalents.Where(t => IsMission(t) && !IsDevMission(t)).OrderBy(t => t.Group, StringComparer.OrdinalIgnoreCase), FilteredMissions, MissionSearchText, null,
            t => t.DisplayName, t => t.RowName, t => t.Group);
        OnPropertyChanged(nameof(WorkshopSummary));
        OnPropertyChanged(nameof(MissionsSummary));
    }

    partial void OnSelectedCharacterChanged(SaveCharacterViewModel? value)
    {
        RefreshFlagFilter();
        RefreshTalentFilter();
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
            var files = await Task.Run(() => (
                Stamps: TryLoad(() => _repository.GetFileStamps(steamId)),
                Profile: _repository.LoadProfile(steamId),
                Characters: TryLoad(() => _repository.LoadCharacters(steamId)),
                Accolades: TryLoad(() => _repository.LoadAccolades(steamId)),
                Bestiary: TryLoad(() => _repository.LoadBestiary(steamId)),
                MetaInventory: TryLoad(() => _repository.LoadMetaInventory(steamId)),
                Mounts: TryLoad(() => _repository.LoadMounts(steamId))));

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
        ApplyPackNotes();
        RunSaveCheck();
        await worldsLoad;
    }

    /// <summary>Marks every talent and research row that's for a DLC or the Supporters Edition (the user's ask, 2026-10-23).</summary>
    private void ApplyPackNotes()
    {
        var rules = _gameNames.TalentRules;
        foreach (var talent in Characters.SelectMany(c => c.Talents).Concat(WorkshopTalents))
        {
            var packs = (rules.GetValueOrDefault(talent.RowName)?.RequiredFlags ?? [])
                .Where(f => f.Table == "D_DLCPackageData")
                .Select(f => _gameNames.DlcPackLabel(f.Row))
                .Distinct()
                .ToList();
            talent.PackNote = packs.Count == 0 ? null : $"For {string.Join(" and ", packs)}";
        }
    }

    // --- Save check (plan step 3): SaveChecker's findings on the Overview tab, each sure fix one click. ---

    public ObservableCollection<SaveCheckFinding> CheckFindings { get; } = [];

    /// <summary>"1 problem · 4 warnings · 12 notes", or that nothing was found.</summary>
    [ObservableProperty]
    private string _checkSummary = "";

    /// <summary>Fix all also turns on the DLC / Supporters Edition packs the notes name (the user's ask, 2026-10-23). Off by default.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFixableFindings))]
    private bool _fixAllIncludesDlc;

    private bool IsInFixAll(SaveCheckFinding f) => f.CanFix && (f.Severity != SaveCheckSeverity.Note || (FixAllIncludesDlc && f.IsDlc));

    public bool HasFixableFindings => CheckFindings.Any(IsInFixAll);

    [RelayCommand]
    private void RunSaveCheck()
    {
        CheckFindings.Clear();
        if (_loadFailed || SelectedSlot is null)
        {
            CheckSummary = "";
            OnPropertyChanged(nameof(HasFixableFindings));
            return;
        }

        foreach (var finding in SaveChecker.Check(this, _gameNames, SelectedSlot.FolderPath))
        {
            CheckFindings.Add(finding);
        }

        var parts = new[] { (SaveCheckSeverity.Problem, "problem"), (SaveCheckSeverity.Warning, "warning"), (SaveCheckSeverity.Note, "note") }
            .Select(x => (Count: CheckFindings.Count(f => f.Severity == x.Item1), Word: x.Item2))
            .Where(x => x.Count > 0)
            .Select(x => $"{x.Count} {x.Word}{(x.Count == 1 ? "" : "s")}");
        CheckSummary = CheckFindings.Count == 0 ? "Nothing found: the save looks consistent." : string.Join(" · ", parts);
        OnPropertyChanged(nameof(HasFixableFindings));
    }

    [RelayCommand]
    private void FixFinding(SaveCheckFinding? finding)
    {
        if (finding?.Fix is not { } fix)
        {
            return;
        }

        fix();
        _activityLog.Log($"Save check: {finding.FixLabel} (press Save to keep it).", ActivityEntryKind.Success);
        RunSaveCheck();
    }

    /// <summary>Every sure fix for a problem or warning, plus the DLC notes with their switch on (other notes stay a choice), after one confirm listing them.</summary>
    [RelayCommand]
    private void FixAllFindings()
    {
        var fixes = CheckFindings.Where(IsInFixAll).ToList();
        if (fixes.Count == 0)
        {
            return;
        }

        if (!_dialogService.Confirm(
                $"These {fixes.Count} fixes are made in the editor; nothing is written until you press Save:\n\n"
                + string.Join("\n", fixes.Take(20).Select(f => $"• {f.Area}: {f.FixLabel}"))
                + (fixes.Count > 20 ? $"\n• and {fixes.Count - 20} more" : ""),
                "Fix what the save check found?", ThemedConfirmSeverity.Question))
        {
            return;
        }

        foreach (var finding in fixes)
        {
            finding.Fix!();
        }

        _activityLog.Log($"Save check: made {fixes.Count} fixes (press Save to keep them).", ActivityEntryKind.Success);
        RunSaveCheck();
    }

    // --- Leftovers from mods (the user's ask, 2026-10-23): every talent, research, mission and
    //     banked item the game doesn't have is labelled with where it comes from (SaveModSources),
    //     and the ones no installed or Library mod gives can be taken out. Taking them out is an
    //     ordinary edit (rank 0 isn't written; an item is dropped), saved with Save like any other. ---

    private Task _pendingModSources = Task.CompletedTask;

    /// <summary>Test seam: the background read of where mod rows come from.</summary>
    internal Task WaitForModSourcesAsync() => _pendingModSources;

    private async Task ClassifyModRowsAsync()
    {
        if (_readModSources is not { } read)
        {
            return;
        }

        SaveModSources.Index index;
        try
        {
            index = await Task.Run(read);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var talent in Characters.SelectMany(c => c.Talents).Concat(WorkshopTalents))
        {
            talent.Source = index.Classify(SaveModSources.TalentsFile, talent.RowName);
        }

        foreach (var item in MetaInventoryItems)
        {
            item.Source = index.Classify(SaveModSources.ItemsFile, item.RowName);
        }

        NotifyLeftoversChanged();

        // Mis-named items are only known now.
        RunSaveCheck();
    }

    private IEnumerable<(string Where, SaveTalentViewModel Talent)> LeftoverTalents() =>
        Characters.SelectMany(c => c.Talents.Select(t => (Where: c.Name, Talent: t)))
            .Concat(WorkshopTalents.Select(t => (Where: IsMission(t) ? "Missions" : "Workshop", Talent: t)))
            .Where(x => x.Talent.Rank > 0 && x.Talent.Source?.IsRemovable == true);

    private IEnumerable<SaveInventoryItemViewModel> LeftoverItems() => MetaInventoryItems.Where(i => i.Source?.IsRemovable == true);

    public bool HasLeftovers => LeftoverTalents().Any() || LeftoverItems().Any();

    /// <summary>"MKHATERS: 14 talents · Workshop: 30 · Items: 2".</summary>
    public string LeftoversSummary =>
        string.Join(" · ", LeftoverTalents().GroupBy(x => x.Where).Select(g => $"{g.Key}: {g.Count()}")
            .Concat(LeftoverItems().Count() is > 0 and var items ? [$"Items: {items}"] : []));

    /// <summary>What the installed mods add that this save uses: "Ferani Buyable Beasts".</summary>
    public string? ModsInUseSummary
    {
        get
        {
            var inUse = Characters.SelectMany(c => c.Talents).Concat(WorkshopTalents).Where(t => t.Rank > 0).Select(t => t.Source)
                .Concat(MetaInventoryItems.Select(i => i.Source))
                .Count(s => s?.Origin is SaveRowOrigin.InstalledMod or SaveRowOrigin.LibraryModNotInstalled);
            return inUse == 0 ? null : $"{inUse} more come from mods you have, and stay.";
        }
    }

    private void NotifyLeftoversChanged()
    {
        OnPropertyChanged(nameof(HasLeftovers));
        OnPropertyChanged(nameof(LeftoversSummary));
        OnPropertyChanged(nameof(ModsInUseSummary));
    }

    [RelayCommand]
    private void RemoveLeftovers()
    {
        var talents = LeftoverTalents().ToList();
        var items = LeftoverItems().ToList();
        if (talents.Count + items.Count == 0)
        {
            return;
        }

        var lines = talents.GroupBy(x => x.Where)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(x => x.Talent.DisplayName).Take(8))}{(g.Count() > 8 ? $" and {g.Count() - 8} more" : "")}")
            .Concat(items.Count > 0 ? [$"Items: {string.Join(", ", items.Select(i => i.DisplayName).Take(8))}{(items.Count > 8 ? $" and {items.Count - 8} more" : "")}"] : []);
        if (!_dialogService.Confirm(
                "These come from mods you no longer have (or the game removed them), and nothing installed or in your Library gives them:\n\n"
                + string.Join("\n\n", lines)
                + "\n\nThey're taken out of the editor now; nothing is written until you press Save, which backs the slot up first. "
                + "Talent points they used come back. Ren spent on removed research isn't refunded.",
                "Remove leftovers from old mods?", ThemedConfirmSeverity.Warning))
        {
            return;
        }

        foreach (var (_, talent) in talents)
        {
            talent.Rank = 0;
        }

        foreach (var item in items)
        {
            MetaInventoryItems.Remove(item);
            FilteredMetaInventoryItems.Remove(item);
            _metaInventoryDirty = true;
        }

        NotifyDirtyChanged();
        NotifyLeftoversChanged();
        _activityLog.Log($"Took {talents.Count + items.Count} leftovers from old mods out of the save (press Save to keep that).", ActivityEntryKind.Success);
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

    private void BuildAccountFlags()
    {
        AccountFlags.Clear();
        var unlocked = SaveJson.IdArray(_profile?["UnlockedFlags"]).ToHashSet();
        foreach (var id in SaveFlagIds.KnownThenExtra(_gameNames.AccountFlagNames.Count, unlocked))
        {
            var flag = new SaveFlagViewModel(id, _gameNames.AccountFlagInfo(id), unlocked.Contains(id), NotifyDirtyChanged);
            flag.PropertyChanged += OnRewardFlagChanged;
            AccountFlags.Add(flag);
        }
    }

    // --- Mission <-> reward links (the user's ask, 2026-10-23): marking a mission done turns on
    // the unlocks it rewards, switching a reward on marks its mission done, and a message says so.
    // Only switching ON carries over: the game never takes a reward back when a mission is undone. ---

    private readonly Dictionary<SaveTalentViewModel, List<SaveFlagViewModel>> _rewardsOfMission = [];

    /// <summary>Each mission row and the reward rows it gives, for the save check.</summary>
    internal IEnumerable<(SaveTalentViewModel Mission, IReadOnlyList<SaveFlagViewModel> Rewards)> MissionRewards =>
        _rewardsOfMission.Select(kv => (kv.Key, (IReadOnlyList<SaveFlagViewModel>)kv.Value));
    private readonly Dictionary<SaveFlagViewModel, List<SaveTalentViewModel>> _missionsOfReward = [];
    private bool _applyingLink;

    /// <summary>Pairs each mission row with its reward rows and writes both rows' hints; after both lists are built.</summary>
    private void LinkMissionRewards()
    {
        _rewardsOfMission.Clear();
        _missionsOfReward.Clear();
        var flagsByRow = new Dictionary<string, SaveFlagViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var flag in AccountFlags)
        {
            flagsByRow.TryAdd(flag.RowName, flag);
            flag.LinkHint = null;
        }

        foreach (var mission in WorkshopTalents.Where(IsMission))
        {
            var rewards = _gameNames.MissionRewardFlags.GetValueOrDefault(mission.RowName, [])
                .Select(row => flagsByRow.GetValueOrDefault(row))
                .OfType<SaveFlagViewModel>()
                .ToList();
            mission.LinkHint = rewards.Count == 0
                ? null
                : $"Reward: {string.Join(", ", rewards.Select(r => r.Name))} — marking it done turns {(rewards.Count == 1 ? "that" : "those")} on in Unlocks too.";
            if (rewards.Count == 0)
            {
                continue;
            }

            _rewardsOfMission[mission] = rewards;
            foreach (var reward in rewards)
            {
                (_missionsOfReward.TryGetValue(reward, out var list) ? list : _missionsOfReward[reward] = []).Add(mission);
            }
        }

        foreach (var (reward, missions) in _missionsOfReward)
        {
            reward.LinkHint = $"Switching it on marks {string.Join(" and ", missions.Select(m => m.DisplayName))} done on the Missions tab too.";
        }
    }

    private void OnMissionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_applyingLink || e.PropertyName != nameof(SaveTalentViewModel.Rank)
            || sender is not SaveTalentViewModel { Rank: > 0 } mission || !_rewardsOfMission.TryGetValue(mission, out var rewards))
        {
            return;
        }

        var turnedOn = rewards.Where(r => !r.IsUnlocked).ToList();
        ApplyLink(() => turnedOn.ForEach(r => r.IsUnlocked = true));
        if (turnedOn.Count > 0)
        {
            _activityLog.Log($"Marked {mission.DisplayName} done, so its reward is on too: {string.Join(", ", turnedOn.Select(r => r.Name))} (Unlocks tab).", ActivityEntryKind.Success);
        }
    }

    private void OnRewardFlagChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_applyingLink || e.PropertyName != nameof(SaveFlagViewModel.IsUnlocked)
            || sender is not SaveFlagViewModel { IsUnlocked: true } reward || !_missionsOfReward.TryGetValue(reward, out var missions))
        {
            return;
        }

        var marked = missions.Where(m => m.Rank == 0).ToList();
        ApplyLink(() => marked.ForEach(m => m.IsDone = true));
        if (marked.Count > 0)
        {
            _activityLog.Log($"Turned on {reward.Name}, so its mission is marked done too: {string.Join(", ", marked.Select(m => m.DisplayName))} (Missions tab).", ActivityEntryKind.Success);
        }
    }

    private void ApplyLink(Action change)
    {
        _applyingLink = true;
        try
        {
            change();
        }
        finally
        {
            _applyingLink = false;
        }
    }

    /// <summary>
    /// The slot's flags_&lt;SteamID&gt;.dat is the game's cached list of the DLC this account owns
    /// (0-based D_DLCPackageData rows; OfflinePackageFlagsSubsystem saves and loads it — researched
    /// 2026-10-23). It used to be read as character-flag names ("Talent_RepairBench" for Dangerous
    /// Horizons). Each pack is a toggle under its real name: the user wants to be able to turn DLC
    /// and Supporter Edition content on (2026-10-23). The game may rewrite the file from its own
    /// ownership check when it starts — the page says so.
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
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException)
        {
            // An unreadable file just means the DLC section stays hidden — hiding beats showing
            // toggles that couldn't be written back safely.
        }

        if (_binaryFlagIds is { } owned)
        {
            var set = owned.ToHashSet();
            foreach (var id in SaveFlagIds.KnownThenExtra(_gameNames.DlcPackageNames.Count, set))
            {
                var info = new FlagInfo(_gameNames.DlcPackageRowName(id) ?? $"DLC {id}", _gameNames.DlcPackageName(id), "", FlagKind.Marker);
                BinaryFlags.Add(new SaveFlagViewModel(id, info, set.Contains(id), NotifyDirtyChanged));
            }
        }

        OnPropertyChanged(nameof(HasBinaryFlags));
        OnPropertyChanged(nameof(OwnedDlcSummary));
    }

    private void BuildWorkshopTalents()
    {
        WorkshopTalents.Clear();
        var ranks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_profile?["Talents"] is JsonArray talentArray)
        {
            foreach (var entry in talentArray.OfType<JsonObject>())
            {
                if (SaveJson.String(entry["RowName"]) is { } rowName)
                {
                    ranks[rowName] = SaveJson.Int(entry["Rank"]) ?? 0;
                }
            }
        }

        // The profile's own list first (its order), then any Workshop_* talent the game defines
        // that isn't researched yet — rank one up to research it.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, rank) in ranks)
        {
            AddWorkshopTalent(new SaveTalentViewModel(rowName, SaveCharacterViewModel.DisplayInfoFor(_gameNames, rowName), rank, NotifyDirtyChanged));
            seen.Add(rowName);
        }

        // Missions not done yet too (Prospect_*), so the Missions tab lists every one.
        foreach (var (rowName, info) in _gameNames.Talents)
        {
            if ((rowName.StartsWith("Workshop_", StringComparison.OrdinalIgnoreCase) || rowName.StartsWith("Prospect_", StringComparison.OrdinalIgnoreCase))
                && !seen.Contains(rowName))
            {
                AddWorkshopTalent(new SaveTalentViewModel(rowName, SaveCharacterViewModel.DisplayInfoFor(_gameNames, info), 0, NotifyDirtyChanged));
            }
        }

        LinkMissionRewards();
        RefreshAccountTalentFilters();
    }

    private void AddWorkshopTalent(SaveTalentViewModel talent)
    {
        if (IsMission(talent))
        {
            talent.PropertyChanged += OnMissionChanged;
        }

        WorkshopTalents.Add(talent);
    }

    // --- Accolades (account-wide, Accolades.json — a separate file from Profile.json) ---

    public ObservableCollection<SaveAccoladeViewModel> Accolades { get; } = [];

    public ObservableCollection<SaveAccoladeViewModel> FilteredAccolades { get; } = [];

    [ObservableProperty]
    private string _accoladeSearchText = "";

    partial void OnAccoladeSearchTextChanged(string value) => RefreshAccoladeFilter();

    /// <summary>Only the ones not earned yet.</summary>
    [ObservableProperty]
    private bool _showUnearnedAccoladesOnly;

    partial void OnShowUnearnedAccoladesOnlyChanged(bool value) => RefreshAccoladeFilter();

    /// <summary>"212 of 447 earned".</summary>
    public string AccoladesSummary => $"{Accolades.Count(a => a.IsCompleted)} of {Accolades.Count} earned";

    // Listed by the game's own categories (Survival, Hunting, Construction, Achievement...).
    private void RefreshAccoladeFilter() =>
        RefreshFilter(Accolades.OrderBy(a => a.Category.Length == 0 ? "~" : a.Category, StringComparer.OrdinalIgnoreCase), FilteredAccolades, AccoladeSearchText,
            ShowUnearnedAccoladesOnly ? a => !a.IsCompleted : null, a => a.DisplayName, a => a.Category, a => a.Description);

    /// <summary>CompletedAccolades only — PlayerTrackers/PlayerTaskListTrackers (the raw progress counters behind eligibility) are preserved untouched, out of scope for this editor.</summary>
    private void BuildAccolades()
    {
        Accolades.Clear();
        var completed = _accoladesRoot?["CompletedAccolades"] is JsonArray array
            ? array.OfType<JsonObject>()
                .Select(entry => SaveJson.String((entry["Accolade"] as JsonObject)?["RowName"]))
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
        OnPropertyChanged(nameof(AccoladesSummary));
    }

    // --- Bestiary (account-wide, BestiaryData.json — a separate file from Profile.json) ---

    public ObservableCollection<SaveBestiaryEntryViewModel> BestiaryEntries { get; } = [];

    public ObservableCollection<SaveBestiaryEntryViewModel> FilteredBestiaryEntries { get; } = [];

    [ObservableProperty]
    private string _bestiarySearchText = "";

    partial void OnBestiarySearchTextChanged(string value) => RefreshBestiaryFilter();

    /// <summary>Only creatures whose field-guide entry isn't complete.</summary>
    [ObservableProperty]
    private bool _showIncompleteBestiaryOnly;

    partial void OnShowIncompleteBestiaryOnlyChanged(bool value) => RefreshBestiaryFilter();

    /// <summary>"61 of 107 complete".</summary>
    public string BestiarySummary => $"{BestiaryEntries.Count(b => b.IsComplete)} of {BestiaryEntries.Count(b => b.PointsRequired > 0)} complete";

    // Listed by biome (Forest, Arctic, Desert..., Great Hunt arenas).
    private void RefreshBestiaryFilter() =>
        RefreshFilter(BestiaryEntries.OrderBy(b => b.Group, StringComparer.OrdinalIgnoreCase), FilteredBestiaryEntries, BestiarySearchText,
            ShowIncompleteBestiaryOnly ? b => !b.IsComplete : null, b => b.DisplayName, b => b.Group);

    /// <summary>Fills every creature listed right now (search and filter applied) to its full points.</summary>
    [RelayCommand]
    private void CompleteShownBestiary()
    {
        foreach (var entry in FilteredBestiaryEntries.Where(b => b.CanSetMax && !b.IsComplete).ToList())
        {
            entry.SetMaxCommand.Execute(null);
        }
    }

    /// <summary>BestiaryTracking only — FishTracking (a separate fishing sub-tracker) is preserved untouched, out of scope for this editor.</summary>
    private void BuildBestiaryEntries()
    {
        BestiaryEntries.Clear();
        var points = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (_bestiaryRoot?["BestiaryTracking"] is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                if (SaveJson.String((entry["BestiaryGroup"] as JsonObject)?["RowName"]) is { } rowName)
                {
                    points[rowName] = SaveJson.Int(entry["NumPoints"]) ?? 0;
                }
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (rowName, info) in _gameNames.BestiaryCreatures)
        {
            BestiaryEntries.Add(new SaveBestiaryEntryViewModel(rowName, info.DisplayName, info.PointsRequired, info.IsBoss, points.GetValueOrDefault(rowName), info.ImagePath, NotifyDirtyChanged, info.Group));
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
        OnPropertyChanged(nameof(BestiarySummary));
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
                if (SaveJson.String((entry["Accolade"] as JsonObject)?["RowName"]) is { } rowName && nowCompleted.Contains(rowName))
                {
                    newArray.Add(entry.DeepClone());
                }
            }
        }

        var alreadyWritten = newArray.OfType<JsonObject>()
            .Select(e => SaveJson.String((e["Accolade"] as JsonObject)?["RowName"]))
            .Where(n => n is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
        foreach (var accolade in Accolades)
        {
            if (accolade.IsCompleted && !alreadyWritten.Contains(accolade.RowName))
            {
                newArray.Add(new JsonObject
                {
                    ["Accolade"] = new JsonObject { ["RowName"] = accolade.RowName, ["DataTableName"] = "D_Accolades" },
                    ["TimeCompleted"] = DateTime.Now.ToString("yyyy.MM.dd-HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture),
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
                var rowName = SaveJson.String((entry["ItemStaticData"] as JsonObject)?["RowName"]) ?? "?";
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

    /// <summary>Name/Level editable, Type shown only — RecorderBlob (the mount's real stats/stomach/saddle state, a raw Unreal binary blob, not JSON) is preserved untouched on each mount's own live Node. See SaveMountViewModel's own doc comment for why a mount has no natural key to rebuild an array by, unlike Bestiary/Accolades.</summary>
    private void BuildMounts()
    {
        Mounts.Clear();
        if (_mountsRoot?["SavedMounts"] is JsonArray array)
        {
            foreach (var entry in array.OfType<JsonObject>())
            {
                var name = SaveJson.String(entry["MountName"]) ?? "";
                var level = SaveJson.Int(entry["MountLevel"]) ?? 0;
                var typeRowName = SaveJson.String(entry["MountType"]) ?? "";
                var iconPath = _gameNames.MountTypeIcons.GetValueOrDefault(typeRowName);
                Mounts.Add(new SaveMountViewModel(entry, name, level, typeRowName, iconPath, NotifyDirtyChanged));
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

    private void ApplyAccountFlagEdits(JsonObject profile)
    {
        var originallyUnlocked = SaveJson.IdArray(profile["UnlockedFlags"]);
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

        profile["UnlockedFlags"] = flagArray;
    }

    private void ApplyWorkshopTalentEdits(JsonObject profile)
    {
        var byRow = WorkshopTalents.ToDictionary(t => t.RowName, t => t.Rank, StringComparer.OrdinalIgnoreCase);
        var talentArrayNew = new JsonArray();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (profile["Talents"] is JsonArray existing)
        {
            foreach (var entry in existing.OfType<JsonObject>())
            {
                if (SaveJson.String(entry["RowName"]) is { } rowName && byRow.GetValueOrDefault(rowName) > 0)
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

        profile["Talents"] = talentArrayNew;
    }

    private void RefreshBackupsList()
    {
        Backups.Clear();
        if (SelectedSlot is null)
        {
            return;
        }

        // A problem with the backups folder must not fail what called this: a load (which then
        // blocked saving a save that read fine) or a save that already succeeded ("Save failed").
        try
        {
            foreach (var backup in _repository.ListBackups(SelectedSlot.SteamId))
            {
                Backups.Add(backup);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SelectedBackup = null;
            LastBackupDisplay = $"Couldn't list backups: {ex.Message}";
            return;
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

        // The character as edited, not as last saved (the "(Copy)" name already followed the edit).
        var clone = character.Node.DeepClone().AsObject();
        character.ApplyTo(clone);
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
        StatusMessage = $"Duplicated '{character.Name}' as '{newName}' (slot {SaveJson.Int(clone["ChrSlot"])}) — click Save player data to make it permanent.";
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
        var used = _characterNodes.Select(n => SaveJson.Int(n["ChrSlot"]) ?? -1).Where(id => id >= 0).ToHashSet();
        var candidate = SaveJson.Int(_profile?["NextChrSlot"]) ?? (used.Count == 0 ? 0 : used.Max() + 1);
        while (used.Contains(candidate))
        {
            candidate++;
        }

        if (_profile is not null)
        {
            _profile["NextChrSlot"] = candidate + 1;
            _profileFieldsDirty = true;
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
            var zipPath = _repository.BackupSlotManually(SelectedSlot.SteamId);
            RefreshBackupsList();
            StatusMessage = $"Backed up to '{Path.GetFileName(zipPath)}'.";
            _activityLog.Log($"Backed up save slot {SelectedSlot.Display}.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Backup failed: {ex.Message}";
            _activityLog.Log($"Save backup failed: {ex.Message}", ActivityEntryKind.Warning);
        }
    }

    [RelayCommand(FlowExceptionsToTaskScheduler = true)]
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

        // Via IDialogService, not ThemedMessageBox.Show directly — this method's real write below
        // needs to be testable without a live WPF Dispatcher, same reasoning DeleteCharacter's own
        // confirm relies on.
        var confirm = _dialogService.Confirm(
            $"Replace this save slot's current files with the backup from {SelectedBackup.TakenAtUtc.LocalDateTime:g}?\n\n"
            + "A pre_restore safety zip of the slot as it is right now is written first, so this is itself undoable.\n\n"
            + "Your single-player worlds, each character's list of worlds and map data are never restored: they stay as they are now."
            + (HasAnyUnsavedEdits ? "\n\nYour unsaved edits on this page will be lost." : ""),
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

            // The reload's own "Couldn't read this save" must not be covered by "Restored."
            StatusMessage = _loadFailed
                ? $"Restored, but the restored save couldn't be read back: {StatusMessage} The replaced state was saved as a pre_restore zip."
                : "Restored. The replaced state was saved as a pre_restore zip.";
            _activityLog.Log($"Restored save slot {SelectedSlot.Display} from backup.", ActivityEntryKind.Success);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Restore failed: {ex.Message}";
            _activityLog.Log($"Save restore failed: {ex.Message}", ActivityEntryKind.Warning);
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
