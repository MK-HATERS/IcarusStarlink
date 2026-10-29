using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using IcarusStarlink.Core.Memory;

namespace IcarusStarlink.App.Services;

/// <summary>
/// Gives memory back while Starlink sits in the background (usually: the user is playing Icarus).
/// An idle .NET app makes almost no new objects, so the garbage collector never runs a full
/// collection and everything a merge, a check or a page left behind just stays: measured on a real
/// install, 1.13 GB of which only about 200 MB was still in use. Shortly after the window loses
/// focus, and every few minutes while it stays in the background, this lets go of caches nothing
/// has used for a while (IdleMemory: the mounted game paks, the merge queue's mods, the fix
/// indexes; each reads itself again when next needed), runs one full compacting collection, and
/// hands the freed pages back to Windows. Nothing runs while the user is using the app.
/// </summary>
internal sealed class BackgroundMemoryTrimmer
{
    private static readonly TimeSpan FirstTrimAfter = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(2);

    /// <summary>A cache is let go once nothing has used it for this long.</summary>
    private static readonly TimeSpan CacheIdleFor = TimeSpan.FromMinutes(2);

    /// <summary>Less new garbage than this since the last trim, and nothing let go: not worth a full collection.</summary>
    private const long GrowthWorthCollecting = 64L * 1024 * 1024;

    private readonly DispatcherTimer _timer;
    private long _committedAfterLastTrim;
    private int _trimming;

    public BackgroundMemoryTrimmer(Application app)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background, app.Dispatcher) { Interval = FirstTrimAfter };
        _timer.Tick += (_, _) => OnTick();
        app.Deactivated += (_, _) =>
        {
            _timer.Interval = FirstTrimAfter;
            _timer.Start();
        };
        app.Activated += (_, _) => _timer.Stop();
    }

    private void OnTick()
    {
        _timer.Interval = CheckEvery;
        if (Interlocked.Exchange(ref _trimming, 1) == 1)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Trim();
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "Background memory trim failed");
            }
            finally
            {
                Volatile.Write(ref _trimming, 0);
            }
        });
    }

    private void Trim()
    {
        var released = IdleMemory.ReleaseIdle(CacheIdleFor);
        var before = GC.GetGCMemoryInfo().TotalCommittedBytes;
        if (released == 0 && before - _committedAfterLastTrim < GrowthWorthCollecting)
        {
            return;
        }

        // Large arrays are never moved by a normal collection; the holes they leave were about
        // 110 MB of the measured heap.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        _committedAfterLastTrim = GC.GetGCMemoryInfo().TotalCommittedBytes;

        // Windows takes the pages back now rather than whenever it next runs short; they come
        // back from memory (not disk) as soon as the window is used again.
        using var self = Process.GetCurrentProcess();
        _ = EmptyWorkingSet(self.Handle);
        Serilog.Log.Debug("Background memory trim: {Released} cache(s) let go, GC committed {Before} MB -> {After} MB",
            released, before / (1024 * 1024), _committedAfterLastTrim / (1024 * 1024));
    }

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr process);
}
