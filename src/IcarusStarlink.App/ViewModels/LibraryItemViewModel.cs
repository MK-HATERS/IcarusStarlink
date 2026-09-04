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
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    private readonly ILibraryRepository _repository;
    private readonly IUnrealPakService _unrealPakService;
    private readonly IUassetTextureDecoder _uassetTextureDecoder;
    private readonly IUassetStaticMeshDecoder _uassetStaticMeshDecoder;
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

    /// <summary>Set instead of SelectedAssetPreview when the picked asset is a decodable image — exactly one of SelectedAssetImage/SelectedAssetMesh/SelectedAssetPreview is ever non-null, which is what the Files tab switches its preview pane on.</summary>
    [ObservableProperty]
    private BitmapImage? _selectedAssetImage;

    /// <summary>Set instead of SelectedAssetPreview/SelectedAssetImage when the picked asset is a decodable static mesh — a Model3D ready to bind straight into the Files tab's Viewport3D.</summary>
    [ObservableProperty]
    private Model3D? _selectedAssetMesh;

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
        IOpaquePakAssetPreviewService opaquePakAssetPreviewService, ISettingsService settingsService,
        INexusApiClient nexusApiClient, ICredentialStore credentialStore, HttpClient httpClient, string thumbnailCacheDirectory,
        string pakPreviewCacheDirectory,
        Func<Task<IReadOnlyList<CatalogEntry>>> getOrFetchCatalog, Action<string> reportStatus, Action onPinnedChanged)
    {
        _repository = repository;
        _unrealPakService = unrealPakService;
        _uassetTextureDecoder = uassetTextureDecoder;
        _uassetStaticMeshDecoder = uassetStaticMeshDecoder;
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
    /// A .uasset is compiled Unreal binary, not something the flat-image/text branches above can
    /// show — this decodes it as a real Unreal texture, then (if that fails) a real static mesh,
    /// via CUE4Parse. Runs off the UI thread since indexing the mod's own asset folder and
    /// decoding either one both take real time; the generation check discards a stale result if
    /// the user has already selected something else by the time it finishes.
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

        var (pngBytes, meshGeometry) = await Task.Run(() =>
        {
            var png = _uassetTextureDecoder.TryDecodeToPng(modFolderPath, relativeAssetPath);
            var mesh = png is null ? _uassetStaticMeshDecoder.TryDecodeStaticMesh(modFolderPath, relativeAssetPath) : null;
            return (png, mesh);
        });

        if (generation != _assetPreviewGeneration)
        {
            return;
        }

        if (pngBytes is not null && TryDecodeImage(pngBytes) is { } image)
        {
            SelectedAssetImage = image;
            SelectedAssetPreview = null;
        }
        else if (meshGeometry is not null)
        {
            SelectedAssetMesh = BuildMeshModel(meshGeometry);
            SelectedAssetPreview = null;
        }
        else
        {
            SelectedAssetPreview = "(compiled Unreal asset — not a texture or static mesh, or couldn't be decoded — no preview available)";
        }
    }

    /// <summary>
    /// Real preview attempt for a .uasset packed inside an opaque/prebuilt-imported pak.
    /// DecodeCompiledAssetPreviewAsync above can point its decoders straight at this mod's own
    /// folder on disk because a regular EXMOD mod's assets already live there loose — an opaque
    /// pak's own assets don't; they're only ever packed inside its single .pak file, which
    /// CueAssetProviderLocator (what both decoders go through) can't index directly. So this
    /// extracts that pak first — cached under this app's own Cache directory, same convention
    /// ThumbnailImage's own remote-picture cache already uses, keyed by this mod's FolderName so
    /// repeated preview clicks against the same pak don't each pay a fresh whole-pak extract (see
    /// OpaquePakAssetPreviewService's own doc comment for why it's whole-pak, not scoped, and
    /// what that costs the first time) — then runs the exact same two decoders through it. Same
    /// generation-based staleness guard as DecodeCompiledAssetPreviewAsync.
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
