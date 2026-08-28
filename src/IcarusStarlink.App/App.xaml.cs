using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Catalog.Nexus;
using IcarusStarlink.Catalog.Ue4ss;
using IcarusStarlink.Core.Activity;
using IcarusStarlink.Core.Catalog;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Nexus;
using IcarusStarlink.Core.Profiles;
using IcarusStarlink.Core.Secrets;
using IcarusStarlink.Core.Server;
using IcarusStarlink.Core.Saves;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.Core.Skins;
using IcarusStarlink.Core.Steam;
using IcarusStarlink.Core.Ue4ss;
using IcarusStarlink.PakIO.Compare;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Import;
using IcarusStarlink.PakIO.Install;
using IcarusStarlink.PakIO.Pak;
using IcarusStarlink.PakIO.Patches;
using IcarusStarlink.PakIO.Rebuild;
using IcarusStarlink.Storage.Catalog;
using IcarusStarlink.Storage.Library;
using IcarusStarlink.Storage.Nexus;
using IcarusStarlink.Storage.Profiles;
using IcarusStarlink.Storage.Secrets;
using IcarusStarlink.Storage.Server;
using IcarusStarlink.Storage.Saves;
using IcarusStarlink.Storage.Settings;
using IcarusStarlink.Storage.Skins;
using IcarusStarlink.Storage.Steam;
using IcarusStarlink.Storage.Ue4ss;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace IcarusStarlink.App;

