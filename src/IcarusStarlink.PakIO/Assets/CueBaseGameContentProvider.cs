using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Versions;
using IcarusStarlink.Core.Memory;
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
/// throwing all collapse to a cached null task). A failed mount isn't final, though (an audit
/// finding: at first launch the game path is still empty, and the risk pill then never appeared
/// all session): it's tried again once the Content path changes, or after RetryFailedMountAfter —
/// never on every call, so a doomed mount isn't redone per lookup.
/// </summary>
public sealed class CueBaseGameContentProvider : IBaseGameContentProvider, IGameAssetCatalog, IIdleReleasable
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

    /// <summary>How long a failed mount stands before a lookup tries again (the game drive plugged in, Icarus installed).</summary>
    private static readonly TimeSpan RetryFailedMountAfter = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settingsService;
    private readonly object _lock = new();
    private Task<MountedContent?>? _mountTask;

    /// <summary>The Content path the current mount was made for, and when it was started.</summary>
    private string? _mountedForPath;
    private DateTime _mountStartedUtc;

    /// <summary>The last lookup (under _lock): the mount is let go after it has stood unused a while.</summary>
    private DateTime _lastUsedUtc;

    public CueBaseGameContentProvider(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        IdleMemory.Register(this);
    }

    /// <summary>
    /// The mounted game paks hold about 100 MB (every file's index entry and a reader per pak) and
    /// mount again in well under a second, so they're let go while the app sits unused (the user
    /// is playing). The next lookup mounts them again.
    /// </summary>
    public bool ReleaseIfIdle(TimeSpan idleFor)
    {
        lock (_lock)
        {
            if (_mountTask is not { IsCompletedSuccessfully: true } task || task.Result is not { } mounted
                || DateTime.UtcNow - _lastUsedUtc < idleFor)
            {
                return false;
            }

            _mountTask = null;
            Release(mounted);
            return true;
        }
    }

    /// <summary>Mounts let go so far (ReleaseIfIdle, or a Content path change): for tests.</summary>
    internal int ReleasedMountCount { get; private set; }

    /// <summary>
    /// Every use of the provider happens under _lock and checks Released first, so nothing is
    /// reading from it while it's disposed here. Under _lock.
    /// </summary>
    private void Release(MountedContent mounted)
    {
        if (mounted.Released)
        {
            return;
        }

        mounted.Released = true;
        mounted.Provider.Dispose();
        ReleasedMountCount++;
    }

    public T? TryLoadExport<T>(string assetPath) where T : class =>
        TryLoadExportAndProject<T, T>(assetPath, static export => export);

    public bool? HasAsset(string assetPath)
    {
        var mounted = GetOrMountProvider();
        if (mounted is null)
        {
            return null;
        }

        // The index is built once at mount and never changed after, so reading it needs no lock.
        return mounted.ContentPathIndex.ContainsKey(assetPath.Replace('\\', '/').TrimStart('/'));
    }

    public BlueprintSummary? TrySummarizeBlueprint(string assetPath)
    {
        // Twice at most: the mount may be let go (ReleaseIfIdle) between getting it and locking.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var summary = TrySummarizeBlueprintOnce(assetPath, out var released);
            if (!released)
            {
                return summary;
            }
        }

        return null;
    }

    private BlueprintSummary? TrySummarizeBlueprintOnce(string assetPath, out bool released)
    {
        released = false;
        var mounted = GetOrMountProvider();
        if (mounted is null)
        {
            return null;
        }

        lock (_lock)
        {
            if (mounted.Released)
            {
                released = true;
                return null;
            }

            if (!mounted.ContentPathIndex.TryGetValue(assetPath.Replace('\\', '/').TrimStart('/'), out var matchedKey))
            {
                return null;
            }

            // The game's files don't change while it's mounted: each blueprint is read once per
            // mount, not on every risk pass (one runs after every Library change).
            if (mounted.Summaries.TryGetValue(matchedKey, out var known))
            {
                return known;
            }

            BlueprintSummary? summary;
            try
            {
                summary = BlueprintComparer.Summarize(mounted.Provider.LoadPackage(matchedKey));
            }
            catch (Exception)
            {
                summary = null;
            }

            mounted.Summaries[matchedKey] = summary;
            return summary;
        }
    }

    public bool? HasPackage(string packagePath) => GetOrMountProvider() is { } mounted ? PackageKey(mounted, packagePath) is not null : null;

    public IReadOnlyDictionary<string, string>? Objects(string packagePath) =>
        UnderLock(mounted =>
        {
            if (PackageKey(mounted, packagePath) is not { } key)
            {
                return null;
            }

            // Only the package's header is read (its .uasset: the objects' data is in the .uexp),
            // once per package per mount.
            if (!mounted.Objects.TryGetValue(key, out var objects))
            {
                try
                {
                    objects = UnrealPackageHeader.TryRead(mounted.Provider.Files[key].Read())?.ExportedObjects();
                }
                catch (Exception)
                {
                    objects = null;
                }

                mounted.Objects[key] = objects;
            }

            return objects;
        });

    public IReadOnlyList<string> PackagesNamed(string assetName) =>
        UnderLock(mounted =>
        {
            mounted.PackagesByName ??= IndexByName(mounted.ContentPathIndex);
            return mounted.PackagesByName.TryGetValue(assetName, out var packages) ? packages : (IReadOnlyList<string>)[];
        }) ?? [];

    /// <summary>The mounted file key of a package ("/Game/ASS/DEP/M_X" or "/Engine/…"), whichever of .uasset/.umap the game has; null when it has neither.</summary>
    private static string? PackageKey(MountedContent mounted, string packagePath)
    {
        var path = packagePath.Replace('\\', '/');
        string relative;
        if (path.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase))
        {
            relative = path["/Game/".Length..];
        }
        else if (path.StartsWith("/Engine/", StringComparison.OrdinalIgnoreCase))
        {
            relative = "Engine/Content/" + path["/Engine/".Length..];
        }
        else
        {
            return null;
        }

        return mounted.ContentPathIndex.TryGetValue(relative + ".uasset", out var key) || mounted.ContentPathIndex.TryGetValue(relative + ".umap", out key) ? key : null;
    }

    /// <summary>Every game package by its asset name, for finding where the game moved one.</summary>
    private static Dictionary<string, List<string>> IndexByName(IReadOnlyDictionary<string, string> contentPathIndex)
    {
        var byName = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, key) in contentPathIndex)
        {
            if (!path.EndsWith(".uasset", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".umap", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var withoutExtension = path[..path.LastIndexOf('.')];
            string package;
            if (key.StartsWith(ContentKeyPrefix, StringComparison.OrdinalIgnoreCase))
            {
                package = "/Game/" + withoutExtension;
            }
            else if (withoutExtension.StartsWith("Engine/Content/", StringComparison.OrdinalIgnoreCase))
            {
                package = "/Engine/" + withoutExtension["Engine/Content/".Length..];
            }
            else
            {
                continue; // an engine plugin's content: not something a mod's reference is checked against
            }

            var name = package[(package.LastIndexOf('/') + 1)..];
            if (!byName.TryGetValue(name, out var list))
            {
                byName[name] = list = [];
            }

            list.Add(package);
        }

        return byName;
    }

    /// <summary>Runs <paramref name="use"/> on the mounted content under _lock (twice at most: the mount may be let go between getting it and locking); null when nothing is mounted.</summary>
    private T? UnderLock<T>(Func<MountedContent, T?> use) where T : class
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (GetOrMountProvider() is not { } mounted)
            {
                return null;
            }

            lock (_lock)
            {
                if (!mounted.Released)
                {
                    return use(mounted);
                }
            }
        }

        return null;
    }

    public TResult? TryLoadExportAndProject<T, TResult>(string assetPath, Func<T, TResult> project)
        where T : class where TResult : class
    {
        // Twice at most: the mount may be let go (ReleaseIfIdle) between getting it and locking.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = TryLoadExportAndProjectOnce(assetPath, project, out var released);
            if (!released)
            {
                return result;
            }
        }

        return null;
    }

    private TResult? TryLoadExportAndProjectOnce<T, TResult>(string assetPath, Func<T, TResult> project, out bool released)
        where T : class where TResult : class
    {
        released = false;
        var mounted = GetOrMountProvider();
        if (mounted is null)
        {
            return null;
        }

        // Looked up exactly (ignoring case) in the index Mount built, whose keys have CUE4Parse's
        // "Icarus/Content/" prefix taken off so they match the plain path a caller hands in here.
        var normalizedAssetPath = assetPath.Replace('\\', '/').TrimStart('/');

        // This provider is a single DI singleton shared by every real caller (CueUassetMaterialDecoder's
        // fallback, CueBaseGameIconDecoder), each of which may run on its own background thread — the
        // underlying DefaultFileProvider's own thread-safety for concurrent LoadPackage calls was never
        // confirmed, so the actual lookup + decode + project is ALL serialized here, under the SAME
        // lock GetOrMountProvider uses. project runs INSIDE this lock, not after it's released — a
        // caller whose project reads a lazily-resolved cross-package reference off export (exactly
        // what CueUassetMaterialDecoder's GetParams does) would otherwise trigger CUE4Parse's own
        // on-first-access resolution against this SAME shared mounted.Provider from outside the lock,
        // free to race a concurrent TryLoadExportAndProject call from another thread doing its own
        // LoadPackage against the very same provider instance — the exact race this lock exists to
        // prevent (see this class's own top doc comment). That lock is only ever briefly held
        // elsewhere (just to publish/read _mountTask, never for the mount itself, which runs on its
        // own Task.Run), so this doesn't block the mount from progressing — it only serializes
        // callers against each other once a provider exists.
        lock (_lock)
        {
            if (mounted.Released)
            {
                released = true;
                return null;
            }

            if (!mounted.ContentPathIndex.TryGetValue(normalizedAssetPath, out var matchedKey))
            {
                return null;
            }

            try
            {
                var package = mounted.Provider.LoadPackage(matchedKey);
                var export = package.ExportsLazy.Select(e => e.Value).OfType<T>().FirstOrDefault();
                return export is null ? null : project(export);
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
            var contentPath = _settingsService.Current.IcarusContentPath;
            if (_mountTask is null || NeedsRemount(_mountTask, contentPath))
            {
                // The mount being replaced (another Content path) is let go once it's done, or it
                // would hold its ~100 MB and a reader on every pak of the old install all session.
                _mountTask?.ContinueWith(
                    done =>
                    {
                        if (done.IsCompletedSuccessfully && done.Result is { } old)
                        {
                            lock (_lock)
                            {
                                Release(old);
                            }
                        }
                    },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _mountedForPath = contentPath;
                _mountStartedUtc = DateTime.UtcNow;
                _mountTask = Task.Run(Mount);
            }

            mountTask = _mountTask;
            _lastUsedUtc = DateTime.UtcNow;
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

    /// <summary>A different Content path than the mount was made for, or a failed mount that has stood long enough to try again. Under _lock.</summary>
    private bool NeedsRemount(Task<MountedContent?> mountTask, string? contentPath)
    {
        if (!string.Equals(_mountedForPath, contentPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return mountTask.IsCompleted
            && (mountTask.IsFaulted || mountTask.Result is null)
            && DateTime.UtcNow - _mountStartedUtc >= RetryFailedMountAfter;
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
            // Function code too, for comparing a mod's copy of a game blueprint with the game's.
            provider.ReadScriptData = true;
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

    private sealed record MountedContent(DefaultFileProvider Provider, IReadOnlyDictionary<string, string> ContentPathIndex)
    {
        /// <summary>Blueprint summaries read so far (under _lock).</summary>
        public Dictionary<string, BlueprintSummary?> Summaries { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Let go by ReleaseIfIdle: its provider is disposed (set and read under _lock).</summary>
        public bool Released { get; set; }

        /// <summary>Each package's objects read so far, by file key (under _lock).</summary>
        public Dictionary<string, IReadOnlyDictionary<string, string>?> Objects { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every package by asset name, built on first use (under _lock).</summary>
        public Dictionary<string, List<string>>? PackagesByName { get; set; }
    }
}
