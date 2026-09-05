using IcarusStarlink.App.Utilities;

namespace IcarusStarlink.App.Tests.Utilities;

/// <summary>
/// App.xaml.cs's own OnStartup has zero test coverage in this codebase (WPF's Application/Dispatcher
/// lifecycle can't run in a headless xunit host) — this is the extracted, WPF-free piece of that
/// method's own real fix (see StartupThreading's own doc comment): without a SynchronizationContext
/// installed on the background startup thread, DownloadsViewModel's (and Saves/Settings/
/// NexusCatalogViewModel's) own constructor-fired fire-and-forget async work would resume its
/// post-await continuation on an arbitrary ThreadPool thread instead of marshaling back to the
/// captured one — a real cross-thread UI mutation once MainWindow is showing.
/// </summary>
public class StartupThreadingTests
{
    [Fact]
    public async Task RunWithCapturedContextAsync_PostAwaitContinuation_RunsThroughTheCapturedContext()
    {
        var recordingContext = new RecordingSynchronizationContext();
        var tcs = new TaskCompletionSource();

        await StartupThreading.RunWithCapturedContextAsync(recordingContext, () =>
        {
            _ = RunAsyncWorkAsync();

            async Task RunAsyncWorkAsync()
            {
                // The install itself happens synchronously, before this first await, inside the
                // delegate StartupThreading.RunWithCapturedContextAsync invokes — this await is what
                // actually exercises whether that installation took effect: with it, the runtime
                // posts this continuation through recordingContext; without it (SynchronizationContext.Current
                // left null on the background thread), it would just resume on an arbitrary
                // ThreadPool thread and recordingContext.Post would never be called at all.
                await Task.Yield();
                tcs.SetResult();
            }
        });

        await tcs.Task;
        Assert.True(recordingContext.PostCount > 0);
    }

    [Fact]
    public async Task RunWithCapturedContextAsync_NullCapturedContext_StillRunsTheWorkWithoutThrowing()
    {
        var ran = false;

        await StartupThreading.RunWithCapturedContextAsync(null, () => ran = true);

        Assert.True(ran);
    }

    /// <summary>Records every Post call rather than actually executing callbacks inline — a real DispatcherSynchronizationContext queues onto the UI thread's message loop, which this test has none of; only whether the runtime routed the continuation through this context at all matters here.</summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => _postCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            d(state);
        }
    }
}