public partial class App : Application
{
    private IHost? _host;
    private SingleInstanceService? _singleInstanceService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A real nxm:// click launches a brand-new process by default — the OS has no idea
        // IcarusStarlink might already be running. If we're the second one, hand our own nxm://
        // argument (if any) to the first instance over a named pipe and exit immediately, before
        // ever touching the DI host/game-data scans/etc. that a normal launch does.
        var singleInstanceService = new SingleInstanceService();
        if (!singleInstanceService.IsFirstInstance)
        {
            var handoffUrl = e.Args.FirstOrDefault(arg => arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
            if (handoffUrl is not null)
            {
                SingleInstanceService.SendToRunningInstance(handoffUrl);
            }

            singleInstanceService.Dispose();
            Shutdown();
            return;
        }

        _singleInstanceService = singleInstanceService;

        // Started immediately, before the (potentially slow — DI host build, HTTP client
        // registration, Library scan, window show) rest of startup below: a second instance only
        // waits a few seconds on SendToRunningInstance before giving up silently, so if the pipe
        // server didn't exist yet until after all of that finished, a real nxm:// click during a
        // cold app start could easily lose the race and vanish with no error anywhere. Safe to
        // call this early — HandleNxmUrl below runs via Dispatcher.BeginInvoke, which the WPF
        // message loop won't actually pump until OnStartup returns, by which point _host (assigned
        // further down) is always already set.
        singleInstanceService.StartListening(nxmUrl => Dispatcher.BeginInvoke(() => HandleNxmUrl(nxmUrl)));

        var appDataDirectory = AppContext.BaseDirectory;
        var logsDirectory = Path.Combine(appDataDirectory, "Logs");
        Directory.CreateDirectory(logsDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDirectory, "icarusstarlink-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(dispose: true);

        builder.Services.AddSingleton<ISettingsService>(sp =>
            new AppSettingsService(appDataDirectory, sp.GetRequiredService<ILogger<AppSettingsService>>()));
        builder.Services.AddSingleton(sp => new PerformanceTracker(sp.GetRequiredService<ISettingsService>(), logsDirectory));
        builder.Services.AddSingleton<IActivityLog, ActivityLog>();
        builder.Services.AddSingleton<IActiveDownloadsTracker, ActiveDownloadsTracker>();
        builder.Services.AddSingleton<ActivityPanelViewModel>();
        builder.Services.AddSingleton<ICustomSkinStore>(sp =>
            new CustomSkinStore(
                Path.Combine(appDataDirectory, "custom_skin.json"),
                sp.GetRequiredService<ILogger<CustomSkinStore>>()));
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<ILibraryRepository>(sp =>
            new FolderLibraryRepository(
                Path.Combine(appDataDirectory, "Extracted_Mods"),
                Path.Combine(appDataDirectory, "Library_Meta"),
                Path.Combine(appDataDirectory, "Backups", "Mods"),
                sp.GetRequiredService<ILogger<FolderLibraryRepository>>()));
        // Same singleton FolderLibraryRepository instance as ILibraryRepository above, just under
        // its other interface — ImportPackage takes a PakIO type (ExmodPackageContents), which
        // ILibraryRepository itself can't expose since Core (where it lives) can't reference
        // PakIO (see IExmodPackageImporter's own doc comment).
        builder.Services.AddSingleton<IExmodPackageImporter>(sp => (IExmodPackageImporter)sp.GetRequiredService<ILibraryRepository>());

        // Typed HttpClient registrations: each catalog client's own constructor takes exactly
        // one HttpClient parameter, which is the convention AddHttpClient<TInterface, TClient>
        // relies on to know what to inject.
        builder.Services.AddHttpClient<IDaedalusCatalogClient, DaedalusCatalogClient>();
        builder.Services.AddHttpClient<IJimk72CatalogClient, Jimk72CatalogClient>();
        builder.Services.AddHttpClient<IGitHubRepoDateClient, GitHubRepoDateClient>();
        builder.Services.AddHttpClient<INexusApiClient, NexusApiClient>();
        // A plain HttpClient for DownloadsViewModel's own file download, distinct from the two
        // typed clients above (those are scoped to their own catalog JSON endpoints).
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient());
        builder.Services.AddSingleton<INexusWatchlistStore>(sp =>
            new NexusWatchlistStore(
                Path.Combine(appDataDirectory, "Cache"),
                sp.GetRequiredService<ILogger<NexusWatchlistStore>>()));
        builder.Services.AddSingleton<IPendingDownloadStore>(sp =>
            new PendingDownloadStore(
                Path.Combine(appDataDirectory, "Cache"),
                sp.GetRequiredService<ILogger<PendingDownloadStore>>()));
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<IUnrealPakService, UnrealPakService>();
        builder.Services.AddSingleton<IWeeklyChangeReportStore>(sp =>
            new WeeklyChangeReportStore(
                Path.Combine(appDataDirectory, "Cache"),
                sp.GetRequiredService<ILogger<WeeklyChangeReportStore>>()));
        builder.Services.AddSingleton<IRebuildService, RebuildService>();
        builder.Services.AddSingleton<IPrebuiltPakToExmodConverter, PrebuiltPakToExmodConverter>();
        builder.Services.AddSingleton<IPrebuiltPakImporter, PrebuiltPakImporter>();
        builder.Services.AddSingleton<IInstallService, InstallService>();
        builder.Services.AddSingleton<IPakCompareService, PakCompareService>();
        builder.Services.AddSingleton<IUnrealPakInstaller>(sp =>
            new UnrealPakInstaller(sp.GetRequiredService<IProcessRunner>(), appDataDirectory));
        builder.Services.AddSingleton<IModVersionComparer, ModVersionComparer>();
        builder.Services.AddSingleton(sp => new ImmMigrationService(
            sp.GetRequiredService<ILibraryRepository>(),
            sp.GetRequiredService<IDaedalusCatalogClient>(),
            sp.GetRequiredService<IJimk72CatalogClient>(),
            sp.GetRequiredService<INexusApiClient>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<IPrebuiltPakImporter>(),
            sp.GetRequiredService<ISettingsService>(),
            Path.Combine(appDataDirectory, "Data")));
        builder.Services.AddSingleton<IProfileStore>(sp =>
            new ProfileStore(
                Path.Combine(appDataDirectory, "Profiles"),
                Path.Combine(appDataDirectory, "Profiles_Backups"),
                sp.GetRequiredService<ILogger<ProfileStore>>()));
        builder.Services.AddSingleton<IPatchService, PatchService>();
        builder.Services.AddSingleton<IUe4ssModRepository>(sp =>
            new Ue4ssModRepository(Path.Combine(appDataDirectory, "Staged_UE4SS")));
        builder.Services.AddSingleton<IUe4ssModStateService, Ue4ssModStateService>();
        builder.Services.AddSingleton<IUe4ssModMetaStore>(sp =>
            new Ue4ssModMetaStore(
                Path.Combine(appDataDirectory, "Ue4ss_Meta"),
                sp.GetRequiredService<ILogger<Ue4ssModMetaStore>>()));
        builder.Services.AddSingleton<ISteamInstallLocator, SteamInstallLocator>();
        builder.Services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        builder.Services.AddSingleton<INxmProtocolRegistrar, NxmProtocolRegistrar>();
        builder.Services.AddSingleton<IUe4ssLoaderInstallService, Ue4ssLoaderInstallService>();
        builder.Services.AddHttpClient<IUe4ssReleaseClient, Ue4ssReleaseClient>();
        builder.Services.AddHttpClient<IAppUpdateClient, AppUpdateClient>();
        builder.Services.AddSingleton<IFtpSiteStore>(sp =>
            new FtpSiteStore(
                Path.Combine(appDataDirectory, "Cache"),
                sp.GetRequiredService<ILogger<FtpSiteStore>>()));
        // A fresh IFtpClient per connect, not a shared singleton — one instance = one live
        // connection (see FluentFtpClient's own doc comment), and ServerViewModel replaces it on
        // every Connect/Disconnect cycle.
        builder.Services.AddSingleton<Func<IFtpClient>>(() => new FluentFtpClient());

        // Transient, not a singleton — every other ViewModel in this app is constructed once and
        // reused, but the EXMOD editor is per-open-instance (Phase 7.1: classic IMM's own editor
        // really does support two independent mods open at once). Registered as a factory
        // delegate, not AddTransient<T>, since the folder name it edits is only known at the
        // moment Library's Edit…/New mod… actions fire, not at DI composition time.
        builder.Services.AddSingleton<Func<string, ExmodEditorViewModel>>(sp => folderName =>
            new ExmodEditorViewModel(folderName, sp.GetRequiredService<ILibraryRepository>(), Path.Combine(appDataDirectory, "Data")));

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton(sp => new LibraryViewModel(
            sp.GetRequiredService<ILibraryRepository>(),
            sp.GetRequiredService<IUe4ssModRepository>(),
            sp.GetRequiredService<IUe4ssModStateService>(),
            sp.GetRequiredService<IUe4ssModMetaStore>(),
            sp.GetRequiredService<IUe4ssLoaderInstallService>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IUnrealPakService>(),
            sp.GetRequiredService<INexusApiClient>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<Func<string, ExmodEditorViewModel>>(),
            sp.GetRequiredService<IActivityLog>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IPendingDownloadStore>(),
            sp.GetRequiredService<IModVersionComparer>(),
            sp.GetRequiredService<DownloadsViewModel>(),
            sp.GetRequiredService<NexusCatalogViewModel>,
            sp.GetRequiredService<IActiveDownloadsTracker>(),
            sp.GetRequiredService<MergeInstallViewModel>,
            Path.Combine(appDataDirectory, "Backups"),
            Path.Combine(appDataDirectory, "Data"),
            sp.GetRequiredService<IPrebuiltPakImporter>(),
            sp.GetRequiredService<IPrebuiltPakToExmodConverter>()));
        builder.Services.AddSingleton(sp => new MergeInstallViewModel(
            sp.GetRequiredService<ILibraryRepository>(),
            sp.GetRequiredService<IRebuildService>(),
            sp.GetRequiredService<IInstallService>(),
            sp.GetRequiredService<IProfileStore>(),
            sp.GetRequiredService<IPatchService>(),
            sp.GetRequiredService<IDaedalusCatalogClient>(),
            sp.GetRequiredService<IJimk72CatalogClient>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IPakCompareService>(),
            sp.GetRequiredService<PerformanceTracker>(),
            sp.GetRequiredService<IActivityLog>(),
            Path.Combine(appDataDirectory, "Data"),
            Path.Combine(appDataDirectory, "Staged_Build", "ISL-Merged_P.pak"),
            Path.Combine(appDataDirectory, "Backups")));
        builder.Services.AddSingleton(sp => new DownloadsViewModel(
            sp.GetRequiredService<IDaedalusCatalogClient>(),
            sp.GetRequiredService<IJimk72CatalogClient>(),
            sp.GetRequiredService<IGitHubRepoDateClient>(),
            sp.GetRequiredService<ILibraryRepository>(),
            sp.GetRequiredService<IUe4ssModRepository>(),
            sp.GetRequiredService<IUe4ssModMetaStore>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<INexusApiClient>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<IPendingDownloadStore>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<PerformanceTracker>(),
            sp.GetRequiredService<IActivityLog>(),
            sp.GetRequiredService<IActiveDownloadsTracker>(),
            Path.Combine(appDataDirectory, "Pending_Downloads"),
            sp.GetRequiredService<IPrebuiltPakImporter>(),
            Path.Combine(appDataDirectory, "Data")));
        builder.Services.AddSingleton<NexusCatalogViewModel>();
        builder.Services.AddSingleton<ISaveRepository>(sp =>
            new SaveRepository(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Icarus", "Saved", "PlayerData"),
                Path.Combine(appDataDirectory, "Cache", "save_backups"),
                sp.GetRequiredService<ISteamInstallLocator>()));
        builder.Services.AddSingleton(new SaveGameNames(Path.Combine(appDataDirectory, "Data")));
        builder.Services.AddSingleton<SavesViewModel>();
        builder.Services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IUnrealPakService>(),
            sp.GetRequiredService<IWeeklyChangeReportStore>(),
            sp.GetRequiredService<ISteamInstallLocator>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<INexusApiClient>(),
            sp.GetRequiredService<INxmProtocolRegistrar>(),
            sp.GetRequiredService<IUe4ssLoaderInstallService>(),
            sp.GetRequiredService<IUe4ssReleaseClient>(),
            sp.GetRequiredService<IAppUpdateClient>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IThemeService>(),
            sp.GetRequiredService<ICustomSkinStore>(),
            sp.GetRequiredService<ImmMigrationService>(),
            sp.GetRequiredService<MergeInstallViewModel>,
            sp.GetRequiredService<IUnrealPakInstaller>(),
            Path.Combine(appDataDirectory, "Backups"),
            Path.Combine(appDataDirectory, "Data"),
            logsDirectory,
            Path.Combine(appDataDirectory, "settings.json"),
            Path.Combine(appDataDirectory, "Staged_UE4SS")));
        builder.Services.AddSingleton<WeeklyChangesViewModel>();
        builder.Services.AddSingleton(sp => new ServerViewModel(
            sp.GetRequiredService<IFtpSiteStore>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<Func<IFtpClient>>(),
            sp.GetRequiredService<IActivityLog>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IUe4ssModRepository>(),
            Path.Combine(appDataDirectory, "Staged_Build", "ISL-Merged_P.pak")));
        builder.Services.AddSingleton<HelpViewModel>();

        builder.Services.AddSingleton<MainWindow>();

        _host = builder.Build();
        _host.Start();

        var logger = _host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("IcarusStarlink starting up");

        var themeService = _host.Services.GetRequiredService<IThemeService>();
        var settingsService = _host.Services.GetRequiredService<ISettingsService>();
        themeService.ApplyTheme(settingsService.Current.ThemeName);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        // Deferred to the next dispatcher cycle so the window paints once before the default
        // page (Library) resolves a repository that scans Extracted_Mods — otherwise that scan
        // would run before the window ever appears at all. This only moves the freeze to just
        // after the window shows, not off the UI thread entirely: for the "dozens of mods"
        // library size this is designed around, that scan is fast enough not to matter; a truly
        // large library would still visibly hang the window here. Making the scan itself
        // non-blocking (background thread + a loading state in the Library UI) is future work if
        // that assumption stops holding.
        var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
        Dispatcher.BeginInvoke(mainViewModel.SelectDefaultPage);

        // Constructed eagerly (page ViewModels are otherwise built lazily on first navigation) so
        // its launch checks — the app-update prompt, Nexus key re-validation, and the UnrealPak
        // first-run verify/locate/install offer — genuinely run at launch, not whenever the user
        // first happens to open Settings. Queued behind SelectDefaultPage so the window paints
        // and lands on its default page before any of those checks can show a dialog.
        Dispatcher.BeginInvoke(() => _host.Services.GetRequiredService<SettingsViewModel>());

        // Handle the case where THIS launch (the first instance) was itself invoked with an
        // nxm:// argument — e.g. the very first time the OS protocol registration is used.
        var ownNxmArg = e.Args.FirstOrDefault(arg => arg.StartsWith("nxm://", StringComparison.OrdinalIgnoreCase));
        if (ownNxmArg is not null)
        {
            Dispatcher.BeginInvoke(() => HandleNxmUrl(ownNxmArg));
        }
    }

    private void HandleNxmUrl(string nxmUrl)
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

        // The pending-downloads / Fetch pipeline lives on Library's own Mods tab now — land there
        // (tab index 0) instead of the removed standalone Downloads page.
        var mainViewModel = _host.Services.GetRequiredService<MainViewModel>();
        mainViewModel.SelectedNavItem = mainViewModel.NavItems.First(item => item.Id == "library");

        var libraryViewModel = _host.Services.GetRequiredService<LibraryViewModel>();
        libraryViewModel.SelectedTabIndex = 0;
        _ = libraryViewModel.Downloads.FetchAndDownloadAsync(nxmUrl);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceService?.Dispose();
        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
