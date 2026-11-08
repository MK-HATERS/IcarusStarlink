using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace IcarusStarlink.App.ViewModels;

/// <summary>How one Nexus fetch ended (FetchNexusModAsync): a queue goes on after the first four and stops at the rest.</summary>
public enum NexusFetchOutcome
{
    /// <summary>Downloaded through the API (Premium); the Downloads page says how the install went.</summary>
    Fetched,

    /// <summary>A free account's Nexus page opened, without waiting for its download.</summary>
    PageOpened,

    /// <summary>A free account's download came back from the Nexus page and was installed (or failed; the Downloads page says).</summary>
    Arrived,

    /// <summary>Nothing could be fetched for this mod (no files, a bad key, an error); the next one can still be tried.</summary>
    Failed,

    /// <summary>The player cancelled the file list.</summary>
    Cancelled,

    /// <summary>The player pressed Stop waiting.</summary>
    Stopped,

    /// <summary>No download came back from the Nexus page in time.</summary>
    TimedOut,

    /// <summary>Nexus said too many requests: stop asking for now.</summary>
    RateLimited,

    /// <summary>Already waiting for another mod's download from Nexus.</summary>
    Busy,
}

// A free account's download from the Nexus website: open the file's page, then wait until its nxm:// link has come back and installed.
public sealed partial class DownloadsViewModel
{
    /// <summary>How long Starlink waits for a download from the Nexus page before giving up on it.</summary>
    internal TimeSpan BrowserDownloadTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>The mod whose download from the Nexus page Starlink waits for (null when none): shows the Stop waiting button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaitingForBrowserDownload))]
    [NotifyCanExecuteChangedFor(nameof(StopWaitingForBrowserDownloadCommand))]
    private string? _waitingForBrowserDownload;

    public bool IsWaitingForBrowserDownload => WaitingForBrowserDownload is not null;

    private readonly object _browserWaitGate = new();
    private int? _browserWaitModId;
    private TaskCompletionSource? _browserWaitArrived;
    private CancellationTokenSource? _browserWaitStop;

    /// <summary>
    /// Opens <paramref name="page"/> and waits until a download of that mod has come back from it
    /// and installed (any of its files: the player may pick another one there). One wait at a time,
    /// so installs stay one after another and only one page is open.
    /// </summary>
    internal async Task<NexusFetchOutcome> OpenPageAndWaitForDownloadAsync(int nexusModId, string name, string page)
    {
        TaskCompletionSource arrived;
        CancellationTokenSource stop;
        lock (_browserWaitGate)
        {
            if (_browserWaitModId is not null)
            {
                PendingDownloadStatusMessage = $"Already waiting for '{WaitingForBrowserDownload}' from Nexus. Download it there, or press Stop waiting first.";
                return NexusFetchOutcome.Busy;
            }

            _browserWaitModId = nexusModId;
            arrived = _browserWaitArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            stop = _browserWaitStop = new CancellationTokenSource();
        }

        WaitingForBrowserDownload = name;
        try
        {
            // Registered before the page opens, so a quick click can't come back unseen.
            PendingDownloadStatusMessage = OpenUrl(page) is null
                ? $"Waiting for '{name}': click Mod Manager Download on the Nexus page that opened. Starlink installs it, then opens the next one."
                : $"Waiting for '{name}': your browser didn't open. Go to {page} and click Mod Manager Download there.";

            var timeout = Task.Delay(BrowserDownloadTimeout, stop.Token);
            var done = await Task.WhenAny(arrived.Task, timeout);
            if (done == arrived.Task)
            {
                return NexusFetchOutcome.Arrived;
            }

            if (stop.IsCancellationRequested)
            {
                PendingDownloadStatusMessage = $"Stopped waiting for '{name}'.";
                return NexusFetchOutcome.Stopped;
            }

            PendingDownloadStatusMessage = $"No download came from Nexus for '{name}', so Starlink stopped waiting. "
                + "If Mod Manager Download did nothing, turn on Register as Nexus download handler in Settings.";
            return NexusFetchOutcome.TimedOut;
        }
        finally
        {
            lock (_browserWaitGate)
            {
                _browserWaitModId = null;
                _browserWaitArrived = null;
                _browserWaitStop = null;
            }

            stop.Dispose();
            WaitingForBrowserDownload = null;
        }
    }

    /// <summary>A download of this mod has finished (installed, or failed): the wait for it ends.</summary>
    private void BrowserDownloadFinished(int nexusModId)
    {
        TaskCompletionSource? arrived = null;
        lock (_browserWaitGate)
        {
            if (_browserWaitModId == nexusModId)
            {
                arrived = _browserWaitArrived;
            }
        }

        arrived?.TrySetResult();
    }

    [RelayCommand(CanExecute = nameof(IsWaitingForBrowserDownload))]
    private void StopWaitingForBrowserDownload()
    {
        lock (_browserWaitGate)
        {
            _browserWaitStop?.Cancel();
        }
    }
}
