using System.IO;
using System.Windows;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Storage.Nexus;
using Microsoft.Extensions.DependencyInjection;

namespace IcarusStarlink.App;

/// <summary>Nexus links and the single instance: a second launch hands its nxm:// link (or "come to the front") to this one.</summary>
public partial class App
{
    private void BringMainWindowToFront()
    {
        if (MainWindow is null)
        {
            return;
        }

        if (MainWindow.WindowState == WindowState.Minimized)
        {
            MainWindow.WindowState = WindowState.Normal;
        }

        MainWindow.Activate();
    }

    /// <summary>
    /// Windows sends every game's nxm:// links to one handler. When that's Starlink, a link for
    /// another game is handed to the mod manager given that game in Settings › Nexus, else to the
    /// one Starlink replaced last (NxmProtocolRegistrar remembers them), so the user's other
    /// managers keep working. False when the link is for Icarus or there's
    /// no other manager to start — Starlink then deals with it (DownloadsViewModel refuses other games).
    /// </summary>
    private static bool TryForwardOtherGameLink(string nxmUrl)
    {
        if (NxmHandlerCommand.IsForIcarus(nxmUrl) || NxmHandlerCommand.GameDomainOf(nxmUrl) is not { } game)
        {
            return false;
        }

        try
        {
            INxmProtocolRegistrar registrar = new NxmProtocolRegistrar();
            if (NxmHandlerCommand.LaunchFor(registrar.HandlerFor(game), nxmUrl) is not { } launch || !File.Exists(launch.FileName))
            {
                return false;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launch.FileName, launch.Arguments) { UseShellExecute = false })?.Dispose();
            return true;
        }
        catch (Exception)
        {
            // Couldn't start it: Starlink opens and says the link isn't for Icarus.
            return false;
        }
    }

    /// <summary>
    /// Links that arrived before the window was ready. Touched only on the UI thread (the pipe
    /// handler and the startup block both run there), so no lock. A link that came in while the app
    /// was starting used to be dropped (the second launch had already exited, so it was simply
    /// gone), and setting the page from the pipe could race SelectDefaultPage on the startup thread.
    /// </summary>
    private readonly Queue<string> _nxmUrlsDuringStartup = new();

    private bool _isReadyForNxmUrls;

    /// <summary>Called at the end of startup, on the UI thread: handles <paramref name="ownNxmArg"/> and then every queued link, one dispatcher cycle later so the window paints first.</summary>
    private void FinishStartupNxmQueue(string? ownNxmArg)
    {
        var pending = new List<string>();
        if (ownNxmArg is not null)
        {
            pending.Add(ownNxmArg);
        }

        pending.AddRange(_nxmUrlsDuringStartup);
        _nxmUrlsDuringStartup.Clear();
        _isReadyForNxmUrls = true;

        foreach (var url in pending)
        {
            WhileRunning(System.Windows.Threading.DispatcherPriority.Normal, () => HandleNxmUrl(url));
        }
    }

    /// <summary>
    /// Links that arrived while a Starlink window was open on top (the manual install, a question).
    /// Handled once it closes, one at a time and in order: handling one at once put its download
    /// and its own questions over the open window. UI thread only, like the startup queue.
    /// </summary>
    private readonly Queue<string> _nxmUrlsWhileModal = new();

    private bool _waitingForModalToClose;

    private void HandleNxmUrl(string nxmUrl)
    {
        if (!_isReadyForNxmUrls || _host is null)
        {
            _nxmUrlsDuringStartup.Enqueue(nxmUrl);
            return;
        }

        // A link that comes while others still wait goes behind them.
        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal || _nxmUrlsWhileModal.Count > 0)
        {
            _nxmUrlsWhileModal.Enqueue(nxmUrl);
            HandleQueuedNxmUrlsLater();
            return;
        }

        HandleNxmUrlNow(nxmUrl);
    }

    /// <summary>Takes the next waiting link once no window is open on top: now (next dispatcher cycle) if none is, else when it closes.</summary>
    private void HandleQueuedNxmUrlsLater()
    {
        if (!System.Windows.Interop.ComponentDispatcher.IsThreadModal)
        {
            WhileRunning(System.Windows.Threading.DispatcherPriority.Normal, HandleNextQueuedNxmUrl);
            return;
        }

        if (_waitingForModalToClose)
        {
            return;
        }

        _waitingForModalToClose = true;
        System.Windows.Interop.ComponentDispatcher.LeaveThreadModal += OnLeaveThreadModal;
    }

    private void OnLeaveThreadModal(object? sender, EventArgs e)
    {
        System.Windows.Interop.ComponentDispatcher.LeaveThreadModal -= OnLeaveThreadModal;
        _waitingForModalToClose = false;
        WhileRunning(System.Windows.Threading.DispatcherPriority.Normal, HandleNextQueuedNxmUrl);
    }

    /// <summary>One waiting link; the next waits for its turn (the one just handled may open a window of its own).</summary>
    private void HandleNextQueuedNxmUrl()
    {
        if (_nxmUrlsWhileModal.Count == 0)
        {
            return;
        }

        if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
        {
            HandleQueuedNxmUrlsLater();
            return;
        }

        HandleNxmUrlNow(_nxmUrlsWhileModal.Dequeue());
        if (_nxmUrlsWhileModal.Count > 0)
        {
            HandleQueuedNxmUrlsLater();
        }
    }

    private void HandleNxmUrlNow(string nxmUrl)
    {
        if (_host is null)
        {
            return;
        }

        if (MainWindow is not null)
        {
            if (MainWindow.WindowState == WindowState.Minimized)
            {
                MainWindow.WindowState = WindowState.Normal;
            }

            MainWindow.Activate();
        }

        // "--import <build>" (OwnBuildImport): a mod author's own build, installed like an update,
        // shown on the Library page where it lands.
        if (OwnBuildImport.PathOf(nxmUrl) is { } buildPath)
        {
            var main = _host.Services.GetRequiredService<MainViewModel>();
            main.SelectedNavItem = main.NavItems.First(item => item.Id == "library");
            var library = _host.Services.GetRequiredService<LibraryViewModel>();
            _ = ImportOwnBuildAsync(library, buildPath);
            return;
        }

        // A download from Starlink's own Nexus page is an update, not a mod: the update window
        // waiting for it takes it, else it's offered as one (it used to be imported into the Library).
        if (Services.NexusAppUpdates.IsStarlinkLink(nxmUrl))
        {
            if (AppUpdateViewModel.Current?.TryDeliverNexusLink(nxmUrl) != true)
            {
                _ = _host.Services.GetRequiredService<SettingsViewModel>().OfferUpdateFromNexusLinkAsync(nxmUrl);
            }

            return;
        }

        // Pending downloads (and what became of each one) live on the Downloads page — land there
        // so the user sees this download arrive.
        var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
        mainViewModel.SelectedNavItem = mainViewModel.NavItems.First(item => item.Id == "downloads");

        _ = _host.Services.GetRequiredService<DownloadsViewModel>().FetchAndDownloadAsync(nxmUrl);
    }

    private static async Task ImportOwnBuildAsync(LibraryViewModel library, string path) =>
        library.StatusMessage = await library.Downloads.ImportOwnBuildAsync(path);
}
