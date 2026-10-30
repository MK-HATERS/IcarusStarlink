using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Catalog;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.Assets;
using IcarusStarlink.PakIO.Container;
using IcarusStarlink.PakIO.Exmod;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class LibraryItemViewModel : ObservableObject
{
    /// <summary>How many .pak files an opaque entry holds (LibraryEntry.PakFileNames) — 0 for an EXMOD mod. A bundle (2+) is one mod that is never converted into an EXMOD.</summary>
    public int PakCount { get; private set; }

    public bool IsPakBundle => PakCount > 1;

    /// <summary>The opaque entry's pak file names (with extension) — what the installed manifest lists for it, so Status can tell whether it is in the game.</summary>
    public IReadOnlyList<string> PakFileNames { get; private set; } = [];

    // ---- The single Mods list (UE4SS tab merged in): every row has the same "On" checkbox, a
    // Type, and a Status that says what still has to happen for the mod to be in the game. ----

    /// <summary>
    /// The row's "On" checkbox: for a pak/EXMOD mod this IS merge-queue membership. Set from the
    /// queue by LibraryViewModel (SetEnabledFromQueue, no event) and toggled by the user (raises
    /// EnabledToggled, which adds to / removes from the queue).
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled;

    private bool _suppressEnabledToggled;

    public event EventHandler<bool>? EnabledToggled;

    public void SetEnabledFromQueue(bool enabled)
    {
        if (IsEnabled == enabled)
        {
            return;
        }

        _suppressEnabledToggled = true;
        try
        {
            IsEnabled = enabled;
        }
        finally
        {
            _suppressEnabledToggled = false;
        }
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_suppressEnabledToggled)
        {
            EnabledToggled?.Invoke(this, value);
        }
    }

    /// <summary>The UE4SS (Lua) mod folder names that came in the same download as this mod's paks (same Nexus mod ID) — the mod's "Lua half", enabled and disabled together with this row. Empty for a mod without one.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _linkedUe4ssNames = [];

    public bool HasLuaPart => LinkedUe4ssNames.Count > 0;

    /// <summary>Set by LibraryViewModel: the Lua half ships a config file, so the right-click menu offers Edit config.</summary>
    [ObservableProperty]
    private bool _hasLuaConfig;

    /// <summary>"EXMOD" or "Pak", the Type column. A Lua half has its own row in the UE4SS panel, linked to this one (not "Pak + UE4SS" here: the two halves are two rows).</summary>
    public string TypeLabel => !IsOpaquePak ? "EXMOD" : "Pak";

    partial void OnLinkedUe4ssNamesChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasLuaPart));
        OnPropertyChanged(nameof(TypeLabel));
    }

    /// <summary>"In game", "Changed" (changed since the game's merge was built; not "Updated", which read like the Update pill beside it), "Needs Merge &amp; Install", "Needs Apply", "Not enabled" — computed by LibraryViewModel from the queue, the installed manifest and the Lua half's own state.</summary>
    [ObservableProperty]
    private string _installStatus = "";

    /// <summary>The second line under InstallStatus ("scripts in game · paks not yet"); empty when there is nothing to add.</summary>
    [ObservableProperty]
    private string _installStatusDetail = "";

    public bool IsInGame => InstallStatus == "In game";

    public bool NeedsAction => InstallStatus.StartsWith("Needs", StringComparison.Ordinal) || InstallStatus is "Changed" or "Will be removed";

    /// <summary>Ticked but not in the game, or in the game but unticked: the next Merge &amp; Install changes it (LibraryViewModel.RefreshEnabledAndStatus). The "Not in game yet" filter.</summary>
    [ObservableProperty]
    private bool _isPendingChange;

    /// <summary>The UE4SS mod that is this mod's other half (MkHaters Bags ↔ MkHatersBags), set by LibraryViewModel; null when there is none. Only a hint in the list: each keeps its own tick.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkedPartner))]
    private Ue4ssModRowViewModel? _linkedPartner;

    public bool HasLinkedPartner => LinkedPartner is not null;

    /// <summary>The mouse is over the other half's row: this row lights up too.</summary>
    [ObservableProperty]
    private bool _isLinkHighlighted;

    /// <summary>The endorse thumb is filled.</summary>
    public bool IsEndorsed => EndorsementStatus == NexusEndorsementStatus.Endorsed;

    partial void OnInstallStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsInGame));
        OnPropertyChanged(nameof(NeedsAction));
    }

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    private readonly ILibraryRepository _repository;
    private readonly IUnrealPakService _unrealPakService;
    private readonly IUassetTextureDecoder _uassetTextureDecoder;
    private readonly IUassetStaticMeshDecoder _uassetStaticMeshDecoder;
    private readonly IUassetSkeletalMeshDecoder _uassetSkeletalMeshDecoder;
    private readonly IUassetSoundDecoder _uassetSoundDecoder;
    private readonly IUassetMaterialDecoder _uassetMaterialDecoder;
    private readonly IOpaquePakAssetPreviewService _opaquePakAssetPreviewService;
    private readonly ISettingsService _settingsService;
    private readonly INexusApiClient _nexusApiClient;
    private readonly ICredentialStore _credentialStore;
    private readonly HttpClient _httpClient;
    private readonly string _thumbnailCacheDirectory;
    private readonly string _pakPreviewCacheDirectory;
    private readonly Func<Task<IReadOnlyList<CatalogEntry>>> _getOrFetchCatalog;
    private readonly Action<string> _reportStatus;
    private readonly Action _onPinnedChanged;
    private readonly DebounceTimer _notesSaveDebounceTimer;
    private bool _detailsLoaded;

    /// <summary>Bumped every time SelectedAssetPath changes — a .uasset decode runs on a background
    /// thread, and this lets a stale decode's result be discarded if the user selects something
    /// else before it finishes, instead of overwriting whatever the user is now looking at.</summary>
    private int _assetPreviewGeneration;

    /// <summary>The temp .wav file backing SelectedAssetAudioPath, if any — tracked separately from
    /// the property itself so a later reselect can delete THIS specific file even after
    /// SelectedAssetAudioPath has already been reset to null (WPF's MediaElement plays from a real
    /// file path, not raw bytes, so a decoded sound has to land on disk somewhere first).</summary>
    private string? _currentAudioTempFilePath;

    public string FolderName { get; }

    /// <summary>When this mod was first imported — real, persisted metadata, not recomputed, so unlike LatestVersion it's already available for every row with no update-check needed.</summary>
    public DateTimeOffset ImportedAtUtc { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>What a screen reader says for the Library row (the class name otherwise).</summary>
    public override string ToString() => Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AuthorDisplay))]
    private string _author;

    /// <summary>The Author column: "—" for a mod that names nobody (blank, or "Unknown"), the same on every row.</summary>
    public string AuthorDisplay => string.IsNullOrWhiteSpace(Author) || string.Equals(Author.Trim(), "Unknown", StringComparison.OrdinalIgnoreCase) ? "—" : Author;

    [ObservableProperty]
    private string _version;

    [ObservableProperty]
    private string _description;

    [ObservableProperty]
    private string? _variantLabel;

    [ObservableProperty]
    private bool _isOpaquePak;

    /// <summary>Where this mod came from ("Nexus"/"Database") — null for a manual/local import, shown as a small provenance badge next to the name.</summary>
    [ObservableProperty]
    private string? _source;

    public bool HasSource => !string.IsNullOrEmpty(Source);

    partial void OnSourceChanged(string? value)
    {
        OnPropertyChanged(nameof(HasSource));
        OnPropertyChanged(nameof(HasUpdateAvailable));
    }

    partial void OnVersionChanged(string value)
    {
        OnPropertyChanged(nameof(HasUpdateAvailable));
        OnPropertyChanged(nameof(VersionText));
    }

    /// <summary>The Version column: "v1.2.0", or empty when the mod declares no version.</summary>
    public string VersionText => VersionLabel.For(Version);

    /// <summary>The real Nexus mod ID — only meaningful when Source == "Nexus", needed by LibraryViewModel.CheckForUpdatesAsync to look up this mod's current version.</summary>
    public int? NexusModId { get; private set; }

    /// <summary>What Nexus said the version was when this copy was downloaded/linked (LibraryEntry.NexusVersion) — the like-for-like side of HasUpdateAvailable; null when never recorded.</summary>
    public string? NexusVersion { get; private set; }

    /// <summary>The Nexus file this copy came from (LibraryEntry.NexusFileId) — when known, updates are judged by that file's own update chain.</summary>
    public int? NexusFileId { get; private set; }

    /// <summary>The newest file in this copy's own update chain, from the last check (null until then, or when the file isn't tracked).</summary>
    [ObservableProperty]
    private int? _latestNexusFileId;

    partial void OnLatestNexusFileIdChanged(int? value) => OnPropertyChanged(nameof(HasUpdateAvailable));

    /// <summary>The Link-to-Nexus button/menu text: an already-linked mod (this one has a Nexus badge and a mod ID) is offered a RE-link, so the action never reads as "this still needs linking" — a real point of confusion reported live on a freshly-activated Nexus download.</summary>
    public string NexusLinkActionLabel => HasNexusLink ? "Re-link Nexus ID…" : "Link to Nexus ID…";

    /// <summary>The community catalog's own stable entry ID — the Database counterpart of NexusModId; keeps update-checking rename-safe for Database-sourced mods.</summary>
    public string? CatalogEntryId { get; private set; }

    /// <summary>Whether this row has a stable Nexus mod ID to link out to — drives the "Open on Nexus" context menu item's enabled state.</summary>
    public bool HasNexusLink => NexusModId is not null;

    /// <summary>
    /// Live check rather than cached state — but it still needs an explicit notification when a
    /// backup is created (see NotifyBackupStateChanged). WPF keeps one ContextMenu instance per
    /// row and evaluates its bindings when that menu is first opened, NOT on every open, so
    /// without the notification a row whose menu had been opened once before its first backup
    /// existed kept "Restore latest backup"/"See what changed" disabled forever. Found live.
    /// </summary>
    public bool HasModBackup => _repository.HasModBackup(FolderName);

    /// <summary>Call after this mod gains a backup so the context menu items gated on HasModBackup enable without needing a full Library reload.</summary>
    public void NotifyBackupStateChanged() => OnPropertyChanged(nameof(HasModBackup));

    /// <summary>
    /// Call after CheckModsAgainstCurrentDataAsync's own confidence-tiered auto-repair rewrites this
    /// mod's .EXMOD directly on disk — every other mutation site in this codebase (e.g.
    /// ExmodEditorViewModel.Save) follows a save with either Update(entry) or a full resync, but the
    /// batch staleness pass only ever wrote to disk and to the repository's own cache, never to this
    /// bound row. Takes the ✎ badge from the repository's entry (not forced on: a fix to a mod
    /// converted from a prebuilt pak deliberately leaves it unedited, so it keeps re-converting
    /// against new game data) and clears the cached Changes tab content (same lines Update()
    /// already resets) so a currently-open Changes tab doesn't keep showing the pre-repair item
    /// name until something unrelated triggers a full resync.
    /// </summary>
    public void NotifyRepairedFromDisk()
    {
        var entry = _repository.GetAll().FirstOrDefault(e => string.Equals(e.FolderName, FolderName, StringComparison.OrdinalIgnoreCase));
        IsLocallyEdited = entry?.IsLocallyEdited ?? IsLocallyEdited;
        FixedByIslAtUtc = entry is null ? FixedByIslAtUtc : entry.FixedByIslAtUtc;
        ChangesContent = null;
        ChangesSummary = null;
        _changesSummaryRun++;
        _detailsLoaded = false;
    }

    /// <summary>
    /// The mod's current known version, as of the last "Check for updates" run — null until a
    /// check has actually happened (never persisted; matches Downloads' own Nexus watchlist
    /// precedent of recomputing this each check rather than caching it stale across sessions).
    /// </summary>
    [ObservableProperty]
    private string? _latestVersion;

    // Not a plain string compare — see UpdateVersionComparer for the live "1.0" vs "1" false positive.
    // A tracked Nexus file: an update means a newer file replaced the one installed — versions of
    // OTHER files of the same mod (its other variants) never count.
    public bool HasUpdateAvailable => HasSource && (NexusFileId is { } installedFile && LatestNexusFileId is { } newestFile
        ? newestFile != installedFile
        : UpdateVersionComparer.HasUpdate(LatestVersion, Version, NexusVersion));

    partial void OnLatestVersionChanged(string? value) => OnPropertyChanged(nameof(HasUpdateAvailable));

    /// <summary>
    /// What "Check mods against game data" found doesn't fit the current game (ModHealthChecker):
    /// old copies of game data, mixed lists, renamed rows. Drives the row's warning badge, which
    /// opens the fix window. Not persisted, recomputed each check.
    /// </summary>
    public ModHealthReport? Health { get; private set; }

    [ObservableProperty]
    private int _modIssueCount;

    public bool HasModIssues => ModIssueCount > 0;

    partial void OnModIssueCountChanged(int value) => OnPropertyChanged(nameof(HasModIssues));

    /// <summary>"Doesn't fully fit the current game: 3 sure fixes, 2 to look over": the sure ones are what Fix sure problems makes (old copies, proven broken links and renames).</summary>
    public string ModIssuesTooltip => Health is not { HasProblems: true } health
        ? ""
        : "Doesn't fully fit the current game: "
          + string.Join(", ", new[]
            {
                health.Issues.Count(i => i.IsAutomatic && !i.IsTidyOnly) is > 0 and var sure ? $"{sure} sure fix(es)" : null,
                health.Issues.Count(i => !i.IsTidyOnly && !i.IsAutomatic) is > 0 and var toLook ? $"{toLook} to look over" : null,
            }.Where(p => p is not null))
          + ". Click to see them and fix.";

    /// <summary>The game files this mod replaces, rated (the risk pill); null: it replaces none, or not checked yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFileRisk), nameof(IsHighFileRisk), nameof(FileRiskLabel), nameof(FileRiskTooltip))]
    private ModFileRisk? _fileRisk;

    /// <summary>Low risk (art and sound only) gets no pill: it can only look or sound wrong.</summary>
    public bool HasFileRisk => FileRisk is { Level: > IcarusStarlink.PakIO.Exmod.FileRisk.Low };

    /// <summary>High risk, or proven out of date since the last game update: the pill is red.</summary>
    public bool IsHighFileRisk => FileRisk is { Level: IcarusStarlink.PakIO.Exmod.FileRisk.High } or { IsOutOfDate: true };

    public string FileRiskLabel => FileRisk?.Label ?? "";

    public string FileRiskTooltip => FileRisk?.Explanation ?? "";

    /// <summary>"Made for an older game: between Week 222 and 4 Sep 2026" (the faint line under the name), or null when it fits today's game.</summary>
    public string? BuiltForText => Health?.BuiltFor?.OlderText;

    public bool HasBuiltForText => BuiltForText is not null;

    public void SetHealth(ModHealthReport? report)
    {
        Health = report;
        OnPropertyChanged(nameof(BuiltForText));
        OnPropertyChanged(nameof(HasBuiltForText));
        // Values that already match the game change nothing: no badge for those alone.
        ModIssueCount = report?.ProblemCount ?? 0;
        OnPropertyChanged(nameof(ModIssuesTooltip));
    }

    /// <summary>Phase 10: an opaque .pak entry has no .EXMOD to edit — the inline row Edit action hides itself rather than opening an editor with nothing to show.</summary>
    public bool CanEdit => !IsOpaquePak;

    partial void OnIsOpaquePakChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        // The Type column reads IsOpaquePak too: a converted or restored mod keeps its row.
        OnPropertyChanged(nameof(TypeLabel));
    }

    /// <summary>Set once the EXMOD editor's own Save action runs against this mod — the ✎ glyph.</summary>
    [ObservableProperty]
    private bool _isLocallyEdited;

    /// <summary>See LibraryEntry.FixedByIslAtUtc — the wrench glyph.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFixedByIsl), nameof(FixedByIslToolTip))]
    private DateTimeOffset? _fixedByIslAtUtc;

    public bool IsFixedByIsl => FixedByIslAtUtc is not null;

    public string FixedByIslToolTip => FixedByIslAtUtc is { } at
        ? $"Fixed by Starlink's mod check (first on {at.ToLocalTime():d MMM yyyy}). The fixes are made again after game updates. Right-click › Reinstall original or Restore latest backup to undo."
        : "";

    /// <summary>True once this mod's own EXMOD was derived from a prebuilt pak (automatically at import, or via Convert to EXMOD…) rather than authored directly — the candidate set LibraryViewModel's own silent post-data-update refresh re-derives from its saved source pak, as long as IsLocallyEdited hasn't since gone true.</summary>
    public bool ConvertedFromPrebuiltPak { get; private set; }

    /// <summary>
    /// A previously-Rebuild-and-Installed IcarusStarlink pak, re-imported as its own Library entry —
    /// the 📦 glyph. Drives Merge &amp; Install's queue rules. Also true for a build with an empty mod
    /// queue but at least one gameplay option enabled (MergedPackOptionDescriptions non-empty,
    /// MergedPackModNames empty) — without this, an options-only Rebuild output was indistinguishable
    /// from an ordinary externally-sourced opaque .pak, which would wrongly make it a candidate for
    /// LibraryViewModel's "Convert opaque mods" sweep (its own doc comment: converting a merged pack
    /// "would produce a meaningless giant diff").
    /// </summary>
    public bool IsMergedPack => MergedPackModNames is { Count: > 0 } || MergedPackOptionDescriptions is { Count: > 0 };

    /// <summary>Every mod's own display Name folded into this merged pack — null for anything that isn't one.</summary>
    public IReadOnlyList<string>? MergedPackModNames { get; private set; }

    /// <summary>Every gameplay option applied to this merged pack (e.g. "Stacks x2") — null for anything that isn't a gameplay-options-only build. See LibraryEntry.MergedPackOptionDescriptions.</summary>
    public IReadOnlyList<string>? MergedPackOptionDescriptions { get; private set; }

    [ObservableProperty]
    private bool _isPinned;

    [ObservableProperty]
    private bool _isFavorite;

    /// <summary>Ctrl/Shift-click multi-select state, for the "Add to merge queue" action — this page no longer has Merge & Install's own browsing pane to drag mods across from, so this is the whole hand-off mechanism. Purely a row-highlight/bulk-action flag, unrelated to IsPinned/IsFavorite's own persisted metadata.</summary>
    [ObservableProperty]
    private bool _isSelectedForBulk;

    [ObservableProperty]
    private string _notes;

    public ObservableCollection<string> AssetPaths { get; } = [];

    /// <summary>Non-null while an opaque .pak's own internal files are being listed via UnrealPak -List, or after that failed (missing UnrealPak.exe path, a corrupt pak) — null once AssetPaths is populated successfully. Not used for a normal EXMOD entry, whose AssetPaths load synchronously from disk.</summary>
    [ObservableProperty]
    private string? _pakListingStatus;

    [ObservableProperty]
    private string? _readmeContent;

    /// <summary>What this mod actually changes, rendered readable via ExmodChangesFormatter — not the raw compiled asset browsing the Files tab does. Null for an opaque .pak entry (IsOpaquePak), which has no .EXMOD to read at all.</summary>
    [ObservableProperty]
    private string? _changesContent;

    /// <summary>"What this mod changes" in a few lines (ModChangeSummarizer), shown above the full Changes list. Null until worked out.</summary>
    [ObservableProperty]
    private string? _changesSummary;

    /// <summary>The extracted game data, for the summary's comparison. Set by LibraryViewModel when it creates the row.</summary>
    internal string? DataFolder { get; set; }

    [ObservableProperty]
    private string? _selectedAssetPath;

    [ObservableProperty]
    private string? _selectedAssetPreview;

    /// <summary>Set instead of SelectedAssetPreview when the picked asset is a decodable image — exactly one of SelectedAssetImage/SelectedAssetMesh/SelectedAssetAudioPath/SelectedAssetMaterialParams/SelectedAssetPreview is ever non-null, which is what the Files tab switches its preview pane on.</summary>
    [ObservableProperty]
    private BitmapImage? _selectedAssetImage;

    /// <summary>Set instead of SelectedAssetPreview/SelectedAssetImage when the picked asset is a decodable static OR skeletal mesh (bind pose only for the latter — see IUassetSkeletalMeshDecoder) — a Model3D ready to bind straight into the Files tab's Viewport3D. Both mesh kinds share this one property since they render through the exact same Viewport3D with no distinction the UI needs to make.</summary>
    [ObservableProperty]
    private Model3D? _selectedAssetMesh;

    /// <summary>Set instead of every other SelectedAsset* preview property when the picked asset is a decodable sound — a real temp .wav file path (WPF's MediaElement plays from a file/URI, not raw bytes) that CleanupAudioTempFile deletes the moment a different asset is selected or this mod's selection is torn down.</summary>
    [ObservableProperty]
    private string? _selectedAssetAudioPath;

    /// <summary>Set instead of every other SelectedAsset* preview property when the picked asset is a decodable material — its own resolved parameter list (textures/scalars/colors/BlendMode/ShadingModel), not a rendered preview.</summary>
    [ObservableProperty]
    private MaterialParamsDisplay? _selectedAssetMaterialParams;

    /// <summary>
    /// Set from an asset conventionally named "ImageOnly" (any common image extension) if the
    /// mod package has one — a real convention from classic IMM's own format ("Added support for
    /// mods to have ImageOnly.png this will load the image into Mod Manager"), not a guess. Null
    /// for most mods, which don't carry one.
    /// </summary>
    [ObservableProperty]
    private BitmapImage? _thumbnailImage;

    public LibraryItemViewModel(
        LibraryEntry entry, ILibraryRepository repository, IUnrealPakService unrealPakService,
        IUassetTextureDecoder uassetTextureDecoder, IUassetStaticMeshDecoder uassetStaticMeshDecoder,
        IUassetSkeletalMeshDecoder uassetSkeletalMeshDecoder, IUassetSoundDecoder uassetSoundDecoder,
        IUassetMaterialDecoder uassetMaterialDecoder,
        IOpaquePakAssetPreviewService opaquePakAssetPreviewService, ISettingsService settingsService,
        INexusApiClient nexusApiClient, ICredentialStore credentialStore, HttpClient httpClient, string thumbnailCacheDirectory,
        string pakPreviewCacheDirectory,
        Func<Task<IReadOnlyList<CatalogEntry>>> getOrFetchCatalog, Action<string> reportStatus, Action onPinnedChanged)
    {
        _repository = repository;
        _unrealPakService = unrealPakService;
        _uassetTextureDecoder = uassetTextureDecoder;
        _uassetStaticMeshDecoder = uassetStaticMeshDecoder;
        _uassetSkeletalMeshDecoder = uassetSkeletalMeshDecoder;
        _uassetSoundDecoder = uassetSoundDecoder;
        _uassetMaterialDecoder = uassetMaterialDecoder;
        _opaquePakAssetPreviewService = opaquePakAssetPreviewService;
        _settingsService = settingsService;
        _nexusApiClient = nexusApiClient;
        _credentialStore = credentialStore;
        _httpClient = httpClient;
        _thumbnailCacheDirectory = thumbnailCacheDirectory;
        _pakPreviewCacheDirectory = pakPreviewCacheDirectory;
        _getOrFetchCatalog = getOrFetchCatalog;
        _reportStatus = reportStatus;
        _onPinnedChanged = onPinnedChanged;
        FolderName = entry.FolderName;
        ImportedAtUtc = entry.ImportedAtUtc;
        _name = entry.Name;
        _author = entry.Author;
        _version = entry.Version;
        _description = entry.Description;
        _variantLabel = entry.Variant;
        _isOpaquePak = entry.IsOpaquePak;
        PakCount = entry.IsOpaquePak ? Math.Max(1, entry.PakFileNames.Count) : 0;
        PakFileNames = entry.PakFileNames;
        _isLocallyEdited = entry.IsLocallyEdited;
        _fixedByIslAtUtc = entry.FixedByIslAtUtc;
        ConvertedFromPrebuiltPak = entry.ConvertedFromPrebuiltPak;
        _source = entry.Source;
        NexusModId = entry.NexusModId;
        NexusVersion = entry.NexusVersion;
        NexusFileId = entry.NexusFileId;
        CatalogEntryId = entry.CatalogEntryId;
        MergedPackModNames = entry.MergedPackModNames;
        MergedPackOptionDescriptions = entry.MergedPackOptionDescriptions;

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
        _notesSaveDebounceTimer = new DebounceTimer(TimeSpan.FromMilliseconds(500), SaveMetadata);
    }

    /// <summary>
    /// LibraryViewModel.GetOrCreateItem calls this on a reused instance only when Reload() was
    /// triggered by an explicit Refresh() (its fullResync flag) — without it, Refresh() re-scanning
    /// a mod that was edited outside the app (a new Name/Version/etc. in its .EXMOD) would update
    /// the repository's data but leave this already-instantiated row showing whatever it had at
    /// construction time, forever. It's deliberately *not* called on every routine reload (search,
    /// import, delete): this mod's own data can't have changed just because a different mod was
    /// imported, and blindly re-syncing on every reload was tried and reverted — see Reload()'s
    /// own doc comment for why.
    /// </summary>
    public void Update(LibraryEntry entry)
    {
        Name = entry.Name;
        Author = entry.Author;
        Version = entry.Version;
        Description = entry.Description;
        VariantLabel = entry.Variant;
        IsOpaquePak = entry.IsOpaquePak;
        // Status ("in game") and the bundle check read these: a restore or reinstall can change them.
        PakCount = entry.IsOpaquePak ? Math.Max(1, entry.PakFileNames.Count) : 0;
        PakFileNames = entry.PakFileNames;
        IsLocallyEdited = entry.IsLocallyEdited;
        FixedByIslAtUtc = entry.FixedByIslAtUtc;
        ConvertedFromPrebuiltPak = entry.ConvertedFromPrebuiltPak;
        Source = entry.Source;
        NexusModId = entry.NexusModId;
        OnPropertyChanged(nameof(HasNexusLink));
        OnPropertyChanged(nameof(ShowEndorseThumb));
        OnPropertyChanged(nameof(NexusLinkActionLabel));
        // Their CanExecute needs a Nexus link: linking a mod must enable them, not only show them.
        EndorseCommand.NotifyCanExecuteChanged();
        AbstainCommand.NotifyCanExecuteChanged();
        NexusVersion = entry.NexusVersion;
        NexusFileId = entry.NexusFileId;
        OnPropertyChanged(nameof(HasUpdateAvailable));
        CatalogEntryId = entry.CatalogEntryId;
        MergedPackModNames = entry.MergedPackModNames;
        MergedPackOptionDescriptions = entry.MergedPackOptionDescriptions;
        OnPropertyChanged(nameof(IsMergedPack));

        // Direct field assignment + manual notify, not the generated properties: this re-syncs
        // from a re-scan, not a user edit, so it must update the UI without re-triggering
        // OnIsPinnedChanged/OnIsFavoriteChanged/OnNotesChanged — those would redundantly write
        // this same value straight back to the repository, and for IsPinned specifically, call
        // back into the very Reload() this method is running from. MVVMTK0034 flags direct field
        // access outside the generated setter on the (correct, in general) assumption that it's
        // a mistake; the constructor gets an implicit pass from the analyzer for the same
        // pattern, a plain method doesn't, so it's suppressed explicitly here instead.
#pragma warning disable MVVMTK0034
        if (_isPinned != entry.IsPinned)
        {
            _isPinned = entry.IsPinned;
            OnPropertyChanged(nameof(IsPinned));
        }

        if (_isFavorite != entry.IsFavorite)
        {
            _isFavorite = entry.IsFavorite;
            OnPropertyChanged(nameof(IsFavorite));
        }

        // Never over text the user is mid-typing (an audit finding): a LibraryChangedMessage from
        // an unrelated import/update arriving inside the 500 ms notes-save debounce used to revert
        // the box to the on-disk text, and the still-pending save then wrote that stale text back.
        // The pending save is the newer truth; it lands on its own.
        if (_notes != entry.Notes && !_notesSaveDebounceTimer.IsRunning)
        {
            _notes = entry.Notes;
            OnPropertyChanged(nameof(Notes));
            OnPropertyChanged(nameof(NotesSummary));
        }
#pragma warning restore MVVMTK0034

        // The mod's own assets (Files list, Readme, thumbnail) can have changed too, not just
        // its header fields — clearing the cache and resetting _detailsLoaded lets the next
        // EnsureDetailsLoaded() (LibraryViewModel.Reload() calls it unconditionally on whatever
        // ends up selected) re-read them, instead of this row showing pre-refresh Files/Readme/
        // thumbnail content for the rest of the session.
        AssetPaths.Clear();
        ReadmeContent = null;
        ChangesContent = null;
        ChangesSummary = null;
        _changesSummaryRun++;
        _pakContentsRun++;
        ThumbnailImage = null;
        SelectedAssetPath = null;
        PakListingStatus = null;
        _detailsLoaded = false;
    }

    /// <summary>
    /// Stops any pending debounced notes save without flushing it — called right before this
    /// mod's folder is deleted. Without this, a stale timer firing after delete could write this
    /// entry's old metadata into a *different* mod that reuses the exact same folder name
    /// (MakeUniqueFolderName only avoids collisions with what currently exists on disk), since
    /// UpdateMetadata's only existence check is Directory.Exists on that name.
    /// </summary>
    public void CancelPendingSave() => _notesSaveDebounceTimer.Cancel();

    /// <summary>
    /// Call before dropping this instance from a cache it's still reachable from (e.g. a
    /// search/filter reload evicting a row that's no longer in the results) but whose underlying
    /// mod folder isn't actually going anywhere — unlike CancelPendingSave (for an actual delete),
    /// discarding an in-flight Notes edit here would silently lose what the user just typed instead
    /// of just saving it a little early.
    /// </summary>
    public void FlushPendingSave() => _notesSaveDebounceTimer.Flush();

    partial void OnIsPinnedChanged(bool value)
    {
        // Explicit sequencing, not a PropertyChanged subscription on the LibraryViewModel side:
        // pinned status drives Reload()'s sort order, and that reorder must see the repository
        // already updated — calling SaveMetadata() first and only then _onPinnedChanged() (rather
        // than relying on where CommunityToolkit's generated setter happens to raise
        // INotifyPropertyChanged relative to this partial method) guarantees that order without
        // depending on source-generator internals.
        SaveMetadata();
        _onPinnedChanged();
    }

    partial void OnIsFavoriteChanged(bool value) => SaveMetadata();

    partial void OnNotesChanged(string value)
    {
        OnPropertyChanged(nameof(NotesSummary));
        _notesSaveDebounceTimer.Restart();
    }

    /// <summary>The detail window's Notes section header: the first line of the notes (shortened), or "None yet".</summary>
    public string NotesSummary
    {
        get
        {
            var firstLine = (Notes ?? "").Trim().Split('\n', 2)[0].Trim();
            return firstLine.Length == 0 ? "None yet" : firstLine.Length > 60 ? firstLine[..60] + "…" : firstLine;
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
}
