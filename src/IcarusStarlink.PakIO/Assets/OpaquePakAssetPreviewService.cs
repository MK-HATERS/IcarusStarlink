using System.Collections.Concurrent;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Wires IUnrealPakService.ExtractPakAsync to the same five decoders a regular EXMOD mod's own
/// preview already goes through (CueAssetProviderLocator needs a real folder on disk to index —
/// it can't be pointed at bytes still packed inside a .pak), so an opaque pak's own .uasset
/// entries get a genuine decode attempt instead of an unconditional "no preview available".
///
/// IUnrealPakService.ExtractPakAsync DOES now have an optional scoped/selective-extract form (a
/// `filter` parameter, confirmed live against the real bundled UnrealPak.exe's own `-Filter` flag)
/// — but this call site deliberately still passes none, so it always extracts a pak's ENTIRE
/// contents rather than just the one asset being previewed. That's a real cost for a large opaque
/// mod pak, but it's paid at most once per pak (cached below, keyed on a size+mtime stamp), not on
/// every asset the user clicks through in the Files tab. Switching this call site to a scoped
/// filter isn't a safe drop-in: a material's own Parent chain, or a skeletal mesh's own skeleton
/// reference, can point at ANOTHER asset inside this same opaque pak, and a filter scoped to just
/// relativeAssetPath's own base name would silently leave that companion asset unextracted —
/// exactly the kind of thing that already happens for base-game Content\Paks (see
/// CueUassetMaterialDecoder's own doc comment) but has no equivalent fallback here, since the
/// asset a scoped filter dropped would need to come from THIS pak, not the base game's. Doing this
/// safely would mean discovering an asset's own cross-references before deciding what to extract,
/// which the decoders here don't currently expose — a real follow-up, not done here.
/// </summary>
public sealed class OpaquePakAssetPreviewService(
    IUnrealPakService unrealPakService, IUassetTextureDecoder textureDecoder, IUassetStaticMeshDecoder meshDecoder,
    IUassetSkeletalMeshDecoder skeletalMeshDecoder, IUassetSoundDecoder soundDecoder, IUassetMaterialDecoder materialDecoder)
    : IOpaquePakAssetPreviewService
{
    // Marks a cache directory as "this exact pak, already fully extracted here" — a cheap
    // size+mtime stamp, not a full hash, is enough to notice the underlying .pak changed since
    // this cache entry was populated (e.g. a folder name reused after a delete+reimport) without
    // re-reading the whole pak on every single preview click just to check.
    private const string CacheStampFileName = ".source_pak_stamp";

    // Keyed per cache directory (one per distinct opaque pak), not one lock for the whole
    // service: a user previewing assets from two different opaque mods at once shouldn't have
    // one block the other. Guards against two concurrent calls for the SAME pak (e.g. two quick
    // clicks on different assets inside one still-uncached pak) racing to delete/recreate/extract
    // into the same directory at once.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ExtractionLocks =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<OpaquePakAssetPreviewResult> PreviewAssetAsync(
        string unrealPakExePath, string pakFilePath, string relativeAssetPath, string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        FileInfo pakInfo;
        try
        {
            pakInfo = new FileInfo(pakFilePath);
        }
        catch (Exception ex)
        {
            return OpaquePakAssetPreviewResult.Failed($"couldn't read this mod's own .pak file: {ex.Message}");
        }

        if (!pakInfo.Exists)
        {
            return OpaquePakAssetPreviewResult.Failed($"couldn't find this mod's own .pak file at '{pakFilePath}'");
        }

        var stamp = $"{pakInfo.Length}:{pakInfo.LastWriteTimeUtc.Ticks}";
        var markerPath = Path.Combine(cacheDirectory, CacheStampFileName);

        var extractionLock = ExtractionLocks.GetOrAdd(Path.GetFullPath(cacheDirectory), static _ => new SemaphoreSlim(1, 1));
        await extractionLock.WaitAsync(cancellationToken);
        try
        {
            var cacheIsFresh = File.Exists(markerPath)
                && await File.ReadAllTextAsync(markerPath, cancellationToken) == stamp;

            if (!cacheIsFresh)
            {
                try
                {
                    // Stale cache from a different pak (or a half-finished previous extract) —
                    // wiped rather than extracted on top of, same "replace, don't merge"
                    // reasoning UnrealPakService.ExtractDataPakAsync already uses for its own
                    // cache, so a leftover file from a since-removed asset in an old version of
                    // this pak can never be mistaken for still being genuinely present.
                    if (Directory.Exists(cacheDirectory))
                    {
                        Directory.Delete(cacheDirectory, recursive: true);
                    }
                    Directory.CreateDirectory(cacheDirectory);

                    await unrealPakService.ExtractPakAsync(unrealPakExePath, pakFilePath, cacheDirectory, cancellationToken);
                    await File.WriteAllTextAsync(markerPath, stamp, cancellationToken);
                }
                catch (Exception ex)
                {
                    return OpaquePakAssetPreviewResult.Failed($"couldn't extract this pak to preview its contents: {ex.Message}");
                }
            }
        }
        finally
        {
            extractionLock.Release();
        }

        // The actual CUE4Parse decode is real CPU work (same reason LibraryItemViewModel's own
        // regular-EXMOD decode path runs it via Task.Run) — kept off whatever thread called this
        // rather than assuming the caller already hopped off the UI thread.
        return await Task.Run(() =>
        {
            try
            {
                // Same order LibraryItemViewModel.DecodeCompiledAsset already uses for a regular
                // EXMOD mod's own preview: texture first, then static mesh, then skeletal mesh
                // (both mesh kinds share the one Mesh slot), then sound, then material — the first
                // decoder to actually produce something short-circuits every decoder still after it.
                var png = textureDecoder.TryDecodeToPng(cacheDirectory, relativeAssetPath);
                if (png is not null)
                {
                    return OpaquePakAssetPreviewResult.Decoded(pngBytes: png);
                }

                var mesh = meshDecoder.TryDecodeStaticMesh(cacheDirectory, relativeAssetPath)
                    ?? skeletalMeshDecoder.TryDecodeSkeletalMesh(cacheDirectory, relativeAssetPath);
                if (mesh is not null)
                {
                    return OpaquePakAssetPreviewResult.Decoded(mesh: mesh);
                }

                var sound = soundDecoder.TryDecodeAudio(cacheDirectory, relativeAssetPath);
                if (sound is not null)
                {
                    return OpaquePakAssetPreviewResult.Decoded(sound: sound);
                }

                var material = materialDecoder.TryDecodeMaterial(cacheDirectory, relativeAssetPath);
                return material is not null
                    ? OpaquePakAssetPreviewResult.Decoded(material: material)
                    : OpaquePakAssetPreviewResult.Failed("not a texture, mesh, sound, or material, or couldn't be decoded");
            }
            catch (Exception ex)
            {
                // Belt-and-suspenders: every decoder already catches internally and returns null
                // rather than throw (see their own doc comments) — this only guards against a
                // future change to that contract turning into an unhandled exception here.
                return OpaquePakAssetPreviewResult.Failed($"couldn't decode this asset: {ex.Message}");
            }
        }, cancellationToken);
    }
}
