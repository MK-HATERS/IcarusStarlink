using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IcarusStarlink.App.Services;
using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Core.Nexus;

namespace IcarusStarlink.App.ViewModels;

/// <summary>One line of the update window: a step, and where it's got to.</summary>
public sealed partial class AppUpdateStepRow(AppUpdateStep step, string title) : ObservableObject
{
    public AppUpdateStep Step { get; } = step;

    public string Title { get; } = title;

    /// <summary>"waiting", "active", "done", "failed" (the window draws a mark for each).</summary>
    [ObservableProperty]
    private string _state = "waiting";

    [ObservableProperty]
    private string? _detail;
}

/// <summary>
/// The update window (user request 2026-10-24: "a window that shows status of download and
/// installing like a real app"): gets the download link (from Nexus for a stable release there,
/// else GitHub), downloads with progress, checks the file, unpacks it and hands over to the
/// updater, then closes Starlink. Cancel stops it at any point before the hand-over; nothing is
/// changed until then.
/// </summary>
public sealed partial class AppUpdateViewModel : ObservableObject
{
    private readonly AppUpdateRelease _release;
    private readonly IAppUpdateClient _appUpdateClient;
    private readonly INexusApiClient _nexusApiClient;
    private readonly string? _nexusApiKey;
    private readonly Func<string?> _busyNow;
    private readonly bool _skipOldExe;
    private readonly Action<string> _openUrl;
    private readonly CancellationTokenSource _cancel = new();
    private TaskCompletionSource<NxmUrl?>? _waitingForNexusLink;
    private DateTime _downloadStartedUtc;

    /// <summary>The window waiting for a Nexus link (App.HandleNxmUrl hands Starlink's own links here).</summary>
    public static AppUpdateViewModel? Current { get; private set; }

    public AppUpdateViewModel(AppUpdateRelease release, IAppUpdateClient appUpdateClient, INexusApiClient nexusApiClient, string? nexusApiKey,
        Func<string?> busyNow, bool skipOldExe, Action<string> openUrl, NxmUrl? nexusLink = null)
    {
        _release = release;
        _appUpdateClient = appUpdateClient;
        _nexusApiClient = nexusApiClient;
        _nexusApiKey = nexusApiKey;
        _busyNow = busyNow;
        _skipOldExe = skipOldExe;
        _openUrl = openUrl;
        _givenNexusLink = nexusLink;
        Headline = $"Updating Icarus Starlink to v{release.Version}";
        Steps =
        [
            new(AppUpdateStep.GetLink, FromNexus ? "Get the download from Nexus" : "Get the download from GitHub"),
            new(AppUpdateStep.Download, "Download"),
            new(AppUpdateStep.Check, "Check the file"),
            new(AppUpdateStep.Unpack, "Unpack"),
            new(AppUpdateStep.Handover, "Close Starlink, install and restart"),
        ];
    }

    private readonly NxmUrl? _givenNexusLink;

    private bool FromNexus => (_release.NexusFileId is not null || _givenNexusLink is not null) && _nexusApiKey is not null;

    public ObservableCollection<AppUpdateStepRow> Steps { get; }

    [ObservableProperty]
    private string _headline;

    /// <summary>"23.4 of 93.7 MB · 4.2 MB/s"; the step's own text otherwise.</summary>
    [ObservableProperty]
    private string? _progressText;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private bool _isProgressIndeterminate = true;

    /// <summary>Why it stopped, in words; null while it runs.</summary>
    [ObservableProperty]
    private string? _errorText;

    /// <summary>Waiting for the user to press the download button on Nexus.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseGitHubInstead))]
    private bool _isWaitingForNexus;

    public bool CanUseGitHubInstead => IsWaitingForNexus && _release.IsOnGitHub;

    /// <summary>Running and still cancellable (before the hand-over).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseLabel))]
    private bool _canCancel = true;

    /// <summary>Stopped (failed or cancelled): the window can be closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseLabel))]
    private bool _isStopped;

    public string CloseLabel => IsStopped ? "Close" : "Cancel";

    /// <summary>The updater is running: Starlink must close now (the window's owner does it).</summary>
    public bool HandedOver { get; private set; }

