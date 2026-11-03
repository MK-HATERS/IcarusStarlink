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
/// and asked again when their row shows again.
/// </summary>
public sealed class GameIconLoader(IBaseGameIconDecoder decoder)
{
    internal const int MaxQueued = 120;

    private readonly object _gate = new();
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Pending> _queue = new();
    private bool _running;

    private sealed record Pending(string Path, Action<ImageSource> Loaded, Action Dropped, SynchronizationContext? Context);

    /// <summary>
    /// Asks for an icon. <paramref name="loaded"/> runs on the asking thread once it's decoded (not
    /// at all when the path doesn't decode); <paramref name="dropped"/> when it gave way to newer
    /// requests before being decoded.
    /// </summary>
    public void Request(string path, Action<ImageSource> loaded, Action dropped)
    {
        ImageSource? cached;
        bool hit;
        lock (_gate)
        {
            hit = _cache.TryGetValue(path, out cached);
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

        if (hit && cached is not null)
        {
            loaded(cached);
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
                known = _cache.TryGetValue(next.Path, out image); // asked twice before the first was decoded
            }

            if (!known)
            {
                image = Decode(next.Path);
                lock (_gate)
                {
                    _cache[next.Path] = image;
                }
            }

            if (image is not null)
            {
                var done = image;
                Post(next.Context, () => next.Loaded(done));
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
            bitmap.DecodePixelWidth = 64; // shown at 32 px at most: keeps 3,000 icons small
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
