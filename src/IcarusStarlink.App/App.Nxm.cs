using System.IO;
using System.Windows;
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
    /// gone), and setting the page from the pipe could race SelectDefaultPage on the startup thread
    /// (review 2026-10-23).
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
            Dispatcher.BeginInvoke(() => HandleNxmUrl(url));
        }
    }

    private void HandleNxmUrl(string nxmUrl)
    {
        if (!_isReadyForNxmUrls || _host is null)
        {
            _nxmUrlsDuringStartup.Enqueue(nxmUrl);
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

        // Pending downloads (and what became of each one) live on the Downloads page — land there
        // so the user sees this download arrive.
        var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
        mainViewModel.SelectedNavItem = mainViewModel.NavItems.First(item => item.Id == "downloads");

        _ = _host.Services.GetRequiredService<DownloadsViewModel>().FetchAndDownloadAsync(nxmUrl);
    }
}
