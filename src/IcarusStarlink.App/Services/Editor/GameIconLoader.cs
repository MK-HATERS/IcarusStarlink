using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IcarusStarlink.PakIO.Assets;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Game item icons for the Editor page, decoded on one background thread (the base-game provider
/// was never confirmed safe for concurrent decodes; see SavesViewModel.LoadIconsAsync) and kept for
/// the session. Newest request first: the list asks as rows scroll into view, so what's on screen
/// now loads before what was scrolled past. Only the newest MaxQueued wait; older ones are dropped
/// and asked again when their row shows again. An empty result (a broken texture, or the game's
/// paks not readable right now) is kept for RetryEmptyAfter only, then decoded again.
/// </summary>
public sealed class GameIconLoader(IBaseGameIconDecoder decoder)
{
    internal const int MaxQueued = 120;

    /// <summary>How long an icon that came back empty is not asked for again (the base-game provider retries a failed mount after 30 s too).</summary>
    internal static readonly TimeSpan RetryEmptyAfter = TimeSpan.FromSeconds(30);

    /// <summary>The clock for RetryEmptyAfter. Tests swap in their own.</summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    private readonly object _gate = new();
    private readonly Dictionary<string, (ImageSource? Image, DateTime At)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Pending> _queue = new();
    private bool _running;

    private sealed record Pending(string Path, Action<ImageSource> Loaded, Action Dropped, SynchronizationContext? Context);

    /// <summary>A cached result to use: an icon, or an empty result that isn't old enough to try again. Call under _gate.</summary>
    private bool TryCached(string path, out ImageSource? image)
    {
        image = null;
        if (!_cache.TryGetValue(path, out var entry) || (entry.Image is null && UtcNow() - entry.At >= RetryEmptyAfter))
        {
            return false;
        }

        image = entry.Image;
        return true;
    }

    /// <summary>
    /// Asks for an icon. <paramref name="loaded"/> runs on the asking thread once it's decoded;
    /// <paramref name="dropped"/> when it gave way to newer requests, or there's no icon for it
    /// right now: ask again when its row shows.
    /// </summary>
    public void Request(string path, Action<ImageSource> loaded, Action dropped)
    {
        ImageSource? cached;
        bool hit;
        lock (_gate)
        {
            hit = TryCached(path, out cached);
            if (!hit)
            {
                _queue.AddFirst(new Pending(path, loaded, dropped, SynchronizationContext.Current));
                while (_queue.Count > MaxQueued)
                {
                    var oldest = _queue.Last!.Value;
                    _queue.RemoveLast();
                    Post(oldest.Context, oldest.Dropped);
                }

                if (!_running)
                {
                    _running = true;
                    _ = Task.Run(Work);
                }
            }
        }

        if (!hit)
        {
            return;
        }

        if (cached is not null)
        {
            loaded(cached);
        }
        else
        {
            Post(SynchronizationContext.Current, dropped);
        }
    }

    private void Work()
    {
        while (true)
        {
            Pending next;
            lock (_gate)
            {
                if (_queue.First is not { } first)
                {
                    _running = false;
                    return;
                }

                next = first.Value;
                _queue.RemoveFirst();
            }

            ImageSource? image;
            bool known;
            lock (_gate)
            {
                known = TryCached(next.Path, out image); // asked twice before the first was decoded
            }

            if (!known)
            {
                image = Decode(next.Path);
                lock (_gate)
                {
                    _cache[next.Path] = (image, UtcNow());
                }
            }

            if (image is not null)
            {
                var done = image;
                Post(next.Context, () => next.Loaded(done));
            }
            else
            {
                // The row resets and asks again when it next shows (after RetryEmptyAfter, a fresh decode).
                Post(next.Context, next.Dropped);
            }
        }
    }

    private ImageSource? Decode(string path)
    {
        try
        {
            if (decoder.TryDecodeIconToPng(path) is not { } png)
            {
                return null;
            }

            using var stream = new MemoryStream(png);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 64; // shown at 52 px at most (the item header): still sharp there, and keeps 3,000 icons small
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null; // a broken texture just has no icon
        }
    }

    private static void Post(SynchronizationContext? context, Action action)
    {
        if (context is null)
        {
            action();
        }
        else
        {
            context.Post(_ => action(), null);
        }
    }
}
