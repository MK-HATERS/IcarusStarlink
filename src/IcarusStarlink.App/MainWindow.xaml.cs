using System.Windows;
using IcarusStarlink.App.ViewModels;
using IcarusStarlink.Core.Settings;

namespace IcarusStarlink.App;

public partial class MainWindow : Window
{
    private readonly ISettingsService _settingsService;

    public MainWindow(MainViewModel viewModel, ISettingsService settingsService)
    {
        InitializeComponent();
        DataContext = viewModel;
        _settingsService = settingsService;

        RestoreWindowBounds();
        Closing += (_, _) => SaveBounds();
    }

    /// <summary>
    /// Restores the last-known window position/size — guarded against a saved position that's no
    /// longer reachable on the current display setup (a real case: a second monitor gets
    /// disconnected between sessions), which would otherwise open the window somewhere invisible
    /// with no way to recover it short of editing settings.json by hand.
    /// </summary>
    private void RestoreWindowBounds()
    {
        var settings = _settingsService.Current;

        if (settings.WindowWidth is > 0 && settings.WindowHeight is > 0)
        {
            Width = settings.WindowWidth.Value;
            Height = settings.WindowHeight.Value;
        }

        if (settings.WindowLeft is { } left && settings.WindowTop is { } top && IsReachable(left, top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Checked against the combined virtual desktop bounds, not a specific monitor's working area —
    /// simpler, and enough to catch the real failure case (the saved position now falls entirely
    /// outside every currently-connected display). Requires a meaningful slice of the title bar to
    /// be reachable, not just any single pixel overlapping, so the window doesn't restore mostly
    /// off-screen and undraggable back.
    /// </summary>
    private static bool IsReachable(double left, double top, double width, double height)
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        return left + 100 <= virtualRight && left + width - 100 >= virtualLeft
            && top + 40 <= virtualBottom && top >= virtualTop - 10;
    }

    private void SaveBounds()
    {
        var settings = _settingsService.Current;
        settings.WindowMaximized = WindowState == WindowState.Maximized;

        // Window.RestoreBounds (a real inherited property, not this class's own RestoreWindowBounds
        // method) holds the pre-maximize size/position even while WindowState is Maximized; saving that
        // instead of the maximized Left/Top/Width/Height means un-maximizing next launch restores
        // somewhere sane instead of the whole screen's own coordinates.
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        settings.WindowLeft = bounds.Left;
        settings.WindowTop = bounds.Top;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;

        _settingsService.Save();
    }
}
