using System.IO;
using System.Net.Http;
using System.Windows;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Catalog.Daedalus;
using IcarusStarlink.Catalog.GitHub;
using IcarusStarlink.Catalog.Jimk72;
using IcarusStarlink.Core.Catalog;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Settings;
using IcarusStarlink.PakIO.DataChanges;
using IcarusStarlink.PakIO.Pak;
using IcarusStarlink.PakIO.Rebuild;
using IcarusStarlink.Storage.Catalog;
using IcarusStarlink.Storage.Library;
using IcarusStarlink.Storage.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace IcarusStarlink.App;

public partial class App : Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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
        builder.Services.AddSingleton<IThemeService, ThemeService>();
        builder.Services.AddSingleton<ILibraryRepository>(sp =>
            new FolderLibraryRepository(
                Path.Combine(appDataDirectory, "Extracted_Mods"),
                Path.Combine(appDataDirectory, "Library_Meta"),
                sp.GetRequiredService<ILogger<FolderLibraryRepository>>()));

        // Typed HttpClient registrations: each catalog client's own constructor takes exactly
        // one HttpClient parameter, which is the convention AddHttpClient<TInterface, TClient>
        // relies on to know what to inject.
        builder.Services.AddHttpClient<IDaedalusCatalogClient, DaedalusCatalogClient>();
        builder.Services.AddHttpClient<IJimk72CatalogClient, Jimk72CatalogClient>();
        builder.Services.AddHttpClient<IGitHubRepoDateClient, GitHubRepoDateClient>();
        // A plain HttpClient for DownloadsViewModel's own file download, distinct from the two
        // typed clients above (those are scoped to their own catalog JSON endpoints).
        builder.Services.AddHttpClient();
        builder.Services.AddSingleton(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient());
        builder.Services.AddSingleton<INexusWatchlistStore>(sp =>
            new NexusWatchlistStore(
                Path.Combine(appDataDirectory, "Cache"),
                sp.GetRequiredService<ILogger<NexusWatchlistStore>>()));
        builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
        builder.Services.AddSingleton<IUnrealPakService, UnrealPakService>();
        builder.Services.AddSingleton<IWeeklyChangeReportStore>(sp =>
            new WeeklyChangeReportStore(
                Path.Combine(appDataDirectory, "Cache"),
                sp.GetRequiredService<ILogger<WeeklyChangeReportStore>>()));
        builder.Services.AddSingleton<IRebuildService, RebuildService>();

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<LibraryViewModel>();
        builder.Services.AddSingleton(sp => new MergeInstallViewModel(
            sp.GetRequiredService<ILibraryRepository>(),
            sp.GetRequiredService<IRebuildService>(),
            sp.GetRequiredService<ISettingsService>(),
            Path.Combine(appDataDirectory, "Data"),
            Path.Combine(appDataDirectory, "Staged_Build", "IMM_Merged_Mod_P.pak")));
        builder.Services.AddSingleton<DownloadsViewModel>();
        builder.Services.AddSingleton(sp => new SettingsViewModel(
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<IUnrealPakService>(),
            sp.GetRequiredService<IWeeklyChangeReportStore>(),
            Path.Combine(appDataDirectory, "Data")));
        builder.Services.AddSingleton<WeeklyChangesViewModel>();

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
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
