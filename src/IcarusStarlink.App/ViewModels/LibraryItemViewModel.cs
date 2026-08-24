using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Settings;
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
    private readonly ISettingsService _settingsService;
    private readonly Action<string> _reportStatus;
    private readonly Action _onPinnedChanged;
    private readonly DebounceTimer _notesSaveDebounceTimer;
    private bool _detailsLoaded;

    public string FolderName { get; }

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

    /// <summary>Set instead of SelectedAssetPreview when the picked asset is a decodable image — exactly one of the two is ever non-null, which is what the Files tab switches its preview pane on.</summary>
    [ObservableProperty]
    private BitmapImage? _selectedAssetImage;

    /// <summary>
    /// Set from an asset conventionally named "ImageOnly" (any common image extension) if the
    /// mod package has one — a real convention from classic IMM's own format ("Added support for
    /// mods to have ImageOnly.png this will load the image into Mod Manager"), not a guess. Null
    /// for most mods, which don't carry one.
    /// </summary>
    [ObservableProperty]
    private BitmapImage? _thumbnailImage;

    public LibraryItemViewModel(
        LibraryEntry entry, ILibraryRepository repository, IUnrealPakService unrealPakService, ISettingsService settingsService,
        Action<string> reportStatus, Action onPinnedChanged)
    {
        _repository = repository;
        _unrealPakService = unrealPakService;
        _settingsService = settingsService;
        _reportStatus = reportStatus;
        _onPinnedChanged = onPinnedChanged;
        FolderName = entry.FolderName;
        _name = entry.Name;
        _author = entry.Author;
        _version = entry.Version;
        _description = entry.Description;
        _variantLabel = entry.Variant;
        _isOpaquePak = entry.IsOpaquePak;
        _isLocallyEdited = entry.IsLocallyEdited;
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

        if (value is null)
        {
            SelectedAssetPreview = null;
            return;
        }

        if (IsOpaquePak)
        {
            // These paths came from UnrealPak -List, not this mod's own folder on disk —
            // ReadAssetContent (ExmodFolder-based) has nothing to read them from, and they're
            // compiled Unreal binary assets anyway, no different from any other .uasset's
            // "no preview" case elsewhere in this same pane.
            SelectedAssetPreview = "(packed inside this .pak — no preview available for opaque pak entries)";
            return;
        }

        try
        {
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
            foreach (var path in _repository.ListAssetPaths(FolderName))
            {
                AssetPaths.Add(path);
            }

            ReadmeContent = _repository.ReadReadme(FolderName);
            LoadThumbnailIfPresent();

            // An opaque .pak entry has no .EXMOD at all — nothing to format, and ExmodFolder.Read
            // would throw trying. Its own internal files aren't on disk under this mod's folder
            // either (just the bare .pak itself) — LoadPakContentsCommand fetches those separately,
            // via UnrealPak -List, an external process this synchronous method can't await.
            if (!IsOpaquePak)
            {
                // ReadPackageOnly, not Read: Read pulls EVERY one of the mod's binary assets into
                // memory (a real mod's .uasset/.ubulk content can be many MB) purely to reach the
                // .EXMOD's own JSON, which is the only thing the Changes text is built from. Paid
                // on every mod selection before this.
                var package = ExmodFolder.ReadPackageOnly(_repository.GetFolderPath(FolderName));
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
