using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.Messaging;
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
using Microsoft.Extensions.Logging;

namespace IcarusStarlink.App;

/// <summary>Every service and page the app is built from (moved out of App.OnStartup, which only builds and starts the host).</summary>
internal static class AppServices
{
    public static void Register(IServiceCollection services, string appDataDirectory, string logsDirectory)
    {
        services.AddSingleton<ISettingsService>(sp =>
            new AppSettingsService(appDataDirectory, sp.GetRequiredService<ILogger<AppSettingsService>>()));
        services.AddSingleton(sp => new PerformanceTracker(sp.GetRequiredService<ISettingsService>(), logsDirectory));
        // Wrapped rather than registered directly: LoggingActivityLog bridges every entry into
        // Serilog too, so "what was the user doing right before this" survives a crash instead of
        // only ever living in the plain ActivityLog's in-memory, never-persisted collection. See
        // LoggingActivityLog's own doc comment.
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<IActivityLog>(sp =>
            new LoggingActivityLog(sp.GetRequiredService<ActivityLog>(), sp.GetRequiredService<ILogger<LoggingActivityLog>>(), Application.Current.Dispatcher));
        services.AddSingleton<IActiveDownloadsTracker, ActiveDownloadsTracker>();
        services.AddSingleton<ActivityPanelViewModel>();
        services.AddSingleton<ToastsViewModel>();
        services.AddSingleton<ICustomSkinStore>(sp =>
            new CustomSkinStore(
                Path.Combine(appDataDirectory, "custom_skin.json"),
                sp.GetRequiredService<ILogger<CustomSkinStore>>()));
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IDialogService, WpfDialogService>();
        services.AddSingleton<ILibraryRepository>(sp =>
            new FolderLibraryRepository(
                Path.Combine(appDataDirectory, "Extracted_Mods"),
                Path.Combine(appDataDirectory, "Library_Meta"),
                Path.Combine(appDataDirectory, "Backups", "Mods"),
                sp.GetRequiredService<ILogger<FolderLibraryRepository>>()));
        // Same singleton FolderLibraryRepository instance as ILibraryRepository above, just under
        // its other interface — ImportPackage takes a PakIO type (ExmodPackageContents), which
        // ILibraryRepository itself can't expose since Core (where it lives) can't reference
        // PakIO (see IExmodPackageImporter's own doc comment).
        services.AddSingleton<IExmodPackageImporter>(sp => (IExmodPackageImporter)sp.GetRequiredService<ILibraryRepository>());

        // Typed HttpClient registrations: each catalog client's own constructor takes exactly
        // one HttpClient parameter, which is the convention AddHttpClient<TInterface, TClient>
        // relies on to know what to inject.
        services.AddHttpClient<IDaedalusCatalogClient, DaedalusCatalogClient>();
        services.AddHttpClient<IJimk72CatalogClient, Jimk72CatalogClient>();
        services.AddHttpClient<IGitHubRepoDateClient, GitHubRepoDateClient>();
        services.AddHttpClient<INexusApiClient, NexusApiClient>();
        // A plain HttpClient for DownloadsViewModel's own file download, distinct from the two
        // typed clients above (those are scoped to their own catalog JSON endpoints).
        services.AddHttpClient();
        services.AddSingleton(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient());
        services.AddSingleton<INexusWatchlistStore>(sp =>
            new NexusWatchlistStore(
                Path.Combine(appDataDirectory, "Cache"),
                sp.GetRequiredService<ILogger<NexusWatchlistStore>>()));
        services.AddSingleton<IPendingDownloadStore>(sp =>
            new PendingDownloadStore(
                Path.Combine(appDataDirectory, "Cache"),
                sp.GetRequiredService<ILogger<PendingDownloadStore>>()));
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IUnrealPakService, UnrealPakService>();
        services.AddSingleton<IUassetTextureDecoder, CueUassetTextureDecoder>();
        services.AddSingleton<IUassetStaticMeshDecoder, CueUassetStaticMeshDecoder>();
        services.AddSingleton<IUassetSkeletalMeshDecoder, CueUassetSkeletalMeshDecoder>();
        services.AddSingleton<IUassetSoundDecoder, CueUassetSoundDecoder>();
        // Base-game-only CUE4Parse index (Content\Paks, never a mod's own folder) — a DI singleton
        // so its one-time ~550ms mount (lazy, first real use) is paid at most once per app session,
        // not once per material preview. See CueUassetMaterialDecoder's own doc comment for the
        // parent-chain gap this exists to fall back into.
        services.AddSingleton<IBaseGameContentProvider, CueBaseGameContentProvider>();
        services.AddSingleton<IUassetMaterialDecoder, CueUassetMaterialDecoder>();
        // Same base-game index as above, wrapped for the Save editor's own item/mount/creature icon
        // lookups (D_Itemable/D_Mounts/D_BestiaryData's own Icon/Image fields) — see
        // IBaseGameIconDecoder's own doc comment for why this isn't just IUassetTextureDecoder again.
        services.AddSingleton<IBaseGameIconDecoder, CueBaseGameIconDecoder>();
        services.AddSingleton<IOpaquePakAssetPreviewService, OpaquePakAssetPreviewService>();
        services.AddSingleton<IWeeklyChangeReportStore>(sp =>
            new WeeklyChangeReportStore(
                Path.Combine(appDataDirectory, "Cache"),
                sp.GetRequiredService<ILogger<WeeklyChangeReportStore>>()));
        services.AddSingleton<IRebuildService, RebuildService>();
        services.AddSingleton<IPrebuiltPakToExmodConverter, PrebuiltPakToExmodConverter>();
        services.AddSingleton<IPrebuiltPakSourceStore>(sp =>
            new PrebuiltPakSourceStore(Path.Combine(appDataDirectory, "PrebuiltPakSources")));
        services.AddSingleton<IPrebuiltPakImporter, PrebuiltPakImporter>();
        services.AddSingleton<IInstallService, InstallService>();
        services.AddSingleton<IPakCompareService, PakCompareService>();
        services.AddSingleton<IUnrealPakInstaller>(sp =>
            new UnrealPakInstaller(sp.GetRequiredService<IProcessRunner>(), sp.GetRequiredService<HttpClient>(), appDataDirectory));
        services.AddSingleton<IModVersionComparer, ModVersionComparer>();
        services.AddSingleton(sp => new ImmMigrationService(
            sp.GetRequiredService<ILibraryRepository>(),
            sp.GetRequiredService<IDaedalusCatalogClient>(),
            sp.GetRequiredService<IJimk72CatalogClient>(),
            sp.GetRequiredService<INexusApiClient>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<IPrebuiltPakImporter>(),
            sp.GetRequiredService<ISettingsService>(),
            Path.Combine(appDataDirectory, "Data")));
        services.AddSingleton<IProfileStore>(sp =>
            new ProfileStore(
                Path.Combine(appDataDirectory, "Profiles"),
                Path.Combine(appDataDirectory, "Profiles_Backups"),
                sp.GetRequiredService<ILogger<ProfileStore>>()));
        services.AddSingleton<IPatchService, PatchService>();
        services.AddSingleton<IUe4ssModRepository>(sp =>
            new Ue4ssModRepository(Path.Combine(appDataDirectory, "Staged_UE4SS")));
        services.AddSingleton<IUe4ssModStateService, Ue4ssModStateService>();
        services.AddSingleton<IUe4ssModMetaStore>(sp =>
            new Ue4ssModMetaStore(
                Path.Combine(appDataDirectory, "Ue4ss_Meta"),
                sp.GetRequiredService<ILogger<Ue4ssModMetaStore>>()));
        services.AddSingleton<ISteamInstallLocator, SteamInstallLocator>();
        services.AddSingleton<ICredentialStore, WindowsCredentialStore>();
        services.AddSingleton<INxmProtocolRegistrar, NxmProtocolRegistrar>();
        services.AddSingleton<NxmHandlerWatch>();
        services.AddSingleton<IUe4ssLoaderInstallService, Ue4ssLoaderInstallService>();
        services.AddHttpClient<IUe4ssReleaseClient, Ue4ssReleaseClient>();
        services.AddHttpClient<IAppUpdateClient, AppUpdateClient>();
        services.AddSingleton<IFtpSiteStore>(sp =>
            new FtpSiteStore(
                Path.Combine(appDataDirectory, "Cache"),
                sp.GetRequiredService<ILogger<FtpSiteStore>>()));
        // A fresh IFtpClient per connect, not a shared singleton — one instance = one live
        // connection (see FluentFtpClient's own doc comment), and ServerViewModel replaces it on
        // every Connect/Disconnect cycle.
        services.AddSingleton<Func<IFtpClient>>(() => new FluentFtpClient());

        // Transient, not a singleton — every other ViewModel in this app is constructed once and
        // reused, but the EXMOD editor is per-open-instance (Phase 7.1: classic IMM's own editor
        // really does support two independent mods open at once). Registered as a factory
        // delegate, not AddTransient<T>, since the folder name it edits is only known at the
        // moment Library's Edit…/New mod… actions fire, not at DI composition time.
        services.AddSingleton<GameDataIndexCache>();
        services.AddSingleton<Func<string, ExmodEditorViewModel>>(sp => folderName =>
            new ExmodEditorViewModel(
                folderName, sp.GetRequiredService<ILibraryRepository>(), Path.Combine(appDataDirectory, "Data"),
                sp.GetRequiredService<GameDataIndexCache>()));

        services.AddSingleton<MainViewModel>();
        services.AddSingleton(sp => new LibraryViewModel(
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
        services.AddSingleton(sp => new GameUpdateDetector(sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<IUnrealPakService>()));
        services.AddSingleton(_ => GameFileFingerprintStore.Load(Path.Combine(appDataDirectory, "Cache", "game_file_fingerprints.json")));
        services.AddSingleton(sp => new GameUpdateCoordinator(
            sp.GetRequiredService<LibraryViewModel>,
            sp.GetRequiredService<MergeInstallViewModel>,
            sp.GetRequiredService<SettingsViewModel>,
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IUe4ssModStateService>(),
            new LastKnownGoodStore(Path.Combine(appDataDirectory, "Backups", "LastKnownGood"), sp.GetRequiredService<ISettingsService>()),
            sp.GetRequiredService<GameUpdateDetector>(),
            sp.GetRequiredService<IGameProcessChecker>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IActivityLog>(),
            Path.Combine(appDataDirectory, "Backups"),
            sp.GetRequiredService<WorldMapViewModel>));
        // One fix service for the whole app: every writer of a mod's .EXMOD takes its lock.
        services.AddSingleton(sp => IcarusStarlink.App.Services.ModFixService.Registered(new ModFixService(
            sp.GetRequiredService<ILibraryRepository>(),
            Path.Combine(appDataDirectory, "Data"),
            () => sp.GetRequiredService<IWeeklyChangeReportStore>().History
                .SelectMany(report => report.ChangedFiles).SelectMany(file => file.FieldChanges),
            Path.Combine(appDataDirectory, "Library_Meta", "Fix_Choices"),
            sp.GetRequiredService<IBaseGameContentProvider>() as IGameAssetCatalog)));
        // How every copy of a mod is replaced: Downloads' Activate/Reinstall, Library's Get update and Reinstall original.
        services.AddSingleton(sp => new ModReplacementService(
            sp.GetRequiredService<ILibraryRepository>(),
            sp.GetRequiredService<IUe4ssModRepository>(),
            sp.GetRequiredService<IUe4ssModMetaStore>(),
            sp.GetRequiredService<IUe4ssModStateService>(),
            sp.GetRequiredService<ISettingsService>(),
            Path.Combine(appDataDirectory, "Backups"),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<ModFixService>(),
            sp.GetRequiredService<IPrebuiltPakSourceStore>(),
            Path.Combine(appDataDirectory, "Cache", "PakPreviews")));
        services.AddSingleton(sp => new MergeInstallViewModel(
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
            gameDataFreshness: new GameDataFreshness(sp.GetRequiredService<IUnrealPakService>(), sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<SettingsViewModel>),
            dialogService: sp.GetRequiredService<IDialogService>(),
            // The Library's health check, for the merge check's Per mod findings.
            fixService: sp.GetRequiredService<ModFixService>(),
            // Share a mod list: the UE4SS mods on in the game travel in a patch.
            ue4ssStateService: sp.GetRequiredService<IUe4ssModStateService>(),
            ue4ssRepository: sp.GetRequiredService<IUe4ssModRepository>(),
            ue4ssMetaStore: sp.GetRequiredService<IUe4ssModMetaStore>(),
            // Import patch fetches the mods a patch references through the Downloads page (registered below: resolved lazily).
            patchFetcher: new DownloadsPatchFetcher(sp.GetRequiredService<DownloadsViewModel>, sp.GetRequiredService<ICredentialStore>())));
        services.AddSingleton(sp => new DownloadsViewModel(
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
        services.AddSingleton<NexusCatalogViewModel>();
        services.AddSingleton<ISaveRepository>(sp =>
            new SaveRepository(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Icarus", "Saved", "PlayerData"),
                Path.Combine(appDataDirectory, "Cache", "save_backups"),
                sp.GetRequiredService<ISteamInstallLocator>(),
                sp.GetRequiredService<ISettingsService>()));
        services.AddSingleton(new SaveGameNames(Path.Combine(appDataDirectory, "Data")));
        services.AddSingleton<IGameProcessChecker, GameProcessChecker>();
        services.AddSingleton(sp => new SavesViewModel(
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
                    .IsGameDataOutdatedAsync().GetAwaiter().GetResult(), sp.GetRequiredService<SaveGameNames>()).Read,
            () => PlayerGrowthReader.Read(Path.Combine(appDataDirectory, "Data"),
                sp.GetRequiredService<ISettingsService>().Current.IcarusContentPath, sp.GetRequiredService<IBaseGameContentProvider>()),
            new PlayerPointsMod(sp.GetRequiredService<ILibraryRepository>(), sp.GetRequiredService<IBaseGameContentProvider>()),
            () => PlayerGrowthReader.ReadMounts(Path.Combine(appDataDirectory, "Data"),
                sp.GetRequiredService<ISettingsService>().Current.IcarusContentPath, sp.GetRequiredService<IBaseGameContentProvider>())));
        services.AddSingleton(sp => new SettingsViewModel(
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
            Path.Combine(appDataDirectory, "Staged_UE4SS"),
            sp.GetRequiredService<IGameDataFreshness>())
        {
            Ue4ssConfig = new Ue4ssConfigViewModel(
                sp.GetRequiredService<ISettingsService>(),
                sp.GetRequiredService<IDedicatedServerProfileStore>(),
                Path.Combine(appDataDirectory, "Backups")),
        });
        services.AddSingleton<IGameDataFreshness>(sp => new GameDataFreshness(
            sp.GetRequiredService<IUnrealPakService>(), sp.GetRequiredService<ISettingsService>(), sp.GetRequiredService<SettingsViewModel>));
        services.AddSingleton<WeeklyChangesViewModel>();
        // The Map page: worlds and zones read from the extracted tables and Paks\mods, pictures and
        // zone images drawn from the game's paks into one kept copy (Cache\World).
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<ISettingsService>();
            var dataFolder = Path.Combine(appDataDirectory, "Data");
            return new IcarusStarlink.PakIO.World.WorldMapStore(
                Path.Combine(appDataDirectory, "Cache", "World"),
                () => IcarusStarlink.PakIO.World.WorldMapCatalog.Read(dataFolder, settings.Current.IcarusContentPath),
                new IcarusStarlink.PakIO.World.WorldMapRenderer(sp.GetRequiredService<IBaseGameContentProvider>()));
        });
        services.AddSingleton(sp => new WorldMapViewModel(
            sp.GetRequiredService<IcarusStarlink.PakIO.World.WorldMapStore>(), sp.GetRequiredService<ISettingsService>(), Path.Combine(appDataDirectory, "Data")));
        services.AddSingleton(sp => new DatabaseViewModel(sp.GetRequiredService<NexusCatalogViewModel>(), sp.GetRequiredService<LibraryViewModel>()));
        services.AddSingleton(sp => new GameEditorViewModel(sp.GetRequiredService<ILibraryRepository>(), Path.Combine(appDataDirectory, "Data"), sp.GetRequiredService<IActivityLog>()));
        services.AddSingleton(sp => new ServerViewModel(
            sp.GetRequiredService<IFtpSiteStore>(),
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<Func<IFtpClient>>(),
            sp.GetRequiredService<IActivityLog>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IDialogService>(),
            Path.Combine(appDataDirectory, "Staged_Build", IcarusStarlink.PakIO.InstallManifestNames.MergedPakFileName),
            sp.GetRequiredService<LocalServerViewModel>()));
        services.AddSingleton<IDedicatedServerProfileStore>(sp =>
            new DedicatedServerProfileStore(appDataDirectory, sp.GetRequiredService<ILogger<DedicatedServerProfileStore>>()));
        services.AddSingleton<IDedicatedServerProcessService, DedicatedServerProcessService>();
        services.AddSingleton<ISteamCmdService>(sp =>
            new SteamCmdService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ISettingsService>(), appDataDirectory));
        services.AddSingleton(sp => new LocalServerViewModel(
            sp.GetRequiredService<IDedicatedServerProfileStore>(),
            sp.GetRequiredService<IDedicatedServerProcessService>(),
            sp.GetRequiredService<IDialogService>(),
            sp.GetRequiredService<IActivityLog>(),
            sp.GetRequiredService<ISteamCmdService>(),
            sp.GetRequiredService<ISettingsService>(),
            Path.Combine(appDataDirectory, "Staged_Build", IcarusStarlink.PakIO.InstallManifestNames.MergedPakFileName),
            Path.Combine(appDataDirectory, "Backups"),
            firewall: sp.GetRequiredService<IFirewallService>(),
            dataFolder: Path.Combine(appDataDirectory, "Data"),
            // Share with players: a patch of what the server runs, and a patch applied to it (Merge & Install does the Library side).
            library: sp.GetRequiredService<ILibraryRepository>(),
            patchService: sp.GetRequiredService<IPatchService>(),
            ue4ssMetaStore: sp.GetRequiredService<IUe4ssModMetaStore>(),
            patchImporter: sp.GetRequiredService<MergeInstallViewModel>(),
            network: sp.GetRequiredService<INetworkAddressService>()));
        services.AddSingleton<IFirewallService, FirewallService>();
        // The Connect card's addresses: this PC's adapters, and api.ipify.org only on "Show my public IP".
        services.AddSingleton<INetworkAddressService>(sp => new NetworkAddressService(sp.GetRequiredService<HttpClient>()));
        // Find the broken mod (Merge & Install › More ▾): rounds of part-installs through Merge &
        // Install, the search saved beside the settings so a closed app can still put the mods back.
        services.AddSingleton(sp => new FindBrokenModLauncher(() => new FindBrokenModContext
        {
            Host = new MergeInstallFinderHost(
                sp.GetRequiredService<MergeInstallViewModel>,
                sp.GetRequiredService<ISaveRepository>(),
                sp.GetRequiredService<IGameProcessChecker>(),
                Path.Combine(appDataDirectory, "Cache", "save_backups", "Worlds")),
            Store = new BrokenModSearchStore(Path.Combine(appDataDirectory, "find_broken_mod.json")),
            Dialogs = sp.GetRequiredService<IDialogService>(),
            ReadCandidates = () => sp.GetRequiredService<MergeInstallViewModel>().ReadFinderCandidatesAsync(),
            GameIndex = () => sp.GetRequiredService<MergeInstallViewModel>().FinderGameIndex() is { } index && index.HasAnyTable() ? index : null,
            GameRows = () => sp.GetRequiredService<GameDataIndexCache>().GetItemIndexAsync(Path.Combine(appDataDirectory, "Data")),
            ProblemCounts = async () =>
            {
                var mergeInstall = sp.GetRequiredService<MergeInstallViewModel>();
                await mergeInstall.RecomputeChecksAsync();
                return mergeInstall.ModsWithProblems.ToDictionary(p => p.FolderName, p => p.ProblemCount, StringComparer.OrdinalIgnoreCase);
            },
            // The Library's fix window, as Merge & Install's check opens it.
            OpenFixWindow = async folderName =>
            {
                var request = ((IMessenger)WeakReferenceMessenger.Default).Send(new IcarusStarlink.App.Messages.OpenModFixMessage(folderName));
                return request.HasReceivedResponse ? await request.Response : "the Library page isn't ready yet. Try again in a moment.";
            },
            NexusModIdOf = folderName => sp.GetRequiredService<ILibraryRepository>().GetAll()
                .FirstOrDefault(e => string.Equals(e.FolderName, folderName, StringComparison.OrdinalIgnoreCase))?.NexusModId,
            OpenUrl = url => UrlOpener.TryOpen(url),
        }));
        services.AddSingleton<HelpViewModel>();

        services.AddSingleton<MainWindow>();
    }
}
