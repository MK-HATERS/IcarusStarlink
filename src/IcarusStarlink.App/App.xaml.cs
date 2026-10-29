using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace IcarusStarlink.App;

public partial class App : Application
{
    private BackgroundMemoryTrimmer? _memoryTrimmer;

    private IHost? _host;

    /// <summary>The app's settings, for windows created outside DI (WindowSizeMemory); null in tests.</summary>
    internal static ISettingsService? Settings { get; private set; }
    private SingleInstanceService? _singleInstanceService;
    private string _logsDirectory = string.Empty;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Assigned before the handlers below are wired, not after (a pure string computation, no
        // I/O, so there's no cost to doing this first) — every one of those handlers calls
        // CrashReportWriter.Write(_logsDirectory, ...), and _logsDirectory's own field initializer
        // default is "". CrashReportWriter.Write's own Directory.CreateDirectory("") throws
        // ArgumentException, caught internally and swallowed — so a crash in the real, throwable code
        // below (SingleInstanceService construction/StartListening, both real OS-resource calls)
        // used to silently write NO crash report at all, while OnDispatcherUnhandledException's own
        // MessageBox still claimed "A crash report was written to the Logs folder" regardless.
        _logsDirectory = Path.Combine(AppContext.BaseDirectory, "Logs");

        // Wired up before anything else below has a chance to throw — previously this app had NO
        // global exception handling at all, meaning a real crash depended entirely on WHERE it
        // happened: a sync UI-thread exception crashed with nothing logged, a background-thread
        // startup exception froze the splash screen forever with no trace, and an exception inside
        // almost any async command (Rebuild/Install/FTP/downloads/save-editing are all built this
        // way) was silently discarded by CommunityToolkit.Mvvm's own default behavior. All three
        // now funnel into CrashReportWriter — see its own doc comment.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // A real nxm:// click launches a brand-new process by default — the OS has no idea
        // IcarusStarlink might already be running. If we're the second one, hand our own nxm://
        // argument (if any) to the first instance over a named pipe and exit immediately, before
        // ever touching the DI host/game-data scans/etc. that a normal launch does.
        // A link for another game goes straight to the mod manager Starlink replaced as the nxm://
        // handler, without opening Starlink (or bothering one that's already running).
        var nxmArgument = e.Args.FirstOrDefault(arg => arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
        if (nxmArgument is not null && TryForwardOtherGameLink(nxmArgument))
        {
            Shutdown();
            return;
        }

        var singleInstanceService = new SingleInstanceService();
        if (!singleInstanceService.IsFirstInstance)
        {
            // A plain second launch (no nxm:// argument) asks the running instance to come to the
            // front instead of exiting silently (an audit finding: a minimized first instance plus a
            // Start-menu click looked like "the app won't start").
            SingleInstanceService.SendToRunningInstance(nxmArgument ?? SingleInstanceService.ActivateMessage);

            singleInstanceService.Dispose();
            Shutdown();
            return;
        }

        _singleInstanceService = singleInstanceService;

        // Started immediately, before the (potentially slow — DI host build, HTTP client
        // registration, Library scan, window show) rest of startup below: a second instance only
        // waits a few seconds on SendToRunningInstance before giving up silently, so if the pipe
        // server didn't exist yet until after all of that finished, a real nxm:// click during a
        // cold app start could easily lose the race and vanish with no error anywhere. The DI host
        // builds on a background thread (see the splash-screen Task.Run below), so OnStartup
        // returns and the WPF message loop starts pumping well before the window exists: a link
        // that arrives in that window is queued by HandleNxmUrl and handled once startup is done.
        singleInstanceService.StartListening(message => WhileRunning(System.Windows.Threading.DispatcherPriority.Normal, () =>
        {
            if (message == SingleInstanceService.ActivateMessage)
            {
                BringMainWindowToFront();
            }
            else
            {
                HandleNxmUrl(message);
            }
        }));

        var appDataDirectory = AppContext.BaseDirectory;

        // GitHub answers are reused between runs: its 60-an-hour limit is shared by everything on the connection.
        IcarusStarlink.Catalog.GitHub.GitHubApiCache.Configure(Path.Combine(appDataDirectory, "Cache", "github_cache.json"));
        var logsDirectory = _logsDirectory;
        Directory.CreateDirectory(logsDirectory);

        // Basic or Advanced (Settings › Diagnostics), switchable while running: see LogDetail.
        LogDetail.LogsDirectory = logsDirectory;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LogDetail.AppLevel)
            .MinimumLevel.Override("System.Net.Http", LogDetail.NoiseLevel)
            .MinimumLevel.Override("Microsoft", LogDetail.NoiseLevel)
            .WriteTo.File(
                Path.Combine(logsDirectory, "icarusstarlink-.log"),
                rollingInterval: RollingInterval.Day,
                // Never bloats: a day's file stops at 5 MB and rolls to the next, and only the
                // newest 14 files are kept (about 70 MB at most, far less in Basic).
                fileSizeLimitBytes: LogDetail.MaxFileBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: LogDetail.MaxFiles)
            .CreateLogger();

        // Shown immediately, before the DI host builds — the actual expensive startup work
        // (building the host, then resolving MainViewModel/Library, which triggers
        // FolderLibraryRepository's own synchronous folder scan + search-index rebuild) runs on a
        // background thread below instead, with this splash's own message pump the only thing kept
        // responsive on the UI thread in the meantime. Closes itself once MainWindow is ready to
        // show — see the Dispatcher.Invoke block near the end of this method.
        var splash = new Views.SplashWindow();
        splash.Show();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(dispose: true);

        AppServices.Register(builder.Services, appDataDirectory, logsDirectory);

        // Building the host and resolving MainViewModel both run off the UI thread now — resolving
        // MainViewModel.SelectDefaultPage() is what actually triggers LibraryViewModel, which
        // constructs FolderLibraryRepository, which synchronously scans Extracted_Mods and rebuilds
        // the FTS5 search index (the one real freeze risk a prior version of this comment flagged:
        // fine for the "dozens of mods" library size this is designed around, real for a much
        // larger one). None of this touches a Window/UIElement, so it's safe to build entirely off
        // the UI thread — only the final MainWindow construction below needs to run on it. The
        // splash's own message pump is what stays responsive while this runs.
        // Wrapped in its own try/catch — an exception anywhere in this background startup work
        // (including one that propagates back from the Dispatcher.Invoke block below, since
        // exceptions from a cross-thread Dispatcher.Invoke call return to the CALLING thread, i.e.
        // land right back here) used to have nowhere to go at all: an unobserved faulted Task from
        // a bare Task.Run doesn't terminate the process on modern .NET, so the splash screen just
        // hung forever with no log, no dialog, no crash. Now it's reported the same way any other
        // crash is, and the app shuts down cleanly instead of hanging.
        //
        // Captured here (still on the UI thread — OnStartup itself always runs there) and installed
        // by StartupThreading on the background thread below (see its own doc comment for why):
        // RunStartup's own synchronous work (the Library scan/FTS5 rebuild) doesn't care about
        // SynchronizationContext, but MainViewModel.SelectDefaultPage() eagerly resolves
        // LibraryViewModel, which eagerly resolves DownloadsViewModel — whose constructor fires a
        // real fire-and-forget catalog fetch (RefreshCatalogAsync). Without a SynchronizationContext
        // installed on THIS thread, that fetch's `await`s would resume on an arbitrary ThreadPool
        // thread instead of the UI thread, and its own AvailableAuthors/AvailableCategories/
        // ApplyCatalogFilters() writes — genuine cross-thread ObservableCollection mutations once
        // MainWindow is already showing and bound to them — would throw.
        var uiSynchronizationContext = SynchronizationContext.Current;
        _ = StartupThreading.RunWithCapturedContextAsync(uiSynchronizationContext, () =>
        {
            try
            {
                RunStartup();
            }
            catch (Exception) when (IsClosing)
            {
                // Closed before startup finished (the host is disposed under it): not a failure.
            }
            catch (Exception ex)
            {
                CrashReportWriter.Write(_logsDirectory, ex, "Startup", TryGetActivityLog());
                Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        $"IcarusStarlink couldn't start up:\n\n{ex.Message}\n\nA crash report was written to the Logs folder.",
                        "IcarusStarlink — startup failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    splash.Close();
                    Shutdown(-1);
                });
            }
        });
        return;

        void RunStartup()
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            _host = builder.Build();
            _host.Start();
            Settings = _host.Services.GetRequiredService<ISettingsService>();
            LogDetail.ApplyFromSettings(Settings);

            // Read before any page records this launch: a fresh install isn't asked about the old exe name.
            var existingInstall = Settings.Current.LastSeenAppVersion is not null;

            var logger = _host.Services.GetRequiredService<ILogger<App>>();
            var appVersion = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
            logger.LogInformation(
                "IcarusStarlink v{Version} starting up ({OS}, {Runtime})",
                appVersion, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription);

            splash.UpdateStatus("Loading library…");
            var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
            mainViewModel.SelectDefaultPage();

            // A short floor, not a fixed delay: for the "dozens of mods" library size this is
            // designed around, the work above finishes in well under this (confirmed live —
            // otherwise it'd flash by unseen), so this just holds the last bit of the transition.
            // A genuinely large library that takes longer than this on its own is never held up
            // an extra moment by it.
            var remaining = TimeSpan.FromMilliseconds(600) - stopwatch.Elapsed;
            if (remaining > TimeSpan.Zero)
            {
                Thread.Sleep(remaining);
            }

            Dispatcher.Invoke(() =>
            {
                var themeService = _host.Services.GetRequiredService<IThemeService>();
                var settingsService = _host.Services.GetRequiredService<ISettingsService>();
                themeService.ApplyTheme(settingsService.Current.ThemeName);

                // Settings › Appearance › Size of text and controls: every window from here on.
                UiScale.Initialize(settingsService.Current.UiScale);

                var mainWindow = _host.Services.GetRequiredService<MainWindow>();
                mainWindow.Show();
                splash.Close();

                // WPF only auto-assigns Application.MainWindow to the FIRST window that ever calls
                // Show() — since the splash screen shows before this one, Application.MainWindow
                // would otherwise be left pointing at the (now closed) splash for the app's entire
                // life. A closed window can't be used as another window's Owner (WPF throws
                // "Cannot set Owner property to a Window that has not been shown previously"), which
                // crashed the app the moment any ThemedConfirmDialog/ThemedMessageBox tried to show
                // using Application.Current.MainWindow as its owner.
                Application.Current.MainWindow = mainWindow;

                // Memory goes back to Windows while the app sits in the background (the game is being played).
                _memoryTrimmer = new BackgroundMemoryTrimmer(this);

                // Constructed eagerly (page ViewModels are otherwise built lazily on first
                // navigation) so its launch checks — the app-update prompt, Nexus key
                // re-validation, and the UnrealPak first-run verify/locate/install offer —
                // genuinely run at launch, not whenever the user first happens to open Settings.
                // Deferred one more dispatcher cycle so the window paints first. A first launch
                // that had prerequisites to set up lands on the Settings page, so what the setup
                // flow did (found the game via Steam, installed UnrealPak, is extracting game
                // data) is visible rather than happening behind the Library page — navigated
                // from HERE, after construction returns, because the flow runs inside the
                // constructor and a navigation handled mid-construction would re-resolve this
                // same singleton re-entrantly.
                WhileRunning(System.Windows.Threading.DispatcherPriority.Normal, () =>
                {
                    var settingsViewModel = _host.Services.GetRequiredService<SettingsViewModel>();
                    if (settingsViewModel.FirstLaunchSetupNeededAttention)
                    {
                        WeakReferenceMessenger.Default.Send(new NavigateToPageMessage("settings"));
                    }
                });

                // Another mod manager may have taken nxm:// links since last time: take them back or
                // warn (NxmHandlerWatch). After the Settings first-launch offer above, and again
                // whenever the window comes forward (the other manager was likely just used).
                var nxmHandlerWatch = _host.Services.GetRequiredService<NxmHandlerWatch>();
                WhileRunning(System.Windows.Threading.DispatcherPriority.Background, nxmHandlerWatch.Check);
                Activated += (_, _) => nxmHandlerWatch.Check();
                nxmHandlerWatch.StartWatching(action => WhileRunning(System.Windows.Threading.DispatcherPriority.Normal, action));

                // Has Steam updated Icarus since the mods were last made ready? The Library's bar (and a pop-up) says so.
                WhileRunning(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    _ = _host.Services.GetRequiredService<LibraryViewModel>().CheckForGameUpdateAsync();
                });

                // Dedicated servers: automation (crash restarts, the daily restart, auto-update,
                // "start with Starlink") must run from launch, not from the first visit to the
                // Server page, so its view model is built now when there is any server to watch.
                WhileRunning(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    if (_host.Services.GetRequiredService<IDedicatedServerProfileStore>().GetAll().Count > 0)
                    {
                        _host.Services.GetRequiredService<ServerViewModel>();
                    }
                });

                // The exe is IcarusStarlink.exe since 3.0: move shortcuts and nxm:// links off the
                // old name and remove its launcher, once (ExeRenamePrompt).
                WhileRunning(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
                {
                    _ = ExeRenamePrompt.RunAtStartupAsync(mainWindow, settingsService,
                        _host.Services.GetRequiredService<IcarusStarlink.Core.Nexus.INxmProtocolRegistrar>(),
                        _host.Services.GetRequiredService<IActivityLog>(), existingInstall);
                });

                // This launch's own nxm:// argument (e.g. the very first time the OS protocol
                // registration is used) goes first, then any link a second launch handed over
                // while this one was starting.
                var ownNxmArg = e.Args.FirstOrDefault(arg => arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
                FinishStartupNxmQueue(ownNxmArg);
            });
        }
    }

    /// <summary>
    /// A synchronous exception that escaped all the way out through WPF's own message loop — the
    /// only crash path that could ever have produced a real OS-level artifact (a Windows crash
    /// dump) before this handler existed, since every other path in this app either froze silently
    /// or was swallowed by CommunityToolkit.Mvvm — see CrashReportWriter's own doc comment.
    /// Deliberately leaves e.Handled false: an exception that reached here means something already
    /// went wrong in a way this app can't identify as safe to just shrug off, so the right call is
    /// to report it and let WPF's own default (terminate) proceed, not to guess the app is still in
    /// a good state and keep running.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        CrashReportWriter.Write(_logsDirectory, e.Exception, "UI thread (DispatcherUnhandledException)", TryGetActivityLog());
        MessageBox.Show(
            $"IcarusStarlink hit an unexpected error and needs to close:\n\n{e.Exception.Message}\n\nA crash report was written to the Logs folder.",
            "IcarusStarlink — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>
    /// Fires immediately before the process actually terminates for a truly unhandled exception
    /// (e.IsTerminating is effectively always true here) — nothing can stop the process dying at
    /// this point, and nothing should try; this is purely the last chance to write down what
    /// happened before it's gone.
    /// </summary>
    private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            CrashReportWriter.Write(_logsDirectory, exception, "AppDomain (process terminating)", TryGetActivityLog());
        }
    }

    /// <summary>
    /// Where an exception from an async [RelayCommand] method actually surfaces, now that every
    /// one of them has FlowExceptionsToTaskScheduler = true — without that, CommunityToolkit.Mvvm's
    /// own default behavior stores the exception on the command's internal ExecutionTask and
    /// discards it, so this handler would never fire for the app's own Rebuild/Install/FTP/
    /// download/save-editing commands at all. SetObserved() prevents this from being treated as a
    /// second, redundant unhandled exception once the faulted Task is garbage-collected — this
    /// handler is the one place that exception is actually being handled (logged + reported).
    /// </summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CrashReportWriter.Write(_logsDirectory, e.Exception, "Unobserved task exception (a command or background task)", TryGetActivityLog());
        e.SetObserved();
    }

    /// <summary>
    /// Best-effort resolve of the app's own IActivityLog from the DI host, if one exists yet — the
    /// _host?. null-check alone only guards _host being null (a crash early enough in startup that
    /// the host was never built), not _host being disposed but still non-null (a crash late enough,
    /// e.g. during shutdown, that OnExit's _host?.Dispose() already ran) — GetService on a disposed
    /// ServiceProvider throws ObjectDisposedException, which every handler below used to let escape
    /// as an argument-evaluation exception, before CrashReportWriter.Write was ever even called.
    /// </summary>
    private IActivityLog? TryGetActivityLog() => SafeServiceResolver.TryGetService<IActivityLog>(_host?.Services);

    /// <summary>
    /// Starlink is closing (OnExit has started, or WPF's shutdown has): the host is being disposed
    /// and WPF can't load windows or resources any more. Work queued at startup checks this first.
    /// </summary>
    public static bool IsClosing => s_closing || (Current is { } app && app.Dispatcher.HasShutdownStarted);

    private static volatile bool s_closing;

    /// <summary>
    /// Startup work queued on the dispatcher, skipped once Starlink is closing, and quiet when
    /// Starlink closed while it ran: a step that shows a window (the update offer while Settings is
    /// built) pumps messages, so closing Starlink then disposes the host under it. Closing during
    /// startup (or Windows shutting down) used to run every queued step against the disposed host:
    /// ObjectDisposedException, "The URI prefix is not recognized" loading theme colours, several
    /// crash reports and a hung process (2026-10-26).
    /// </summary>
    private void WhileRunning(System.Windows.Threading.DispatcherPriority priority, Action work) =>
        Dispatcher.BeginInvoke(priority, () =>
        {
            if (IsClosing)
            {
                return;
            }

            try
            {
                work();
            }
            catch (Exception) when (IsClosing)
            {
                // Closed while this ran: nothing left to do it for.
            }
        });

    protected override void OnExit(ExitEventArgs e)
    {
        s_closing = true;
        _singleInstanceService?.Dispose();
        _host?.Services.GetService<NxmHandlerWatch>()?.Stop();
        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
