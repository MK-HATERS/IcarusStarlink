namespace IcarusStarlink.App.Utilities;

/// <summary>
/// Runs <paramref name="work"/> on a background thread pool thread, but first installs
/// <paramref name="capturedContext"/> (the UI thread's own SynchronizationContext, captured by the
/// caller BEFORE spawning this background work — see App.xaml.cs's own OnStartup) as that background
/// thread's own SynchronizationContext.Current. Without this, any `await` inside <paramref
/// name="work"/> (or anything it transitively invokes) resumes its continuation on an arbitrary
/// ThreadPool thread instead of marshaling back to the UI thread once the awaited operation
/// completes. This app's own real startup path hits this exactly: MainViewModel.SelectDefaultPage()
/// eagerly resolves LibraryViewModel, which eagerly resolves DownloadsViewModel, whose constructor
/// fires a real fire-and-forget catalog fetch (RefreshCatalogAsync) — its post-await continuation
/// mutates AvailableAuthors/AvailableCategories and calls ApplyCatalogFilters(), genuine
/// cross-thread ObservableCollection writes once MainWindow is already showing and bound to them.
/// SavesViewModel/SettingsViewModel/NexusCatalogViewModel fire the same constructor-triggered,
/// fire-and-forget shape and are covered by this same fix, at its one real root cause, rather than
/// needing every individual ViewModel to know about its own construction thread.
/// </summary>
public static class StartupThreading
{
    public static Task RunWithCapturedContextAsync(SynchronizationContext? capturedContext, Action work) =>
        Task.Run(() =>
        {
            if (capturedContext is not null)
            {
                SynchronizationContext.SetSynchronizationContext(capturedContext);
            }

            work();
        });
}