    /// <summary>Raised when the window should close (stopped and dismissed, or handed over).</summary>
    public event Action<bool>? Finished;

    /// <summary>Runs the whole update. True once the updater is running and Starlink should close.</summary>
    public async Task<bool> RunAsync()
    {
        var token = _cancel.Token;
        var workFolder = "";
        try
        {
            var urls = await GetDownloadUrlsAsync(token);
            if (urls is null)
            {
                return false;
            }

            Step(AppUpdateStep.Download).State = "active";
            workFolder = await Task.Run(AppUpdateInstaller.NewWorkFolder, token);
            var zipPath = Path.Combine(workFolder, "update.zip");
            _downloadStartedUtc = DateTime.UtcNow;
            IsProgressIndeterminate = false;
            await _appUpdateClient.DownloadAsync(urls, null, zipPath, new Progress<AppUpdateDownloadProgress>(ShowDownloadProgress), token);
            Done(AppUpdateStep.Download, $"{Megabytes(new FileInfo(zipPath).Length)} MB");

            Step(AppUpdateStep.Check).State = "active";
            ProgressText = "Checking the file…";
            IsProgressIndeterminate = true;
            await GitHubAssetIntegrity.VerifySha256Async(_release.AssetDigest, zipPath, token);
            Done(AppUpdateStep.Check, _release.AssetDigest is null
                ? "No checksum published for this version: checked it holds Starlink while unpacking"
                : "Matches the published checksum");

            var problem = await AppUpdateInstaller.InstallFromZipAsync(_release, zipPath, workFolder, step =>
            {
                if (step == AppUpdateStep.Handover)
                {
                    Done(AppUpdateStep.Unpack, null);
                }

                Step(step).State = "active";
                ProgressText = step == AppUpdateStep.Unpack ? "Unpacking…" : "Getting the updater ready…";
            }, _busyNow, _skipOldExe);
            if (problem is not null)
            {
                Fail(problem);
                return false;
            }

            // From here the updater waits for Starlink to close: nothing left to cancel.
            CanCancel = false;
            Step(AppUpdateStep.Handover).Detail = "Starlink closes now; the updater installs and opens it again in a few seconds.";
            ProgressText = "Closing Starlink to install…";
            await Task.Delay(900, CancellationToken.None);
            HandedOver = true;
            Finished?.Invoke(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            Fail("Cancelled — nothing was changed.");
            return false;
        }
        catch (Exception ex)
        {
            Fail($"{ex.Message} Nothing was changed.");
            return false;
        }
        finally
        {
            Current = null;
        }
    }

    /// <summary>Where to download from: Nexus (straight for Premium, else through the page's download button) or GitHub. Null when it stopped.</summary>
    private async Task<IReadOnlyList<string>?> GetDownloadUrlsAsync(CancellationToken token)
    {
        var step = Step(AppUpdateStep.GetLink);
        step.State = "active";
        if (!FromNexus)
        {
            Done(AppUpdateStep.GetLink, "github.com/MK-HATERS/IcarusStarlink");
            return [_release.AssetBrowserDownloadUrl];
        }

        var fileId = _givenNexusLink?.FileId ?? _release.NexusFileId!.Value;
        if (_givenNexusLink is { } given)
        {
            Done(AppUpdateStep.GetLink, "From the Nexus download you started");
            return await NexusAppUpdates.GetDownloadUrlsAsync(_nexusApiClient, _nexusApiKey!, given.FileId, given.Key, given.Expires, token);
        }

        ProgressText = "Asking Nexus…";
        var premium = false;
        try
        {
            premium = (await _nexusApiClient.ValidateKeyAsync(_nexusApiKey!, token))?.IsPremium == true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Can't tell: the page's own download button works for every account.
        }

        if (premium)
        {
            var urls = await NexusAppUpdates.GetDownloadUrlsAsync(_nexusApiClient, _nexusApiKey!, fileId, null, null, token);
            Done(AppUpdateStep.GetLink, "Nexus Premium: straight from Nexus");
            return urls;
        }

        // Nexus gives other accounts a link only through its own download button.
        step.Detail = "Nexus opened in your browser: press Slow download (or Mod Manager Download) there, and Starlink takes it from there.";
        ProgressText = "Waiting for the download from Nexus…";
        IsWaitingForNexus = true;
        Current = this;
        NxmUrl? link;
        var opened = false;
        while (true)
        {
            var waiting = _waitingForNexusLink = new TaskCompletionSource<NxmUrl?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!opened)
            {
                opened = true;
                _openUrl(NexusAppUpdates.DownloadPageUrl(fileId));
            }

            using (token.Register(() => waiting.TrySetCanceled()))
            {
                link = await waiting.Task;
            }

            // Only this release's file: another one on the page (an older version) isn't what the
            // window says it installs, and without a GitHub checksum nothing else would catch it.
            if (link is null || link.FileId == fileId)
            {
                break;
            }

            step.Detail = $"That download was a different file on the page, not v{_release.Version}. Press Slow download (or Mod Manager Download) on v{_release.Version} there.";
        }

        IsWaitingForNexus = false;
        Current = null;
        if (link is null)
        {
            // "Get it from GitHub instead".
            Done(AppUpdateStep.GetLink, "From GitHub instead");
            return [_release.AssetBrowserDownloadUrl];
        }

        var fromLink = await NexusAppUpdates.GetDownloadUrlsAsync(_nexusApiClient, _nexusApiKey!, link.FileId, link.Key, link.Expires, token);
        Done(AppUpdateStep.GetLink, "From your Nexus download");
        return fromLink;
    }

