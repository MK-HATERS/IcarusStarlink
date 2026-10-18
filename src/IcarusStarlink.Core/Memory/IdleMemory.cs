namespace IcarusStarlink.Core.Memory;

/// <summary>Something that holds data it can read again later, and can drop it while the app is idle.</summary>
public interface IIdleReleasable
{
    /// <summary>Drops what it holds when nothing has used it for <paramref name="idleFor"/>. True when something was dropped.</summary>
    bool ReleaseIfIdle(TimeSpan idleFor);
}

/// <summary>
/// The caches that can be dropped while the app sits in the background (the game is being played).
/// Held weakly: registering never keeps anything alive.
/// </summary>
public static class IdleMemory
{
    private static readonly List<WeakReference<IIdleReleasable>> Registered = [];

    public static void Register(IIdleReleasable releasable)
    {
        lock (Registered)
        {
            Registered.Add(new WeakReference<IIdleReleasable>(releasable));
        }
    }

    /// <summary>Asks every live registered cache to drop what it holds if idle; how many dropped something.</summary>
    public static int ReleaseIdle(TimeSpan idleFor)
    {
        List<IIdleReleasable> live;
        lock (Registered)
        {
            Registered.RemoveAll(w => !w.TryGetTarget(out _));
            live = [.. Registered.Select(w => w.TryGetTarget(out var r) ? r : null).OfType<IIdleReleasable>()];
        }

        var released = 0;
        foreach (var releasable in live)
        {
            try
            {
                if (releasable.ReleaseIfIdle(idleFor))
                {
                    released++;
                }
            }
            catch (Exception)
            {
                // Dropping a cache is never worth an error: it stays as it is.
            }
        }

        return released;
    }
}
