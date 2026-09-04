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

/// <summary>One texture-valued material parameter, ready for the Files tab's own Image control — the
/// WPF counterpart of PakIO's own MaterialTextureParam, whose PngBytes this decodes exactly once
/// (via LibraryItemViewModel's own TryDecodeImage) rather than the XAML layer needing a byte[]-to-
/// BitmapImage converter of its own.</summary>
public sealed record MaterialTextureParamDisplay(string Name, BitmapImage Thumbnail);

/// <summary>One scalar (float) material parameter, ready to show as-is.</summary>
public sealed record MaterialScalarParamDisplay(string Name, float Value);

/// <summary>
/// One color material parameter, ready for a small Border/Rectangle swatch plus a readable text
/// value. SwatchColor is a straightforward linear-to-byte mapping (each channel clamped to 0..1,
/// then scaled to 0..255) — not a true sRGB gamma correction, since this is a plain parameter-list
/// swatch, not meant to be colorimetrically exact the way an in-engine material preview would be.
/// </summary>
public sealed record MaterialColorParamDisplay(string Name, Color SwatchColor, string DisplayText);

/// <summary>
/// The WPF-ready counterpart of PakIO's own UassetMaterialParams — LibraryItemViewModel's public
/// SelectedAssetMaterialParams surface never exposes that raw PakIO record directly (its own
/// texture parameters are still un-decoded PNG bytes), same "convert to a WPF-ready type before
/// exposing it as a bound property" precedent BuildMeshModel already sets for SelectedAssetMesh.
/// </summary>
public sealed record MaterialParamsDisplay(
    IReadOnlyList<MaterialTextureParamDisplay> Textures,
    IReadOnlyList<MaterialScalarParamDisplay> Scalars,
    IReadOnlyList<MaterialColorParamDisplay> Colors,
    string BlendMode,
    string ShadingModel);

public sealed partial class LibraryItemViewModel : ObservableObject
{
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

    [ObservableProperty]
    private string _author;

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

    partial void OnVersionChanged(string value) => OnPropertyChanged(nameof(HasUpdateAvailable));

    /// <summary>The real Nexus mod ID — only meaningful when Source == "Nexus", needed by LibraryViewModel.CheckForUpdatesAsync to look up this mod's current version.</summary>
    public int? NexusModId { get; private set; }

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
    /// bound row. Sets the ✎ badge immediately and clears the cached Changes tab content (same two
    /// lines Update() already resets) so a currently-open Changes tab doesn't keep showing the
    /// pre-repair item name until something unrelated triggers a full resync.
    /// </summary>
    public void NotifyRepairedFromDisk()
    {
        IsLocallyEdited = true;
        ChangesContent = null;
        _detailsLoaded = false;
    }

    /// <summary>
    /// Closes a stale gap: the context menu item was hardcoded disabled since Phase 3.5 ("Available
    /// once Downloads links mods to their Nexus page"), but NexusModId has existed on LibraryEntry
    /// since this session's own update-checking work — the data this needs was already there, it
    /// just was never wired up. Self-contained on this row's own ViewModel (not LibraryViewModel)
    /// since opening a URL needs nothing else, and doing it here sidesteps ContextMenu's DataContext
    /// not flowing the same way RelativeSource lookups do elsewhere in this app.
    /// </summary>
    [RelayCommand]
    private void OpenOnNexus()
    {
        if (NexusModId is not { } nexusModId)
        {
            return;
        }

        UrlOpener.TryOpen(NexusModWebUrl.For(nexusModId));
    }

    /// <summary>
    /// Nexus's own recorded status for THIS account on this mod — null until "Check endorsements"
    /// has actually run (never persisted, same "computed at display time" precedent as
    /// LatestVersion/StaleItemCount below — a stale cached endorsement status surviving a restart
    /// would be actively misleading if it changed on Nexus's own website in the meantime).
    /// </summary>
    [ObservableProperty]
    private NexusEndorsementStatus? _endorsementStatus;

    public bool HasEndorsementStatus => EndorsementStatus is not null;

    public string EndorsementStatusDisplay => EndorsementStatus switch
    {
        NexusEndorsementStatus.Endorsed => "Endorsed",
        NexusEndorsementStatus.Abstained => "Abstained",
        NexusEndorsementStatus.Undecided => "Not endorsed yet",
        _ => "",
    };

    [ObservableProperty]
    private bool _isEndorsing;

