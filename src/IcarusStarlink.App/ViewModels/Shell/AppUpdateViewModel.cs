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
/// The update window, showing the download and install like a real app does: gets the download link (from Nexus for a stable release there,
/// else GitHub), downloads with progress, checks the file, unpacks it and hands over to the
/// updater, then closes Starlink. Cancel stops it at any point before the hand-over; nothing is
/// changed until then.
/// </summary>
public sealed partial class AppUpdateViewModel : ObservableObject
{
    private readonly AppUpdateRelease _release;

    /// <summary>The zip's checksum: the release's, or fetched again from GitHub for a Nexus download.</summary>
    private string? _digest;
    private readonly IAppUpdateClient _appUpdateClient;
    private readonly INexusApiClient _nexusApiClient;
    private readonly string? _nexusApiKey;
    private readonly Func<string?> _busyNow;
    private readonly bool _skipOldExe;
    private readonly Action<string> _openUrl;
    private readonly INxmProtocolRegistrar? _nxmRegistrar;
    private readonly IDialogService? _dialogService;
    private readonly CancellationTokenSource _cancel = new();
    private TaskCompletionSource<NxmUrl?>? _waitingForNexusLink;
    private DateTime _downloadStartedUtc;

    /// <summary>The Nexus file the window waits for, and whether its page was opened yet.</summary>
    private int _waitingForFileId;
    private bool _nexusPageOpened;

    /// <summary>Getting or downloading the Nexus file is under way (its failures count for <see cref="_nexusFailures"/>).</summary>
    private bool _gettingNexusDownload;

    /// <summary>How many runs in this window failed while getting or downloading the Nexus file.</summary>
    private int _nexusFailures;

    /// <summary>The user chose GitHub after Nexus failed twice: every later run downloads from there.</summary>
    private bool _useGitHub;

    /// <summary>The window waiting for a Nexus link (App.HandleNxmUrl hands Starlink's own links here).</summary>
    public static AppUpdateViewModel? Current { get; private set; }

