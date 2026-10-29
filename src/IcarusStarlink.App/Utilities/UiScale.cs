using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace IcarusStarlink.App.Utilities;

/// <summary>One choice in Settings › Appearance › Size of text and controls.</summary>
public sealed record UiScaleChoice(double Value, string Label)
{
    /// <summary>What a screen reader says for the item (a record would read out "UiScaleChoice { Value = … }").</summary>
    public override string ToString() => Label;
}

/// <summary>
/// Settings › Appearance › Size of text and controls (classic IMM's 4K option): every Starlink
/// window is drawn bigger or smaller. A LayoutTransform on each window's content, not a
/// RenderTransform, so layout reflows at the new size and text is laid out again at that size
/// (sharp, not a stretched bitmap). Drop-downs inherit it from their ComboBox; context menus and
/// tooltips have no visual parent, so they get the same transform as they open.
///
/// Applied to every window as it loads (a class handler, so windows built anywhere get it), and
/// to every open window straight away when the setting changes. Pop-up windows (not the main one)
/// also grow or shrink by the same factor so their content still fits; WindowSizeMemory stores
/// sizes at 100% so a remembered size doesn't compound.
/// </summary>
public static class UiScale
{
    public const double Normal = 1.0;
    private const double Min = 0.8;
    private const double Max = 2.0;

    public static IReadOnlyList<UiScaleChoice> Choices { get; } =
    [
        new(0.9, "90% — smaller, fits more"),
        new(1.0, "100% — normal"),
        new(1.15, "115%"),
        new(1.3, "130%"),
        new(1.5, "150% — big or 4K screens"),
    ];

    /// <summary>The scale in use now (1.0 = normal).</summary>
    public static double Current { get; private set; } = Normal;

    private static bool s_registered;

    /// <summary>A saved value that's missing, broken or out of range falls back to something sane (hand-edited settings.json).</summary>
    public static double Normalize(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) || value <= 0 ? Normal : Math.Clamp(value, Min, Max);

    /// <summary>At startup, before the main window is built: remembers the scale and hooks every window that loads from now on.</summary>
    public static void Initialize(double scale)
    {
        Current = Normalize(scale);
        if (!s_registered)
        {
            s_registered = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));

            // Context menus (right-click, More ▾, Check ▾) and tooltips open in a popup of their own
            // with no visual parent, so the window's transform never reaches them (seen at 150%: the
            // page grew, the Check ▾ menu didn't). Drop-downs are fine: their popup sits inside the
            // ComboBox's template, so it inherits the transform. Sub-menus sit inside the menu.
            EventManager.RegisterClassHandler(typeof(ContextMenu), ContextMenu.OpenedEvent, new RoutedEventHandler(OnPopupOpened));
            EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnPopupOpened));
        }
    }

    private static void OnPopupOpened(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement popupRoot && ReferenceEquals(e.OriginalSource, popupRoot))
        {
            ApplyScale(popupRoot);
        }
    }

    /// <summary>The scale as a LayoutTransform on one root element (a window's content, a context menu, a tooltip).</summary>
    internal static void ApplyScale(FrameworkElement root) =>
        root.LayoutTransform = Current == Normal ? Transform.Identity : new ScaleTransform(Current, Current);

    /// <summary>The setting changed: every open window takes the new size now.</summary>
    public static void Set(double scale)
    {
        var previous = Current;
        Current = Normalize(scale);
        if (Application.Current is not { } app || previous == Current)
        {
            return;
        }

        var factor = Current / previous;
        void ApplyToOpenWindows()
        {
            foreach (Window window in app.Windows)
            {
                ApplyTransform(window);
                ResizePopUpWindow(window, factor);
            }
        }

        // Windows belong to the UI thread (the setting is only ever changed there; tests aren't).
        if (app.Dispatcher.CheckAccess())
        {
            ApplyToOpenWindows();
        }
        else
        {
            app.Dispatcher.BeginInvoke(ApplyToOpenWindows);
        }
    }

    private static void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Window window || !ReferenceEquals(e.OriginalSource, window))
        {
            return;
        }

        ApplyTransform(window);
        if (Current != Normal)
        {
            ResizePopUpWindow(window, Current);
        }
    }

    private static void ApplyTransform(Window window)
    {
        if (window.Content is FrameworkElement root)
        {
            ApplyScale(root);
        }
    }

    /// <summary>
    /// A pop-up window with a set size grows with its content (capped to the screen) and keeps its
    /// centre. The main window keeps its own size: it's usually maximised, and its content reflows.
    /// A side the window sizes to its content follows it by itself; a set side is scaled. So a
    /// question box (a set width, its height from its text) gets wider too: it used to keep its
    /// width, its content was squeezed into two thirds of it at 150%, and a row of three buttons
    /// ran off the edge.
    /// </summary>
    private static void ResizePopUpWindow(Window window, double factor)
    {
        if (window is MainWindow || window.WindowState != WindowState.Normal)
        {
            return;
        }

        var scaleWidth = window.SizeToContent is SizeToContent.Manual or SizeToContent.Height && !double.IsNaN(window.Width);
        var scaleHeight = window.SizeToContent is SizeToContent.Manual or SizeToContent.Width && !double.IsNaN(window.Height);
        var workArea = SystemParameters.WorkArea;
        var oldWidth = window.Width;
        var oldHeight = window.Height;
        if (scaleWidth)
        {
            window.MinWidth = Math.Min(window.MinWidth * factor, workArea.Width);
            window.Width = Math.Clamp(oldWidth * factor, window.MinWidth, workArea.Width);
        }

        if (scaleHeight)
        {
            window.MinHeight = Math.Min(window.MinHeight * factor, workArea.Height);
            window.Height = Math.Clamp(oldHeight * factor, window.MinHeight, workArea.Height);
        }

        // Re-centred only, not clamped to the primary screen's work area: that would pull a window
        // off a second monitor. The title bar is kept from going above the top of the screen it's on.
        // A side sized to its content is left to WPF.
        if (scaleWidth && !double.IsNaN(window.Left))
        {
            window.Left -= (window.Width - oldWidth) / 2;
        }

        if (scaleHeight && !double.IsNaN(window.Top))
        {
            window.Top = Math.Max(window.Top - (window.Height - oldHeight) / 2, SystemParameters.VirtualScreenTop);
        }
    }
}
