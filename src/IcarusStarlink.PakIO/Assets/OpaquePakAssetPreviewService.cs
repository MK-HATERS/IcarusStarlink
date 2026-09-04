using System.Collections.Concurrent;
using IcarusStarlink.PakIO.Pak;

namespace IcarusStarlink.PakIO.Assets;

/// <summary>
/// Wires IUnrealPakService.ExtractPakAsync to the same five decoders a regular EXMOD mod's own
/// preview already goes through (CueAssetProviderLocator needs a real folder on disk to index —
/// it can't be pointed at bytes still packed inside a .pak), so an opaque pak's own .uasset
/// entries get a genuine decode attempt instead of an unconditional "no preview available".
///
/// IUnrealPakService.ExtractPakAsync has no scoped/selective-extract form (confirmed by reading
/// its own interface) — it always runs UnrealPak.exe's own `-Extract`, which pulls a pak's ENTIRE
/// contents, not just the one asset being previewed. For a large opaque pak (the base game's own
/// pak-chunks run well into the hundreds of MB) that's a real cost to pay just to preview one
/// texture — cached per-pak below so it's paid once, not on every single asset the user clicks
/// through in the Files tab, but the first click against any given pak still extracts everything.
/// A scoped extract (if UnrealPak.exe itself ever grew one) would remove this cost entirely; this
/// class is the one place that cost would need to change.
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
