using System.Windows;

namespace IcarusStarlink.App.Utilities;

/// <summary>Remembers a window's size between openings (the main window does its own, with position).</summary>
public static class WindowSizeMemory
{
    public static void Remember(Window window, string key)
    {
        if (App.Settings is not { } settings)
        {
            return;
        }

        if (settings.Current.WindowSizes.TryGetValue(key, out var size) && size.Width > 200 && size.Height > 150)
        {
            // Never bigger than this screen: a size saved on a larger monitor still fits.
            window.Width = Math.Min(size.Width, SystemParameters.WorkArea.Width);
            window.Height = Math.Min(size.Height, SystemParameters.WorkArea.Height);
            if (size.Maximized)
            {
                window.WindowState = WindowState.Maximized;
            }
        }

        window.Closing += (_, _) =>
        {
            // RestoreBounds whenever it isn't Normal: closed while minimized, ActualWidth/Height are
            // the minimized size, not the window's.
            var bounds = window.WindowState != WindowState.Normal ? window.RestoreBounds : new Rect(0, 0, window.ActualWidth, window.ActualHeight);
            if (bounds.Width > 0 && bounds.Height > 0)
            {
                // Stored at 100% size: UiScale grows the window again by the current scale when it
                // next loads, so a remembered size never compounds with the scale.
                var scale = UiScale.Current;
                settings.Current.WindowSizes[key] = new Core.Settings.SavedWindowSize { Width = bounds.Width / scale, Height = bounds.Height / scale, Maximized = window.WindowState == WindowState.Maximized };
                settings.Save();
            }
        };
    }
}