    /// <param name="nxmRegistrar">
    /// Tells whether a Nexus download (nxm:// link) can come back to Starlink, and makes Starlink the
    /// handler when the user asks. Null: assume it can.
    /// </param>
    /// <param name="dialogService">Asks before Starlink becomes the download handler (a registry change). Null: no question (tests).</param>
    public AppUpdateViewModel(AppUpdateRelease release, IAppUpdateClient appUpdateClient, INexusApiClient nexusApiClient, string? nexusApiKey,
        Func<string?> busyNow, bool skipOldExe, Action<string> openUrl, NxmUrl? nexusLink = null, INxmProtocolRegistrar? nxmRegistrar = null,
        IDialogService? dialogService = null)
    {
        _dialogService = dialogService;
        _release = release;
        _digest = release.AssetDigest;
        _appUpdateClient = appUpdateClient;
        _nexusApiClient = nexusApiClient;
        _nexusApiKey = nexusApiKey;
        _busyNow = busyNow;
        _skipOldExe = skipOldExe;
        _openUrl = openUrl;
        _nxmRegistrar = nxmRegistrar;
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

    private bool FromNexus => !_useGitHub && (_release.NexusFileId is not null || _givenNexusLink is not null) && _nexusApiKey is not null;

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
    [NotifyPropertyChangedFor(nameof(CanOpenNexusPageAgain))]
    private bool _isWaitingForNexus;

    /// <summary>
    /// While waiting for the Nexus download, or once getting or downloading the Nexus file has
    /// failed twice in this window (never switched to by itself).
    /// </summary>
    public bool CanUseGitHubInstead => _release.IsOnGitHub && (IsWaitingForNexus || (IsStopped && OffersGitHubAfterNexusFailed));

    /// <summary>Nexus failed again after Try again: GitHub is offered next to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseGitHubInstead))]
    private bool _offersGitHubAfterNexusFailed;

    /// <summary>Stopped by a failure (not the user's Cancel): the whole update can run again.</summary>
    [ObservableProperty]
    private bool _canRetry;

    /// <summary>
    /// Waiting for a Nexus download that can't come back here: nxm:// links go to another app, or
    /// to nothing. The window offers to make Starlink the handler and opens Nexus only after that.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOpenNexusPageAgain))]
    private bool _canMakeStarlinkTheDownloadHandler;

    public bool CanOpenNexusPageAgain => IsWaitingForNexus && !CanMakeStarlinkTheDownloadHandler;

    /// <summary>Running and still cancellable (before the hand-over).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseLabel))]
    private bool _canCancel = true;

    /// <summary>Stopped (failed or cancelled): the window can be closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CloseLabel))]
    [NotifyPropertyChangedFor(nameof(CanUseGitHubInstead))]
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

        // The updater runs from the work folder and copies from it: from then on it must stay.
        var updaterStarted = false;
        StartOver();
        try
        {
            // A zip from Nexus is only installed once it matches GitHub's checksum for the same
            // version: GitHub down or rate-limited, or a version put on
            // Nexus before its GitHub release, stops here rather than installing unchecked.
            if (FromNexus && _digest is null)
            {
                Step(AppUpdateStep.GetLink).State = "active";
                ProgressText = "Getting the checksum from GitHub…";
                _digest = (await _appUpdateClient.GetReleaseAsync(_release.Version, token))?.AssetDigest;
                if (_digest is null)
                {
                    Fail($"Couldn't confirm v{_release.Version} against GitHub's checksum, so nothing was downloaded. GitHub may be busy: try again in a few minutes.");
                    return false;
                }
            }

            _gettingNexusDownload = FromNexus;
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
            _gettingNexusDownload = false;

            Step(AppUpdateStep.Check).State = "active";
            ProgressText = "Checking the file…";
            IsProgressIndeterminate = true;
            await GitHubAssetIntegrity.VerifySha256Async(_digest, zipPath, token);
            Done(AppUpdateStep.Check, _digest is null
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
            }, _busyNow, _skipOldExe, token);
            updaterStarted = problem is null;
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
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Fail("Cancelled — nothing was changed.", canRetry: false);
            return false;
        }
        catch (OperationCanceledException)
        {
            // HttpClient's own timeout (a TaskCanceledException nobody asked for), not the user's Cancel.
            Fail("Nexus or GitHub didn't answer in time. Nothing was changed: try again in a few minutes.");
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
            _gettingNexusDownload = false;

            // Stopped before the hand-over (failed or cancelled): its zip and unpacked build used
            // to stay in %TEMP% until the next update. Last, so a Try again pressed meanwhile
            // (it gets a folder of its own) isn't undone by the lines above.
            if (!updaterStarted && workFolder.Length > 0)
            {
                await Task.Run(() => AppUpdateInstaller.TryDeleteWorkFolder(workFolder));
            }
        }
    }

    /// <summary>Clears what an earlier run in this window left (Try again, Get it from GitHub instead).</summary>
    private void StartOver()
    {
        foreach (var row in Steps)
        {
            row.State = "waiting";
            row.Detail = null;
        }

        ErrorText = null;
        ProgressText = null;
        ProgressPercent = 0;
        IsProgressIndeterminate = true;
        CanRetry = false;
        OffersGitHubAfterNexusFailed = false;
        IsStopped = false;
        CanCancel = true;
    }

    /// <summary>Where to download from: Nexus (straight for Premium, else through the page's download button) or GitHub. Null when it stopped.</summary>
    private async Task<IReadOnlyList<string>?> GetDownloadUrlsAsync(CancellationToken token)
    {
        var step = Step(AppUpdateStep.GetLink);
        step.State = "active";
        if (!FromNexus)
        {
            Done(AppUpdateStep.GetLink, _useGitHub ? "From GitHub instead" : "github.com/MK-HATERS/IcarusStarlink");
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
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // Can't tell (including Nexus not answering in time): the page's own download button
            // works for every account.
        }

        if (premium)
        {
            var urls = await NexusAppUpdates.GetDownloadUrlsAsync(_nexusApiClient, _nexusApiKey!, fileId, null, null, token);
            Done(AppUpdateStep.GetLink, "Nexus Premium: straight from Nexus");
            return urls;
        }

        // Nexus gives other accounts a link only through its own download button, and that link
        // reaches this window only when Starlink handles nxm:// links.
        _waitingForFileId = fileId;
        _nexusPageOpened = false;
        CanMakeStarlinkTheDownloadHandler = !NexusDownloadsComeToStarlink();
        step.Detail = CanMakeStarlinkTheDownloadHandler
            ? "Nexus downloads on this PC don't open Starlink, so this one wouldn't reach it. Press Make Starlink the download handler, "
                + "then press Slow download (or Mod Manager Download) on the Nexus page that opens."
            : NexusOpenedText;
        ProgressText = "Waiting for the download from Nexus…";
        IsWaitingForNexus = true;
        Current = this;
        NxmUrl? link;
        while (true)
        {
            var waiting = _waitingForNexusLink = new TaskCompletionSource<NxmUrl?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_nexusPageOpened && !CanMakeStarlinkTheDownloadHandler)
            {
                _nexusPageOpened = true;
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
        CanMakeStarlinkTheDownloadHandler = false;
        Current = null;
        if (link is null)
        {
            // "Get it from GitHub instead".
            _gettingNexusDownload = false;
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

    /// <summary>While waiting for Nexus: take the GitHub download. Once Nexus failed twice: run the update again from GitHub.</summary>
    [RelayCommand]
    private async Task UseGitHubInsteadAsync()
    {
        if (IsWaitingForNexus)
        {
            _waitingForNexusLink?.TrySetResult(null);
            return;
        }

        if (CanUseGitHubInstead)
        {
            _useGitHub = true;
            await RunAsync();
        }
    }

    /// <summary>Runs the whole update again after a failure.</summary>
    [RelayCommand]
    private async Task TryAgainAsync()
    {
        if (CanRetry)
        {
            await RunAsync();
        }
    }

    [RelayCommand]
    private void OpenNexusPageAgain()
    {
        if (_release.NexusFileId is { } fileId)
        {
            _openUrl(NexusAppUpdates.DownloadPageUrl(fileId));
        }
    }

    private const string NexusOpenedText =
        "Nexus opened in your browser: press Slow download (or Mod Manager Download) there, and Starlink takes it from there.";

    /// <summary>
    /// Whether an nxm:// link from Nexus reaches a Starlink: this one, or another Starlink install,
    /// which passes Starlink's own links on to the running one. Assumed when it can't be told.
    /// </summary>
    private bool NexusDownloadsComeToStarlink()
    {
        if (_nxmRegistrar is null)
        {
            return true;
        }

        try
        {
            return _nxmRegistrar.IsRegisteredToThisApp() || NxmHandlerCommand.IsStarlink(_nxmRegistrar.CurrentHandlerCommand());
        }
        catch (Exception)
        {
            // A registry read refused: don't hold the update back on a guess.
            return true;
        }
    }

    /// <summary>The user's click: Starlink takes nxm:// links (links for other games still go to the app it replaces), then Nexus opens.</summary>
    [RelayCommand]
    private void MakeStarlinkTheDownloadHandler()
    {
        if (_nxmRegistrar is null || !CanMakeStarlinkTheDownloadHandler)
        {
            return;
        }

        var asked = _dialogService?.Confirm(
            "Nexus's Slow download and Mod Manager Download buttons will open Icarus Starlink for your Windows account.\n\n"
            + "If another mod manager (for example Vortex) handles them now, Starlink passes it the links for its own games, so it keeps working. "
            + "Unregister in Settings › Nexus gives the links back to it.\n\nContinue?",
            "Make Starlink the download handler", Views.ThemedConfirmSeverity.Question);
        if (asked == false)
        {
            return;
        }

        var step = Step(AppUpdateStep.GetLink);
        try
        {
            _nxmRegistrar.Register();
        }
        catch (Exception ex)
        {
            step.Detail = $"Couldn't make Starlink the download handler: {ex.Message} Settings › Nexus has the same button.";
            return;
        }

        if (!NexusDownloadsComeToStarlink())
        {
            step.Detail = "Windows didn't take the change, so Nexus downloads still don't open Starlink. Settings › Nexus has the same button.";
            return;
        }

        CanMakeStarlinkTheDownloadHandler = false;
        step.Detail = NexusOpenedText;
        if (IsWaitingForNexus && !_nexusPageOpened)
        {
            _nexusPageOpened = true;
            _openUrl(NexusAppUpdates.DownloadPageUrl(_waitingForFileId));
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
            Cancel();
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

        Cancel();
        return false;
    }

    /// <summary>Unpacking can take a few seconds to stop: say it was heard.</summary>
    private void Cancel()
    {
        ProgressText = "Cancelling…";
        _cancel.Cancel();
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

    /// <param name="canRetry">False for the user's own Cancel: Try again is offered after any failure.</param>
    private void Fail(string message, bool canRetry = true)
    {
        if (Steps.FirstOrDefault(s => s.State == "active") is { } active)
        {
            active.State = "failed";
        }

        // Getting or using the Nexus link failed (a quarantined file, a server error): Try again
        // first; failing again offers the GitHub download too, never switching by itself.
        if (canRetry && _gettingNexusDownload)
        {
            _nexusFailures++;
        }

        CanRetry = canRetry;
        OffersGitHubAfterNexusFailed = canRetry && _nexusFailures >= 2 && !_useGitHub;
        IsWaitingForNexus = false;
        CanMakeStarlinkTheDownloadHandler = false;
        IsProgressIndeterminate = false;
        ProgressPercent = 0;
        ProgressText = null;
        ErrorText = message;
        CanCancel = false;
        IsStopped = true;
    }
}
