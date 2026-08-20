using System.IO;
using System.Windows;
using IcarusStarlink.App.Services;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Library;
using IcarusStarlink.Core.Settings;
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

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<LibraryViewModel>();
        builder.Services.AddSingleton<MergeInstallViewModel>();
        builder.Services.AddSingleton<DownloadsViewModel>();
        builder.Services.AddSingleton<ProfilesViewModel>();
        builder.Services.AddSingleton<Ue4ssViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddSingleton<DiagnosticsViewModel>();

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
