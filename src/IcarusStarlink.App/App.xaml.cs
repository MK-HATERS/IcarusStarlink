using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
using IcarusStarlink.App.Messages;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.Utilities;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog.AppUpdate;
using IcarusStarlink.Catalog;
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
using IcarusStarlink.PakIO.Assets;
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
        singleInstanceService.StartListening(message => Dispatcher.BeginInvoke(() =>
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

        builder.Services.AddSingleton<ISettingsService>(sp =>
            new AppSettingsService(appDataDirectory, sp.GetRequiredService<ILogger<AppSettingsService>>()));
        builder.Services.AddSingleton(sp => new PerformanceTracker(sp.GetRequiredService<ISettingsService>(), logsDirectory));
        // Wrapped rather than registered directly: LoggingActivityLog bridges every entry into
        // Serilog too, so "what was the user doing right before this" survives a crash instead of
        // only ever living in the plain ActivityLog's in-memory, never-persisted collection. See
        // LoggingActivityLog's own doc comment.
        builder.Services.AddSingleton<ActivityLog>();
        builder.Services.AddSingleton<IActivityLog>(sp =>
            new LoggingActivityLog(sp.GetRequiredService<ActivityLog>(), sp.GetRequiredService<ILogger<LoggingActivityLog>>(), Current.Dispatcher));
        builder.Services.AddSingleton<IActiveDownloadsTracker, ActiveDownloadsTracker>();
        builder.Services.AddSingleton<ActivityPanelViewModel>();
        builder.Services.AddSingleton<ToastsViewModel>();
        builder.Services.AddSingleton<ICustomSkinStore>(sp =>
            new CustomSkinStore(
                Path.Combine(appDataDirectory, "custom_skin.json"),
                sp.GetRequiredService<ILogger<CustomSkinStore>>()));
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<IDialogService, WpfDialogService>();
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
        builder.Services.AddSingleton<IUassetTextureDecoder, CueUassetTextureDecoder>();
        builder.Services.AddSingleton<IUassetStaticMeshDecoder, CueUassetStaticMeshDecoder>();
        builder.Services.AddSingleton<IUassetSkeletalMeshDecoder, CueUassetSkeletalMeshDecoder>();
        builder.Services.AddSingleton<IUassetSoundDecoder, CueUassetSoundDecoder>();
        // Base-game-only CUE4Parse index (Content\Paks, never a mod's own folder) — a DI singleton
        // so its one-time ~550ms mount (lazy, first real use) is paid at most once per app session,
        // not once per material preview. See CueUassetMaterialDecoder's own doc comment for the
        // parent-chain gap this exists to fall back into.
        builder.Services.AddSingleton<IBaseGameContentProvider, CueBaseGameContentProvider>();
        builder.Services.AddSingleton<IUassetMaterialDecoder, CueUassetMaterialDecoder>();
        // Same base-game index as above, wrapped for the Save editor's own item/mount/creature icon
        // lookups (D_Itemable/D_Mounts/D_BestiaryData's own Icon/Image fields) — see
        // IBaseGameIconDecoder's own doc comment for why this isn't just IUassetTextureDecoder again.
        builder.Services.AddSingleton<IBaseGameIconDecoder, CueBaseGameIconDecoder>();
        builder.Services.AddSingleton<IOpaquePakAssetPreviewService, OpaquePakAssetPreviewService>();
        builder.Services.AddSingleton<IWeeklyChangeReportStore>(sp =>
            new WeeklyChangeReportStore(
                Path.Combine(appDataDirectory, "Cache"),
                sp.GetRequiredService<ILogger<WeeklyChangeReportStore>>()));
        builder.Services.AddSingleton<IRebuildService, RebuildService>();
        builder.Services.AddSingleton<IPrebuiltPakToExmodConverter, PrebuiltPakToExmodConverter>();
        builder.Services.AddSingleton<IPrebuiltPakSourceStore>(sp =>
            new PrebuiltPakSourceStore(Path.Combine(appDataDirectory, "PrebuiltPakSources")));
        builder.Services.AddSingleton<IPrebuiltPakImporter, PrebuiltPakImporter>();
        builder.Services.AddSingleton<IInstallService, InstallService>();
        builder.Services.AddSingleton<IPakCompareService, PakCompareService>();
        builder.Services.AddSingleton<IUnrealPakInstaller>(sp =>
            new UnrealPakInstaller(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<HttpClient>(), appDataDirectory));
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
        builder.Services.AddSingleton<NxmHandlerWatch>();
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
        builder.Services.AddSingleton<GameDataIndexCache>();
        builder.Services.AddSingleton<Func<string, ExmodEditorViewModel>>(sp => folderName =>
            new ExmodEditorViewModel(
                folderName, sp.GetRequiredService<ILibraryRepository>(), Path.Combine(appDataDirectory, "Data"),
                sp.GetRequiredService<GameDataIndexCache>()));

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton(sp => new LibraryViewModel(
            sp.GetRequiredService<ILibraryRepository>(),
            sp.GetRequiredService<IUe4ssModRepository>(),
            sp.GetRequiredService<IUe4ssModStateService>(),
            sp.GetRequiredService<IUe4ssModMetaStore>(),
            sp.GetRequiredService<IUe4ssLoaderInstallService>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IUnrealPakService>(),
            sp.GetRequiredService<IUassetTextureDecoder>(),
            sp.GetRequiredService<IUassetStaticMeshDecoder>(),
            sp.GetRequiredService<IUassetSkeletalMeshDecoder>(),
            sp.GetRequiredService<IUassetSoundDecoder>(),
            sp.GetRequiredService<IUassetMaterialDecoder>(),
            sp.GetRequiredService<IOpaquePakAssetPreviewService>(),
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
            sp.GetRequiredService<IPrebuiltPakToExmodConverter>(),
            sp.GetRequiredService<IPrebuiltPakSourceStore>(),
            Path.Combine(appDataDirectory, "Cache", "Thumbnails"),
            Path.Combine(appDataDirectory, "Cache", "PakPreviews"),
            sp.GetRequiredService<IDialogService>())
        {
            // A UE4SS mod's config can also be saved to the mod's copy on these servers.
            ServerProfiles = () => sp.GetRequiredService<IDedicatedServerProfileStore>().GetAll(),
            // Old values from recent game updates: a mod still holding one is an old copy.
            GameHistory = () => sp.GetRequiredService<IWeeklyChangeReportStore>().History
                .SelectMany(report => report.ChangedFiles).SelectMany(file => file.FieldChanges),
            // Each mod's fix-window choices, so they're made by themselves next time.
            FixDecisionsDirectory = Path.Combine(appDataDirectory, "Library_Meta", "Fix_Choices"),
            // The same instance the replacement uses to make fixes again after an update (one lock for every writer).
            SharedFixService = sp.GetRequiredService<ModFixService>(),
            // The game's own files: a mod's copy of one is rated by how much can break (the risk pill).
            BaseGameContent = sp.GetRequiredService<IBaseGameContentProvider>(),
            // After a game update: the bar, the game's files per build (proof a mod is out of date), Update everything.
            GameUpdates = sp.GetRequiredService<GameUpdateDetector>(),
            GameFileFingerprints = sp.GetRequiredService<GameFileFingerprintStore>(),
            GameUpdateCoordinator = sp.GetRequiredService<GameUpdateCoordinator>,
        });
        builder.Services.AddSingleton(sp => new GameUpdateDetector(sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<IUnrealPakService>()));
        builder.Services.AddSingleton(_ => GameFileFingerprintStore.Load(Path.Combine(appDataDirectory, "Cache", "game_file_fingerprints.json")));
        builder.Services.AddSingleton(sp => new GameUpdateCoordinator(
            sp.GetRequiredService<LibraryViewModel>,
            sp.GetRequiredService<MergeInstallViewModel>,
            sp.GetRequiredService<SettingsViewModel>,
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IUe4ssModStateService>(),
            new LastKnownGoodStore(Path.Combine(appDataDirectory, "Backups", "LastKnownGood")),
            sp.GetRequiredService<GameUpdateDetector>(),
            sp.GetRequiredService<IGameProcessChecker>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IActivityLog>(),
            Path.Combine(appDataDirectory, "Backups")));
        // One fix service for the whole app: every writer of a mod's .EXMOD takes its lock.
        builder.Services.AddSingleton(sp => IcarusStarlink.App.Services.ModFixService.Registered(new ModFixService(
            sp.GetRequiredService<ILibraryRepository>(),
            Path.Combine(appDataDirectory, "Data"),
            () => sp.GetRequiredService<IWeeklyChangeReportStore>().History
                .SelectMany(report => report.ChangedFiles).SelectMany(file => file.FieldChanges),
            Path.Combine(appDataDirectory, "Library_Meta", "Fix_Choices"),
            sp.GetRequiredService<IBaseGameContentProvider>() as IGameAssetCatalog)));
        // How every copy of a mod is replaced: Downloads' Activate/Reinstall, Library's Get update and Reinstall original.
        builder.Services.AddSingleton(sp => new ModReplacementService(
            sp.GetRequiredService<ILibraryRepository>(),
            sp.GetRequiredService<IUe4ssModRepository>(),
            sp.GetRequiredService<IUe4ssModMetaStore>(),
            sp.GetRequiredService<IUe4ssModStateService>(),
            sp.GetRequiredService<ISettingsService>(),
            Path.Combine(appDataDirectory, "Backups"),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<ModFixService>(),
            sp.GetRequiredService<IPrebuiltPakSourceStore>()));
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
            Path.Combine(appDataDirectory, "Staged_Build", IcarusStarlink.PakIO.InstallManifestNames.MergedPakFileName),
            Path.Combine(appDataDirectory, "Backups"),
            messenger: null,
            gameProcessChecker: sp.GetRequiredService<IGameProcessChecker>(),
            gameDataFreshness: new GameDataFreshness(sp.GetRequiredService<IUnrealPakService>(), sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<SettingsViewModel>)));
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
            Path.Combine(appDataDirectory, "Data"),
            new JsonCatalogSnapshotStore(Path.Combine(appDataDirectory, "Cache", "catalog_snapshot.json")),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<ModReplacementService>()));
        builder.Services.AddSingleton<NexusCatalogViewModel>();
        builder.Services.AddSingleton<ISaveRepository>(sp =>
            new SaveRepository(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Icarus", "Saved", "PlayerData"),
                Path.Combine(appDataDirectory, "Cache", "save_backups"),
                sp.GetRequiredService<ISteamInstallLocator>()));
        builder.Services.AddSingleton(new SaveGameNames(Path.Combine(appDataDirectory, "Data")));
        builder.Services.AddSingleton<IGameProcessChecker, GameProcessChecker>();
        builder.Services.AddSingleton(sp => new SavesViewModel(
            sp.GetRequiredService<ISaveRepository>(),
            sp.GetRequiredService<IActivityLog>(),
            sp.GetRequiredService<SaveGameNames>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IGameProcessChecker>(),
            sp.GetRequiredService<IBaseGameIconDecoder>(),
            Path.Combine(appDataDirectory, "Cache", "save_backups", "Worlds"),
            new SaveModSources(sp.GetRequiredService<ILibraryRepository>(), sp.GetRequiredService<ISettingsService>(), Path.Combine(appDataDirectory, "Data"),
                Path.Combine(appDataDirectory, "Staged_UE4SS"),
                () => new GameDataFreshness(sp.GetRequiredService<IUnrealPakService>(), sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<SettingsViewModel>)
                    .IsGameDataOutdatedAsync().GetAwaiter().GetResult()).Read,
            () => PlayerGrowthReader.Read(Path.Combine(appDataDirectory, "Data"),
                sp.GetRequiredService<ISettingsService>().Current.IcarusContentPath, sp.GetRequiredService<IBaseGameContentProvider>()),
            new PlayerPointsMod(sp.GetRequiredService<ILibraryRepository>(), sp.GetRequiredService<IBaseGameContentProvider>()),
            () => PlayerGrowthReader.ReadMounts(Path.Combine(appDataDirectory, "Data"),
                sp.GetRequiredService<ISettingsService>().Current.IcarusContentPath, sp.GetRequiredService<IBaseGameContentProvider>())));
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
            sp.GetRequiredService<IActivityLog>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IGameProcessChecker>(),
            Path.Combine(appDataDirectory, "Backups"),
            Path.Combine(appDataDirectory, "Data"),
            logsDirectory,
            Path.Combine(appDataDirectory, "settings.json"),
            Path.Combine(appDataDirectory, "Staged_UE4SS"))
        {
            Ue4ssConfig = new Ue4ssConfigViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IDedicatedServerProfileStore>(),
                Path.Combine(appDataDirectory, "Backups")),
        });
        builder.Services.AddSingleton<WeeklyChangesViewModel>();
        builder.Services.AddSingleton(sp => new DatabaseViewModel(sp.GetRequiredService<NexusCatalogViewModel>(), sp.GetRequiredService<LibraryViewModel>()));
        builder.Services.AddSingleton(sp => new GameEditorViewModel(sp.GetRequiredService<ILibraryRepository>(), Path.Combine(appDataDirectory, "Data"), sp.GetRequiredService<IActivityLog>()));
        builder.Services.AddSingleton(sp => new ServerViewModel(
            sp.GetRequiredService<IFtpSiteStore>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<Func<IFtpClient>>(),
            sp.GetRequiredService<IActivityLog>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IUe4ssModRepository>(),
            sp.GetRequiredService<IDialogService>(),
            Path.Combine(appDataDirectory, "Staged_Build", IcarusStarlink.PakIO.InstallManifestNames.MergedPakFileName),
            sp.GetRequiredService<LocalServerViewModel>()));
        builder.Services.AddSingleton<IDedicatedServerProfileStore>(sp =>
            new DedicatedServerProfileStore(appDataDirectory, sp.GetRequiredService<ILogger<DedicatedServerProfileStore>>()));
        builder.Services.AddSingleton<IDedicatedServerProcessService, DedicatedServerProcessService>();
        builder.Services.AddSingleton<ISteamCmdService>(sp =>
            new SteamCmdService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ISettingsService>(), appDataDirectory));
        builder.Services.AddSingleton(sp => new LocalServerViewModel(
            sp.GetRequiredService<IDedicatedServerProfileStore>(),
            sp.GetRequiredService<IDedicatedServerProcessService>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IActivityLog>(),
            sp.GetRequiredService<ISteamCmdService>(),
            sp.GetRequiredService<ISettingsService>(),
            Path.Combine(appDataDirectory, "Staged_Build", IcarusStarlink.PakIO.InstallManifestNames.MergedPakFileName),
            Path.Combine(appDataDirectory, "Backups"),
            firewall: sp.GetRequiredService<IFirewallService>(),
            dataFolder: Path.Combine(appDataDirectory, "Data")));
        builder.Services.AddSingleton<IFirewallService, FirewallService>();
        builder.Services.AddSingleton<HelpViewModel>();

        builder.Services.AddSingleton<MainWindow>();

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
                Dispatcher.BeginInvoke(() =>
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
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, nxmHandlerWatch.Check);
                Activated += (_, _) => nxmHandlerWatch.Check();
                nxmHandlerWatch.StartWatching(action => Dispatcher.BeginInvoke(action));

                // Has Steam updated Icarus since the mods were last made ready? The Library's bar (and a pop-up) says so.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    _ = _host.Services.GetRequiredService<LibraryViewModel>().CheckForGameUpdateAsync();
                });

                // Dedicated servers: automation (crash restarts, the daily restart, auto-update,
                // "start with Starlink") must run from launch, not from the first visit to the
                // Server page, so its view model is built now when there is any server to watch.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                {
                    if (_host.Services.GetRequiredService<IDedicatedServerProfileStore>().GetAll().Count > 0)
                    {
                        _host.Services.GetRequiredService<ServerViewModel>();
                    }
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

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceService?.Dispose();
        _host?.Services.GetService<NxmHandlerWatch>()?.Stop();
        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