    partial void OnEndorsementStatusChanged(NexusEndorsementStatus? value)
    {
        OnPropertyChanged(nameof(HasEndorsementStatus));
        OnPropertyChanged(nameof(EndorsementStatusDisplay));
        EndorseCommand.NotifyCanExecuteChanged();
        AbstainCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsEndorsingChanged(bool value)
    {
        EndorseCommand.NotifyCanExecuteChanged();
        AbstainCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEndorse))]
    private Task Endorse() => SetEndorsementAsync(endorse: true);

    private bool CanEndorse() => HasNexusLink && !IsEndorsing && EndorsementStatus != NexusEndorsementStatus.Endorsed;

    [RelayCommand(CanExecute = nameof(CanAbstain))]
    private Task Abstain() => SetEndorsementAsync(endorse: false);

    private bool CanAbstain() => HasNexusLink && !IsEndorsing && EndorsementStatus != NexusEndorsementStatus.Abstained;

    /// <summary>
    /// A real write to this account's Nexus endorsement for this mod. Fetches the mod's own
    /// CURRENT real Nexus version first rather than reusing this row's own locally-recorded
    /// Version, which can be stale (this mod may have updated since it was downloaded) — Nexus's
    /// own endorse endpoint requires a version that genuinely exists for the mod right now.
    /// </summary>
    private async Task SetEndorsementAsync(bool endorse)
    {
        if (NexusModId is not { } modId)
        {
            return;
        }

        var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
        if (apiKey is null)
        {
            _reportStatus("Sign in with your Nexus API key in Settings to endorse mods.");
            return;
        }

        IsEndorsing = true;
        try
        {
            var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", modId);
            if (info is null)
            {
                _reportStatus($"Couldn't reach '{Name}' on Nexus.");
                return;
            }

            EndorsementStatus = await _nexusApiClient.SetEndorsementAsync(apiKey, "icarus", modId, info.Version, endorse);
            _reportStatus(endorse ? $"Endorsed '{Name}' on Nexus." : $"Marked '{Name}' as abstained on Nexus.");
        }
        catch (Exception ex)
        {
            _reportStatus($"Couldn't update your endorsement for '{Name}': {ex.Message}");
        }
        finally
        {
            IsEndorsing = false;
        }
    }

    /// <summary>
    /// The mod's current known version, as of the last "Check for updates" run — null until a
    /// check has actually happened (never persisted; matches Downloads' own Nexus watchlist
    /// precedent of recomputing this each check rather than caching it stale across sessions).
    /// </summary>
    [ObservableProperty]
    private string? _latestVersion;

    public bool HasUpdateAvailable => HasSource && !string.IsNullOrEmpty(LatestVersion) && !string.Equals(LatestVersion, Version, StringComparison.OrdinalIgnoreCase);

    partial void OnLatestVersionChanged(string? value) => OnPropertyChanged(nameof(HasUpdateAvailable));

    /// <summary>
    /// Populated by LibraryViewModel.CheckModsAgainstCurrentDataAsync — count of this mod's own
    /// items that look like they're editing a row the currently-extracted game data no longer has
    /// (a small field count is the tell; see StaleItemHeuristic). Not persisted, recomputed each
    /// check — same "computed at display time" precedent as LatestVersion/HasUpdateAvailable above.
    /// </summary>
    [ObservableProperty]
    private int _staleItemCount;

    public bool HasPossiblyStaleItems => StaleItemCount > 0;

    partial void OnStaleItemCountChanged(int value) => OnPropertyChanged(nameof(HasPossiblyStaleItems));

    /// <summary>
    /// A best-guess replacement name for the first remaining flagged item, when one exists but
    /// wasn't confident enough to auto-apply (see StaleItemFixSuggester's ambiguity/field-overlap
    /// guards) — shown in the row's warning badge tooltip so a user doesn't have to open the editor
    /// just to learn there's a guess at all. Null when there's no candidate close enough to name.
    /// </summary>
    [ObservableProperty]
    private string? _staleItemSuggestionHint;

    private IReadOnlyList<ExmodStalenessChecker.StaleItem> _staleItems = [];

    /// <summary>The first flagged item, if any — LibraryViewModel's OpenStaleItemCommand pre-selects this one in the editor when the row's warning badge is clicked.</summary>
    public (string CurrentFile, string ItemName)? FirstStaleItem =>
        _staleItems.Count > 0 ? (_staleItems[0].CurrentFile, _staleItems[0].ItemName) : null;

    public void SetStaleItems(IReadOnlyList<ExmodStalenessChecker.StaleItem> staleItems, string? suggestionHint)
    {
        _staleItems = staleItems;
        StaleItemCount = staleItems.Count;
        StaleItemSuggestionHint = suggestionHint;
    }

    /// <summary>Phase 10: an opaque .pak entry has no .EXMOD to edit — the inline row Edit action hides itself rather than opening an editor with nothing to show.</summary>
    public bool CanEdit => !IsOpaquePak;

    partial void OnIsOpaquePakChanged(bool value) => OnPropertyChanged(nameof(CanEdit));

    /// <summary>Set once the EXMOD editor's own Save action runs against this mod — the ✎ glyph.</summary>
    [ObservableProperty]
    private bool _isLocallyEdited;

    /// <summary>True once this mod's own EXMOD was derived from a prebuilt pak (automatically at import, or via Convert to EXMOD…) rather than authored directly — the candidate set LibraryViewModel's own silent post-data-update refresh re-derives from its saved source pak, as long as IsLocallyEdited hasn't since gone true.</summary>
    public bool ConvertedFromPrebuiltPak { get; private set; }

    /// <summary>A previously-Rebuild-and-Installed IcarusStarlink pak, re-imported as its own Library entry — the 📦 glyph. Drives Merge &amp; Install's queue rules.</summary>
    public bool IsMergedPack => MergedPackModNames is { Count: > 0 };

    /// <summary>Every mod's own display Name folded into this merged pack — null for anything that isn't one.</summary>
    public IReadOnlyList<string>? MergedPackModNames { get; private set; }

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
        _isLocallyEdited = entry.IsLocallyEdited;
        ConvertedFromPrebuiltPak = entry.ConvertedFromPrebuiltPak;
        _source = entry.Source;
        NexusModId = entry.NexusModId;
        CatalogEntryId = entry.CatalogEntryId;
        MergedPackModNames = entry.MergedPackModNames;

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
        IsLocallyEdited = entry.IsLocallyEdited;
        ConvertedFromPrebuiltPak = entry.ConvertedFromPrebuiltPak;
        Source = entry.Source;
        NexusModId = entry.NexusModId;
        OnPropertyChanged(nameof(HasNexusLink));
        CatalogEntryId = entry.CatalogEntryId;
        MergedPackModNames = entry.MergedPackModNames;
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

        if (_notes != entry.Notes)
        {
            _notes = entry.Notes;
            OnPropertyChanged(nameof(Notes));
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

    partial void OnNotesChanged(string value) => _notesSaveDebounceTimer.Restart();

    partial void OnSelectedAssetPathChanged(string? value)
    {
        SelectedAssetImage = null;
        SelectedAssetMesh = null;
        SelectedAssetMaterialParams = null;
        CleanupAudioTempFile();
        _assetPreviewGeneration++;
        var generation = _assetPreviewGeneration;

        if (value is null)
        {
            SelectedAssetPreview = null;
            return;
        }

        if (IsOpaquePak)
        {
            // These paths came from UnrealPak -List, not this mod's own folder on disk —
            // ReadAssetContent (ExmodFolder-based) has nothing to read them from. A .uasset is
            // still worth a real decode attempt (see DecodeOpaquePakAssetPreviewAsync below,
            // which extracts this mod's own .pak and runs it through the same decoders a regular
            // EXMOD mod's preview already uses) — everything else here (.uexp, .ubulk, or a
            // non-asset file) has no decoder to try and falls back to a plain "no preview"
            // message, the same as any file type DecodeCompiledAssetPreviewAsync can't show.
            if (string.Equals(Path.GetExtension(value), ".uasset", StringComparison.OrdinalIgnoreCase))
            {
                SelectedAssetPreview = "Decoding this asset...";
                _ = DecodeOpaquePakAssetPreviewAsync(value, generation);
                return;
            }

            SelectedAssetPreview = "(packed inside this .pak — no preview available for this file type)";
            return;
        }

        try
        {
            // Checked before any read: DecodeCompiledAssetPreviewAsync does its own async decode
            // straight from disk (via FolderName's own path + this relative path) and never touches
            // this method's own `bytes` — reading a compiled asset's full content synchronously
            // here first, only to discard it, was pure wasted work blocking the UI thread for
            // nothing (a large mesh/texture .uasset can be many MB). Found live.
            if (string.Equals(Path.GetExtension(value), ".uasset", StringComparison.OrdinalIgnoreCase))
            {
                SelectedAssetPreview = "Decoding this asset...";
                _ = DecodeCompiledAssetPreviewAsync(value, generation);
                return;
            }

            var bytes = _repository.ReadAssetContent(FolderName, value);

            // The spec's "preview text/images" — an image asset used to render as binary noise in
            // the text box, since every non-text file fell through to the byte-count message.
            if (ImageExtensions.Contains(Path.GetExtension(value)) && TryDecodeImage(bytes) is { } image)
            {
                SelectedAssetImage = image;
                SelectedAssetPreview = null;
                return;
            }

            SelectedAssetPreview = LooksLikeText(bytes)
                ? System.Text.Encoding.UTF8.GetString(bytes)
                : $"(binary file — {bytes.Length:N0} bytes, no preview)";
        }
        catch (Exception ex)
        {
            // Same UI boundary as SaveMetadata: the asset a user just clicked can vanish or get
            // locked between ListAssetPaths populating this list and this read (external edit,
            // AV quarantine), and that should show up as an explanation in the preview pane
            // itself — the one place this specific failure is actually relevant — rather than
            // crash the app out of a binding-driven property setter.
            SelectedAssetPreview = $"(failed to read this file: {ex.Message})";
            _reportStatus($"Preview failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The one non-null slot in CompiledAssetDecodeResult — an asset is realistically only ever one
    /// of these types, so DecodeCompiledAssetPreviewAsync's own chain stops at the first decoder
    /// that succeeds instead of running every one of the (real, CPU-costing) remaining decoders for
    /// nothing. Static and skeletal meshes share the one Mesh slot: both end up rendered through
    /// the exact same BuildMeshModel/Viewport3D path, so nothing downstream needs to tell them apart.
    /// </summary>
    private sealed record CompiledAssetDecodeResult(
        byte[]? PngBytes, StaticMeshGeometry? Mesh, UassetSoundAudio? Sound, UassetMaterialParams? Material);

    /// <summary>
    /// A .uasset is compiled Unreal binary, not something the flat-image/text branches above can
    /// show — this tries, in order, a real Unreal texture, a static mesh, a skeletal mesh (bind
    /// pose only), a sound, then a material, via CUE4Parse — the same "texture first, most-proven
    /// decoders first" order this chain already used before the last three were added. Runs off
    /// the UI thread since indexing the mod's own asset folder and decoding any one of these takes
    /// real time; the generation check discards a stale result if the user has already selected
    /// something else by the time it finishes.
    /// </summary>
    private async Task DecodeCompiledAssetPreviewAsync(string relativeAssetPath, int generation)
    {
        string modFolderPath;
        try
        {
            modFolderPath = _repository.GetFolderPath(FolderName);
        }
        catch (Exception ex)
        {
            if (generation == _assetPreviewGeneration)
            {
                SelectedAssetPreview = $"(failed to read this file: {ex.Message})";
            }
            return;
        }

        var result = await Task.Run(() => DecodeCompiledAsset(modFolderPath, relativeAssetPath));

        if (generation != _assetPreviewGeneration)
        {
            return;
        }

        if (result.PngBytes is not null && TryDecodeImage(result.PngBytes) is { } image)
        {
            SelectedAssetImage = image;
            SelectedAssetPreview = null;
        }
        else if (result.Mesh is not null)
        {
            SelectedAssetMesh = BuildMeshModel(result.Mesh);
            SelectedAssetPreview = null;
        }
        else if (result.Sound is { WavBytes: { } wavBytes })
        {
            try
            {
                SelectedAssetAudioPath = WriteAudioTempFile(wavBytes);
                SelectedAssetPreview = null;
            }
            catch (Exception ex)
            {
                // A real but unlikely failure (disk full, %TEMP% unavailable/locked down) — same UI
                // boundary as everything else in this method: a status message, not an unobserved
                // exception out of this fire-and-forget decode task.
                SelectedAssetPreview = $"(compiled Unreal asset — couldn't write this sound to a temp file for playback: {ex.Message})";
            }
        }
        else if (result.Sound is { UnsupportedFormatReason: { } unsupportedReason })
        {
            SelectedAssetPreview = $"(compiled Unreal asset — {unsupportedReason})";
        }
        else if (result.Material is not null)
        {
            SelectedAssetMaterialParams = BuildMaterialParamsDisplay(result.Material);
            SelectedAssetPreview = null;
        }
        else
        {
            SelectedAssetPreview = "(compiled Unreal asset — not a texture, mesh, sound, or material, or couldn't be decoded — no preview available)";
        }
    }

    private CompiledAssetDecodeResult DecodeCompiledAsset(string modFolderPath, string relativeAssetPath)
    {
        var png = _uassetTextureDecoder.TryDecodeToPng(modFolderPath, relativeAssetPath);
        if (png is not null)
        {
            return new CompiledAssetDecodeResult(png, null, null, null);
        }

        var mesh = _uassetStaticMeshDecoder.TryDecodeStaticMesh(modFolderPath, relativeAssetPath)
            ?? _uassetSkeletalMeshDecoder.TryDecodeSkeletalMesh(modFolderPath, relativeAssetPath);
        if (mesh is not null)
        {
            return new CompiledAssetDecodeResult(null, mesh, null, null);
        }

        var sound = _uassetSoundDecoder.TryDecodeAudio(modFolderPath, relativeAssetPath);
        if (sound is not null)
        {
            return new CompiledAssetDecodeResult(null, null, sound, null);
        }

        var material = _uassetMaterialDecoder.TryDecodeMaterial(modFolderPath, relativeAssetPath);
        return new CompiledAssetDecodeResult(null, null, null, material);
    }

    /// <summary>
    /// Writes a decoded sound's own real, complete WAV bytes to a fresh temp file — WPF's
    /// MediaElement plays from a file/URI, not raw bytes, so a decoded sound has to land on disk
    /// somewhere first. The PREVIOUS temp file (if any) was already deleted by
    /// OnSelectedAssetPathChanged's own CleanupAudioTempFile call before this decode even started,
    /// so there's nothing left over to clean up here — only this new one needs tracking, for
    /// whenever the NEXT selection change comes along.
    /// </summary>
    private string WriteAudioTempFile(byte[] wavBytes)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"IcarusStarlink_AudioPreview_{Guid.NewGuid():N}.wav");
        File.WriteAllBytes(tempPath, wavBytes);
        _currentAudioTempFilePath = tempPath;
        return tempPath;
    }

    /// <summary>
    /// ModDetailWindow's own Closed handler calls this (after releasing the MediaElement's file
    /// handle via AudioPlayer.Close()) — without it, closing the window left whatever temp .wav
    /// was last previewed on disk indefinitely: this instance is cached/reused by LibraryViewModel,
    /// so CleanupAudioTempFile's own normal trigger (the NEXT selection change) might not fire
    /// again for the life of the session.
    /// </summary>
    public void ReleaseAudioPreview() => CleanupAudioTempFile();

    /// <summary>
    /// Best-effort delete of whatever temp .wav WriteAudioTempFile last created — called at the
    /// start of every selection change (see OnSelectedAssetPathChanged) and by ReleaseAudioPreview
    /// (see its own doc comment), so at most one of these temp files exists per
    /// LibraryItemViewModel instance at any given time, rather than one accumulating per sound ever
    /// previewed for the life of the app.
    /// </summary>
    private void CleanupAudioTempFile()
    {
        SelectedAssetAudioPath = null;

        if (_currentAudioTempFilePath is not { } path)
        {
            return;
        }

        _currentAudioTempFilePath = null;
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Best-effort only, same convention this codebase's other temp-file cleanup already
            // uses (e.g. DownloadsViewModel's own download temp files) — a locked/already-gone
            // file here is cosmetic (an OS temp directory, not mod data), never worth surfacing.
        }
    }

    /// <summary>
    /// Real preview attempt for a .uasset packed inside an opaque/prebuilt-imported pak.
    /// DecodeCompiledAssetPreviewAsync above can point its decoders straight at this mod's own
    /// folder on disk because a regular EXMOD mod's assets already live there loose — an opaque
    /// pak's own assets don't; they're only ever packed inside its single .pak file, which
    /// CueAssetProviderLocator (what every decoder goes through) can't index directly. So this
    /// extracts that pak first — cached under this app's own Cache directory, same convention
    /// ThumbnailImage's own remote-picture cache already uses, keyed by this mod's FolderName so
    /// repeated preview clicks against the same pak don't each pay a fresh whole-pak extract (see
    /// OpaquePakAssetPreviewService's own doc comment for why it's whole-pak, not scoped, and
    /// what that costs the first time) — then runs the exact same five decoders through it. Same
    /// generation-based staleness guard, and the same PngBytes/Mesh/Sound/Material branching, as
    /// DecodeCompiledAssetPreviewAsync.
    /// </summary>
    private async Task DecodeOpaquePakAssetPreviewAsync(string relativeAssetPath, int generation)
    {
        var unrealPakExePath = _settingsService.Current.UnrealPakExePath;
        if (string.IsNullOrWhiteSpace(unrealPakExePath))
        {
            if (generation == _assetPreviewGeneration)
            {
                SelectedAssetPreview = "couldn't decode this asset: set UnrealPak.exe's path in Settings first";
            }
            return;
        }

        string? pakFilePath;
        try
        {
            pakFilePath = Directory.GetFiles(_repository.GetFolderPath(FolderName), "*.pak").FirstOrDefault();
        }
        catch (Exception ex)
        {
            if (generation == _assetPreviewGeneration)
            {
                SelectedAssetPreview = $"couldn't decode this asset: {ex.Message}";
            }
            return;
        }

        if (pakFilePath is null)
        {
            if (generation == _assetPreviewGeneration)
            {
                SelectedAssetPreview = "couldn't decode this asset: couldn't find this mod's own .pak file";
            }
            return;
        }

        var cacheDirectory = Path.Combine(_pakPreviewCacheDirectory, FolderName);
        OpaquePakAssetPreviewResult result;
        try
        {
            result = await _opaquePakAssetPreviewService.PreviewAssetAsync(unrealPakExePath, pakFilePath, relativeAssetPath, cacheDirectory);
        }
        catch (Exception ex)
        {
            // Not expected — OpaquePakAssetPreviewService reports every failure it knows about
            // through its own result type — but this is still a background-thread continuation
            // reached from a binding-driven property setter, the same UI boundary
            // DecodeCompiledAssetPreviewAsync's own callers guard, so an unanticipated exception
            // here becomes a status message instead of crashing the app.
            if (generation == _assetPreviewGeneration)
            {
                SelectedAssetPreview = $"couldn't decode this asset: {ex.Message}";
            }
            return;
        }

        if (generation != _assetPreviewGeneration)
        {
            return;
        }

        if (result.PngBytes is not null && TryDecodeImage(result.PngBytes) is { } image)
        {
            SelectedAssetImage = image;
            SelectedAssetPreview = null;
        }
        else if (result.Mesh is not null)
        {
            SelectedAssetMesh = BuildMeshModel(result.Mesh);
            SelectedAssetPreview = null;
        }
        else if (result.Sound is { WavBytes: { } wavBytes })
        {
            try
            {
                SelectedAssetAudioPath = WriteAudioTempFile(wavBytes);
                SelectedAssetPreview = null;
            }
            catch (Exception ex)
            {
                // Same UI boundary as DecodeCompiledAssetPreviewAsync's own sound branch: a real
                // but unlikely failure (disk full, %TEMP% unavailable/locked down).
                SelectedAssetPreview = $"(compiled Unreal asset — couldn't write this sound to a temp file for playback: {ex.Message})";
            }
        }
        else if (result.Sound is { UnsupportedFormatReason: { } unsupportedReason })
        {
            SelectedAssetPreview = $"(compiled Unreal asset — {unsupportedReason})";
        }
        else if (result.Material is not null)
        {
            SelectedAssetMaterialParams = BuildMaterialParamsDisplay(result.Material);
            SelectedAssetPreview = null;
        }
        else
        {
            SelectedAssetPreview = $"couldn't decode this asset: {result.FailureReason ?? "unknown reason"}";
        }
    }

    /// <summary>
    /// Converts plain decoded geometry into a real WPF Model3D — a flat-shaded mesh plus its own
    /// small ambient+directional light rig, so the Files tab's Viewport3D can show it without
    /// depending on any scene lighting of its own. Unreal is Z-up — UpDirection is set to match
    /// when framing the camera, rather than transforming any vertex data to WPF's usual Y-up.
    /// </summary>
    private static Model3D BuildMeshModel(StaticMeshGeometry geometry)
    {
        var mesh = new MeshGeometry3D();
        foreach (var position in geometry.Positions)
        {
            mesh.Positions.Add(new Point3D(position.X, position.Y, position.Z));
        }

        foreach (var index in geometry.TriangleIndices)
        {
            mesh.TriangleIndices.Add(index);
        }

        foreach (var normal in geometry.Normals)
        {
            mesh.Normals.Add(new Vector3D(normal.X, normal.Y, normal.Z));
        }

        foreach (var uv in geometry.TextureCoordinates)
        {
            mesh.TextureCoordinates.Add(new Point(uv.U, uv.V));
        }

        mesh.Freeze();

        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xA0, 0xA6, 0xB0))));
        material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40)), 32));
        material.Freeze();

        var geometryModel = new GeometryModel3D(mesh, material) { BackMaterial = material };
        geometryModel.Freeze();

        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(0x60, 0x60, 0x60)));
        group.Children.Add(new DirectionalLight(Colors.White, new Vector3D(-1, -1, -2)));
        group.Children.Add(geometryModel);
        group.Freeze();
        return group;
    }

    /// <summary>
    /// Converts PakIO's own plain UassetMaterialParams into the WPF-ready MaterialParamsDisplay the
    /// Files tab's own material panel actually binds to — same "convert before exposing" step
    /// BuildMeshModel already does for SelectedAssetMesh. A texture parameter whose own PNG bytes
    /// don't decode to a real image (shouldn't happen — CueUassetMaterialDecoder only ever adds a
    /// texture parameter after already encoding it to PNG itself — but TryDecodeImage's own
    /// contract is "never throw, return null instead") is left out of the list entirely, the exact
    /// same "don't show what didn't decode" convention MaterialTextureParam's own doc comment
    /// already establishes for a texture that couldn't be decoded in the first place.
    /// </summary>
    private static MaterialParamsDisplay BuildMaterialParamsDisplay(UassetMaterialParams materialParams)
    {
        var textures = new List<MaterialTextureParamDisplay>();
        foreach (var texture in materialParams.Textures)
        {
            if (TryDecodeImage(texture.PngBytes) is { } thumbnail)
            {
                textures.Add(new MaterialTextureParamDisplay(texture.Name, thumbnail));
            }
        }

        var scalars = materialParams.Scalars
            .Select(scalar => new MaterialScalarParamDisplay(scalar.Name, scalar.Value))
            .ToList();

        var colors = materialParams.Colors
            .Select(color => new MaterialColorParamDisplay(
                color.Name,
                Color.FromArgb(ToDisplayByte(color.A), ToDisplayByte(color.R), ToDisplayByte(color.G), ToDisplayByte(color.B)),
                $"R {color.R:0.###}  G {color.G:0.###}  B {color.B:0.###}  A {color.A:0.###}"))
            .ToList();

        return new MaterialParamsDisplay(textures, scalars, colors, materialParams.BlendMode, materialParams.ShadingModel);
    }

    /// <summary>Unreal's own linear-color channels aren't clamped to 0..1 by construction (an emissive color, in particular, can legitimately run well above 1) — clamped here before scaling so an out-of-range value becomes a plain solid swatch color instead of an invalid byte.</summary>
    private static byte ToDisplayByte(float channel) => (byte)(Math.Clamp(channel, 0f, 1f) * 255);

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

    /// <summary>Files/readme are only loaded once the user actually selects this item, not for every entry in the library up front.</summary>
    public void EnsureDetailsLoaded()
    {
        if (_detailsLoaded)
        {
            return;
        }

        try
        {
            // One walk of the folder, reused for the asset list, the readme lookup, and (below)
            // the .EXMOD read — the three single-arg forms would otherwise each re-walk the exact
            // same folder from scratch, paid on every single mod selection.
            var files = _repository.ListFolderFiles(FolderName);

            foreach (var path in _repository.ListAssetPaths(FolderName, files))
            {
                AssetPaths.Add(path);
            }

            ReadmeContent = _repository.ReadReadme(FolderName, files);
            LoadThumbnailIfPresent();
            if (ThumbnailImage is null && (NexusModId is not null || CatalogEntryId is not null))
            {
                // Fire-and-forget, same pattern LoadPakContentsCommand already uses below for the
                // opaque-pak case — this synchronous method can't await a network call, and a
                // missing/slow picture is cosmetic, never a reason to block the rest of the details
                // load.
                _ = LoadRemoteThumbnailAsync();
            }

            // An opaque .pak entry has no .EXMOD at all — nothing to format, and ExmodFolder.Read
            // would throw trying. Its own internal files aren't on disk under this mod's folder
            // either (just the bare .pak itself) — LoadPakContentsCommand fetches those separately,
            // via UnrealPak -List, an external process this synchronous method can't await.
            if (!IsOpaquePak)
            {
                // ReadPackageOnly, not Read: Read pulls EVERY one of the mod's binary assets into
                // memory (a real mod's .uasset/.ubulk content can be many MB) purely to reach the
                // .EXMOD's own JSON, which is the only thing the Changes text is built from.
                var package = ExmodFolder.ReadPackageOnly(_repository.GetFolderPath(FolderName), files);
                ChangesContent = ExmodChangesFormatter.Format(package);
            }
            else
            {
                _ = LoadPakContentsCommand.ExecuteAsync(null);
            }

            // Only mark this as done once it actually succeeded — a transient failure (folder
            // locked, deleted out from under the app) should let a later reselect retry rather
            // than permanently pin this mod's Files/Readme tabs empty for the rest of the run.
            // The opaque-pak listing above is deliberately NOT part of this gate — it's its own
            // independently retryable action (see LoadPakContentsAsync's own doc comment).
            _detailsLoaded = true;
        }
        catch (Exception ex)
        {
            // Same UI boundary as SaveMetadata/TryImport/DeleteSelected: this runs from
            // OnSelectedItemChanged, off a binding-driven selection change, so an unhandled
            // exception here would crash the app instead of showing a status message.
            _reportStatus($"Couldn't load mod details: {ex.Message}");
        }
    }

    /// <summary>
    /// Lists an opaque .pak's own internal files via UnrealPak -List — separate from
    /// EnsureDetailsLoaded's own once-only gate (_detailsLoaded) since this needs an external
    /// process and a configured UnrealPak.exe path, either of which can fail independently of
    /// whether the rest of this mod's (empty, for a pak) details loaded fine. Exposed as a real
    /// command, not just called internally, so the Files tab can offer a manual retry after the
    /// user goes and sets UnrealPak.exe's path in Settings.
    /// </summary>
    [RelayCommand]
    private async Task LoadPakContentsAsync()
    {
        var unrealPakExePath = _settingsService.Current.UnrealPakExePath;
        if (string.IsNullOrWhiteSpace(unrealPakExePath))
        {
            PakListingStatus = "Set UnrealPak.exe's path in Settings to list this pak's internal files.";
            return;
        }

        string? pakFilePath;
        try
        {
            pakFilePath = Directory.GetFiles(_repository.GetFolderPath(FolderName), "*.pak").FirstOrDefault();
        }
        catch (Exception ex)
        {
            PakListingStatus = $"Couldn't find this mod's own .pak file: {ex.Message}";
            return;
        }

        if (pakFilePath is null)
        {
            PakListingStatus = "Couldn't find this mod's own .pak file.";
            return;
        }

        try
        {
            PakListingStatus = "Loading pak contents…";
            var paths = await _unrealPakService.ListPakContentsAsync(unrealPakExePath, pakFilePath);

            AssetPaths.Clear();
            foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                AssetPaths.Add(path);
            }

            PakListingStatus = null;
        }
        catch (Exception ex)
        {
            PakListingStatus = $"Couldn't list this pak's contents: {ex.Message}";
        }
    }

    /// <summary>
    /// Best-effort only: a missing or corrupt thumbnail is cosmetic, not a reason to fail the
    /// whole details load the way a Files/Readme read failure would — so failures here are
    /// swallowed rather than routed through _reportStatus.
    /// </summary>
    private void LoadThumbnailIfPresent()
    {
        var thumbnailPath = AssetPaths.FirstOrDefault(p =>
            Path.GetFileNameWithoutExtension(p).Equals("ImageOnly", StringComparison.OrdinalIgnoreCase)
            && ImageExtensions.Contains(Path.GetExtension(p)));

        if (thumbnailPath is null)
        {
            return;
        }

        try
        {
            ThumbnailImage = TryDecodeImage(_repository.ReadAssetContent(FolderName, thumbnailPath));
        }
        catch (Exception)
        {
            // The read itself can fail (locked/vanished file) separately from the decode — both
            // are cosmetic here, per this method's own contract.
        }
    }

    /// <summary>
    /// Only runs when no local bundled preview exists (LoadThumbnailIfPresent already found
    /// nothing) and this mod is linked to a real Nexus mod or Database catalog entry — fetches
    /// that source's own real picture instead of leaving the generic placeholder showing for a mod
    /// that genuinely has a real image out there. Cached to disk by FolderName so a later reselect
    /// (or app restart) never re-fetches the same picture twice; best-effort throughout, same
    /// cosmetic-only contract as LoadThumbnailIfPresent — a missing key, an unreachable network, or
    /// a mod removed from the catalog since just means the placeholder stays showing.
    /// </summary>
    private async Task LoadRemoteThumbnailAsync()
    {
        try
        {
            var cachePath = Path.Combine(_thumbnailCacheDirectory, $"{FolderName}.img");
            if (File.Exists(cachePath))
            {
                ThumbnailImage = TryDecodeImage(await File.ReadAllBytesAsync(cachePath));
                return;
            }

            string? imageUrl = null;
            if (NexusModId is { } modId)
            {
                var apiKey = _credentialStore.Read(CredentialTargets.NexusApiKey);
                if (apiKey is not null)
                {
                    var info = await _nexusApiClient.GetModInfoAsync(apiKey, "icarus", modId);
                    imageUrl = info?.PictureUrl;
                }
            }
            else if (CatalogEntryId is { } catalogId)
            {
                var catalog = await _getOrFetchCatalog();
                imageUrl = catalog.FirstOrDefault(e => e.Id == catalogId)?.ImageUrl;
            }

            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return;
            }

            var bytes = await _httpClient.GetByteArrayAsync(imageUrl);
            var decoded = TryDecodeImage(bytes);
            if (decoded is null)
            {
                return;
            }

            ThumbnailImage = decoded;
            Directory.CreateDirectory(_thumbnailCacheDirectory);
            await File.WriteAllBytesAsync(cachePath, bytes);
        }
        catch (Exception)
        {
            // Cosmetic-only, per this method's own doc comment.
        }
    }

    /// <summary>Decodes image bytes to a frozen bitmap, or null if they aren't a decodable image. Frozen so it can be handed to the UI thread's bindings safely and never re-reads the (already disposed) stream.</summary>
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
            // A file with an image extension that isn't actually a decodable image — treat it as
            // "not an image" rather than an error.
            return null;
        }
    }

    private static bool LooksLikeText(byte[] bytes) =>
        !bytes.Take(Math.Min(bytes.Length, 512)).Any(b => b == 0);
}
