using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Real IBaseGameContentProvider: lazily mounts a DefaultFileProvider over
/// Path.Combine(IcarusContentPath, "Paks") — SearchOption.TopDirectoryOnly, so it only ever sees
/// the base game's own pakchunk*-WindowsNoEditor.pak files sitting directly in that folder, never
/// recursing into sibling "mods"/"LogicMods" subfolders CueAssetProviderLocator (or a real
/// install) might also have created there — the same "index the folder, don't index the whole
/// world" boundary as everywhere else this app touches the Paks directory.
///
/// Caches the mount as a single shared Task<DefaultFileProvider?>, the same lazy-Task-under-a-lock
/// pattern GameDataIndexCache already establishes for its own two indexes: the first TryLoadExport
/// call (from ANY decoder, for ANY mod) pays the one real mount cost (confirmed ~550ms for all 33
/// paks' trailing directory indices — reads no payload bytes yet), and every call after that,
/// this session, reuses the same mounted provider instance for free. A DI singleton, so "this
/// session" really does mean the app's whole lifetime, not per-decoder or per-preview.
///
/// The same lock also serializes every real lookup + LoadPackage call once a provider exists (not
/// just the mount) — this provider is shared by more than one caller (CueUassetMaterialDecoder's
/// fallback, CueBaseGameIconDecoder), each of which may run on its own background thread, and the
/// underlying DefaultFileProvider's own thread-safety for concurrent LoadPackage calls was never
/// confirmed, so callers are never actually allowed to race here regardless of what any individual
/// caller's own usage pattern happens to do.
///
/// Never throws out of construction (it does no I/O at all until the first TryLoadExport call) or
/// out of a failed mount (IcarusContentPath unset, the Paks folder missing, or the mount itself
/// throwing all collapse to a cached null task, so a failed first attempt doesn't retry a doomed
/// mount on every later call either — same "settle once" contract as the success case).
/// </summary>
public sealed class CueBaseGameContentProvider : IBaseGameContentProvider
{
    // GAME_UE4_27 never varies for this app (Icarus is a fixed UE4.27 title) — same shared
    // instance convention CueAssetProviderLocator already uses for its own mod-folder provider.
    private static readonly VersionContainer Versions = new(EGame.GAME_UE4_27);

    // Every real base-game top-level (Icarus, not Engine) file key carries this exact prefix —
    // confirmed against the real mounted provider's full key set (174,143 keys on a real install):
    // stripping it turns a key into precisely the /Game/-rooted, Content-relative path every real
    // caller (CueBaseGameIconDecoder.ToAssetPath, CueUassetMaterialDecoder.TryGetUnresolvedParentAssetPath)
    // already constructs, with zero collisions across the whole real key set.
    private const string ContentKeyPrefix = "Icarus/Content/";

    private readonly ISettingsService _settingsService;
    private readonly object _lock = new();
    private Task<MountedContent?>? _mountTask;

    public CueBaseGameContentProvider(ISettingsService settingsService) => _settingsService = settingsService;

    public T? TryLoadExport<T>(string assetPath) where T : class
    {
        var mounted = GetOrMountProvider();
        if (mounted is null)
        {
            return null;
        }

        // Same PathBoundaryMatch suffix rule, and the same reason it's needed, as
        // CueAssetProviderLocator's own doc comment already explains: CUE4Parse's real internal
        // file keys carry their own pakchunk-relative prefix ahead of the plain path a caller
        // hands in here.
        var normalizedAssetPath = assetPath.Replace('\\', '/').TrimStart('/');

        // This provider is a single DI singleton shared by every real caller (CueUassetMaterialDecoder's
        // fallback, CueBaseGameIconDecoder), each of which may run on its own background thread — the
        // underlying DefaultFileProvider's own thread-safety for concurrent LoadPackage calls was never
        // confirmed, so the actual lookup + decode is serialized here, under the SAME lock GetOrMountProvider
        // uses. That lock is only ever briefly held elsewhere (just to publish/read _mountTask, never for the
        // mount itself, which runs on its own Task.Run), so this doesn't block the mount from progressing —
        // it only serializes callers against each other once a provider exists.
        lock (_lock)
        {
            if (!mounted.ContentPathIndex.TryGetValue(normalizedAssetPath, out var matchedKey))
            {
                return null;
            }

            try
            {
                var package = mounted.Provider.LoadPackage(matchedKey);
                return package.ExportsLazy.Select(export => export.Value).OfType<T>().FirstOrDefault();
            }
            catch (Exception)
            {
                // A corrupt/unsupported real asset lands here — same "can't decode this, don't throw
                // out of a caller that's already treating this whole call as best-effort" contract
                // every other decoder in this project already uses.
                return null;
            }
        }
    }

    /// <summary>
    /// Blocks on the cached mount Task rather than exposing an async surface of its own — every
    /// real caller today (CueUassetMaterialDecoder's fallback) is itself synchronous, already
    /// running off the UI thread inside LibraryItemViewModel's own Task.Run, the same place
    /// CueAssetProviderLocator's own synchronous (and comparable, if smaller-scale) mount already
    /// runs — so a blocking wait here costs a background thread-pool thread, never the UI thread.
    /// </summary>
    private MountedContent? GetOrMountProvider()
    {
        Task<MountedContent?> mountTask;
        lock (_lock)
        {
            mountTask = _mountTask ??= Task.Run(Mount);
        }

        try
        {
            return mountTask.GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private MountedContent? Mount()
    {
        var contentPath = _settingsService.Current.IcarusContentPath;
        if (string.IsNullOrWhiteSpace(contentPath))
        {
            return null;
        }

        var paksPath = Path.Combine(contentPath, "Paks");
        if (!Directory.Exists(paksPath))
        {
            return null;
        }

        try
        {
            var provider = new DefaultFileProvider(paksPath, SearchOption.TopDirectoryOnly, Versions, StringComparer.OrdinalIgnoreCase);
            provider.Initialize();
            provider.Mount();

            // Built once, right after a successful mount (still on this same background Task, so
            // it adds to the one-time mount cost rather than to any later TryLoadExport call) —
            // replaces what used to be an O(n) provider.Files.Keys.FirstOrDefault(EndsWithSegmentBoundary)
            // scan on every single lookup (measured ~500ms of pure overhead across a realistic
            // ~150-lookup save-slot load against a real install) with an O(1) dictionary lookup.
            // A key without the Icarus/Content/ prefix (the small Engine/* subset — no real caller
            // path can ever match one of those anyway) is indexed under its own full key instead of
            // being dropped, so nothing is silently lost even though nothing here can reach it.
            var contentPathIndex = new Dictionary<string, string>(provider.Files.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var key in provider.Files.Keys)
            {
                var strippedKey = key.StartsWith(ContentKeyPrefix, StringComparison.OrdinalIgnoreCase)
                    ? key[ContentKeyPrefix.Length..]
                    : key;
                contentPathIndex[strippedKey] = key;
            }

            return new MountedContent(provider, contentPathIndex);
        }
        catch (Exception)
        {
            // A corrupt/unreadable install, a permissions problem, or any other real mount
            // failure — degrades to "base-game content unavailable this session" rather than
            // throwing out of the Task every TryLoadExport call is waiting on.
            return null;
        }
    }

    private sealed record MountedContent(DefaultFileProvider Provider, IReadOnlyDictionary<string, string> ContentPathIndex);
}
