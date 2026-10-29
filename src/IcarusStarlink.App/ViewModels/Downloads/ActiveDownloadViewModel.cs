using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.Core.IO;

namespace IcarusStarlink.App.ViewModels;

/// <summary>
/// One row of the Downloads page's "Downloading" list: a download in progress (bytes so far, the
/// size when the server said it, the speed), then "Installing…" while it is unpacked into the
/// Library. Its Cancel stops this download only — linked to the token Cancel downloads cancels, so
/// that one still stops them all. Gone from the list once the download has landed, failed or been
/// cancelled.
/// </summary>
public sealed partial class ActiveDownloadViewModel : ObservableObject, IDisposable
{
    private const double BytesPerMegabyte = 1024d * 1024d;

    /// <summary>The speed is worked out over at least this long, so it doesn't flicker with every report.</summary>
    private static readonly TimeSpan SpeedWindow = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _cancellation;
    private readonly SynchronizationContext? _uiContext;
    private long _speedSampleTimestamp = Stopwatch.GetTimestamp();
    private long _speedSampleBytes;

    public ActiveDownloadViewModel(string name, CancellationToken cancelAll)
    {
        _name = name;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancelAll);
        // Made on the UI thread in the app; progress from the download is posted back to it.
        _uiContext = SynchronizationContext.Current;
        Progress = new UiProgress(this);
    }

    /// <summary>What the download and the request before it are cancelled with.</summary>
    public CancellationToken Token => _cancellation.Token;

    /// <summary>Still counted as a running download by the page (the bytes aren't all in yet).</summary>
    internal bool CountedAsDownloading { get; set; }

    /// <summary>Hand this to HttpFileDownloader: each report lands on the UI thread.</summary>
    public IProgress<DownloadProgress> Progress { get; }

    /// <summary>The mod's name, or "Mod #N" until a Nexus download's file name is known.</summary>
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(Percent), nameof(IsIndeterminate))]
    private long _bytesReceived;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(Percent), nameof(IsIndeterminate))]
    private long? _totalBytes;

    /// <summary>Bytes per second over the last second or so; null until there's enough to tell.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    private double? _bytesPerSecond;

    /// <summary>The file is in; it's being unpacked into the Library now (can't be cancelled: that could leave a mod half-copied).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(IsIndeterminate), nameof(CanCancel), nameof(SpeedText))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isInstalling;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText), nameof(CanCancel))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isCancelling;

    /// <summary>0–100 when the size is known.</summary>
    public double Percent => TotalBytes is > 0 and var total ? Math.Clamp(BytesReceived * 100d / total, 0, 100) : 0;

    /// <summary>A moving bar instead of a filling one: size unknown, or installing.</summary>
    public bool IsIndeterminate => IsInstalling || TotalBytes is not > 0;

    /// <summary>"12.3 of 45.6 MB", "12.3 MB" when the size isn't known, "Starting…" before the first byte, "Installing…" after the last.</summary>
    public string SizeText =>
        IsInstalling ? "Installing…"
        : IsCancelling ? "Cancelling…"
        : BytesReceived == 0 && TotalBytes is null ? "Starting…"
        : TotalBytes is > 0 and var total ? $"{Megabytes(BytesReceived)} of {Megabytes(total)} MB"
        : $"{Megabytes(BytesReceived)} MB";

    /// <summary>"1.2 MB/s", or empty when not downloading or not known yet.</summary>
    public string SpeedText => IsInstalling || BytesPerSecond is not { } speed ? "" : $"{Megabytes((long)speed)} MB/s";

    public bool CanCancel => !IsInstalling && !IsCancelling;

    private static string Megabytes(long bytes) => (bytes / BytesPerMegabyte).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Stops this download only; the others keep going.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        IsCancelling = true;
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished and gone from the list.
        }
    }

    /// <summary>The download is done; what's left is the install.</summary>
    public void MarkInstalling() => IsInstalling = true;

    /// <summary>One progress report, on the UI thread.</summary>
    public void Apply(DownloadProgress progress)
    {
        TotalBytes = progress.TotalBytes;
        BytesReceived = progress.BytesReceived;

        var elapsed = Stopwatch.GetElapsedTime(_speedSampleTimestamp);
        if (elapsed >= SpeedWindow)
        {
            BytesPerSecond = Math.Max(0, progress.BytesReceived - _speedSampleBytes) / elapsed.TotalSeconds;
            _speedSampleTimestamp = Stopwatch.GetTimestamp();
            _speedSampleBytes = progress.BytesReceived;
        }
    }

    public void Dispose() => _cancellation.Dispose();

    /// <summary>
    /// Posts each report to the UI thread the row was made on (inline when already there, or when
    /// there is none — tests). Progress&lt;T&gt; would queue to the thread pool with no UI context.
    /// </summary>
    private sealed class UiProgress(ActiveDownloadViewModel row) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value)
        {
            var context = row._uiContext;
            if (context is null || ReferenceEquals(context, SynchronizationContext.Current))
            {
                row.Apply(value);
                return;
            }

            context.Post(_ => row.Apply(value), null);
        }
    }
}