    /// <summary>Starlink's own nxm:// link arrived (App.HandleNxmUrl): false when this window isn't waiting for one.</summary>
    public bool TryDeliverNexusLink(string nxmUrl)
    {
        if (_waitingForNexusLink is null || !IsWaitingForNexus)
        {
            return false;
        }

        try
        {
            return _waitingForNexusLink.TrySetResult(NxmUrl.Parse(nxmUrl));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    [RelayCommand]
    private void UseGitHubInstead() => _waitingForNexusLink?.TrySetResult(null);

    [RelayCommand]
    private void OpenNexusPageAgain()
    {
        if (_release.NexusFileId is { } fileId)
        {
            _openUrl(NexusAppUpdates.DownloadPageUrl(fileId));
        }
    }

    /// <summary>Cancel while running; Close once stopped.</summary>
    [RelayCommand]
    private void CancelOrClose()
    {
        if (IsStopped)
        {
            Finished?.Invoke(false);
            return;
        }

        if (CanCancel)
        {
            _cancel.Cancel();
        }
    }

    /// <summary>The window's close box: same as Cancel; refused once the hand-over started.</summary>
    public bool TryClose()
    {
        if (IsStopped)
        {
            return true;
        }

        if (!CanCancel)
        {
            return false;
        }

        _cancel.Cancel();
        return false;
    }

    private void ShowDownloadProgress(AppUpdateDownloadProgress progress)
    {
        var seconds = Math.Max((DateTime.UtcNow - _downloadStartedUtc).TotalSeconds, 0.1);
        var speed = progress.Received / seconds / (1024 * 1024);
        if (progress.Total is { } total and > 0)
        {
            ProgressPercent = progress.Received * 100.0 / total;
            ProgressText = $"{Megabytes(progress.Received)} of {Megabytes(total)} MB · {speed:0.0} MB/s";
        }
        else
        {
            IsProgressIndeterminate = true;
            ProgressText = $"{Megabytes(progress.Received)} MB · {speed:0.0} MB/s";
        }
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("0.0");

    private AppUpdateStepRow Step(AppUpdateStep step) => Steps.First(s => s.Step == step);

    private void Done(AppUpdateStep step, string? detail)
    {
        var row = Step(step);
        row.State = "done";
        row.Detail = detail;
    }

    private void Fail(string message)
    {
        if (Steps.FirstOrDefault(s => s.State == "active") is { } active)
        {
            active.State = "failed";
        }

        IsWaitingForNexus = false;
        IsProgressIndeterminate = false;
        ProgressPercent = 0;
        ProgressText = null;
        ErrorText = message;
        CanCancel = false;
        IsStopped = true;
    }
}
