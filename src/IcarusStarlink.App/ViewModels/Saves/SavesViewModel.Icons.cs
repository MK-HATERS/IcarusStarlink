using System.IO;
using System.Windows.Media.Imaging;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.App.ViewModels;

public sealed partial class SavesViewModel
{
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
        var requests = new List<(string? IconPath, bool Trim, Action<BitmapImage> SetIcon)>();
        foreach (var item in MetaInventoryItems)
        {
            requests.Add((item.IconPath, false, bitmap => item.Icon = bitmap));
        }

        foreach (var mount in Mounts)
        {
            requests.Add((mount.IconPath, false, bitmap => mount.Icon = bitmap));
        }

        // Bestiary pictures are mostly empty around a small creature: cut to the creature (PngTrim).
        foreach (var creature in BestiaryEntries)
        {
            requests.Add((creature.ImagePath, true, bitmap => creature.Icon = bitmap));
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
    private async Task LoadIconsAsync(IReadOnlyList<(string? IconPath, bool Trim, Action<BitmapImage> SetIcon)> requests, int generation)
    {
        foreach (var (iconPath, trim, setIcon) in requests)
        {
            if (generation != _iconLoadGeneration)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(iconPath))
            {
                continue;
            }

            var cacheKey = trim ? "trimmed:" + iconPath : iconPath;
            if (!_iconCache.TryGetValue(cacheKey, out var bitmap))
            {
                var pngBytes = await Task.Run(() =>
                    _baseGameIconDecoder.TryDecodeIconToPng(iconPath) is { } png ? (trim ? PngTrim.TrimTransparentBorder(png) : png) : null);
                bitmap = pngBytes is null ? null : TryDecodeImage(pngBytes);
                _iconCache[cacheKey] = bitmap;
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
}
