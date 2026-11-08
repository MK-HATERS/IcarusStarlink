namespace IcarusStarlink.PakIO.Tests;

/// <summary>
/// Stands in for the UI thread's context: work that should leave the caller's thread must not run
/// under it. The base Post queues to the thread pool, so awaits that captured it still complete.
/// </summary>
internal sealed class MarkerSynchronizationContext : SynchronizationContext
{
    /// <summary>Starts <paramref name="start"/> with this context current, then puts the previous one back.</summary>
    public Task<T> Run<T>(Func<Task<T>> start)
    {
        var previous = Current;
        SetSynchronizationContext(this);
        try
        {
            return start();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}
