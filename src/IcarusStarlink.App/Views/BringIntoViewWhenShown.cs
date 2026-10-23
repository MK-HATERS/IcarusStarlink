using System.Windows;
using System.Windows.Threading;

namespace IcarusStarlink.App.Views;

/// <summary>
/// views:BringIntoViewWhenShown.IsEnabled="True" scrolls the surrounding page so the element is
/// on screen each time it appears. For lists that open under a search box: the mod editor's
/// "Add a stat" sits at the bottom of the fields, so its matches opened below the window's edge
/// (user report 2026-10-23).
/// </summary>
public static class BringIntoViewWhenShown
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(BringIntoViewWhenShown), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.IsVisibleChanged -= OnIsVisibleChanged;
        if (e.NewValue is true)
        {
            element.IsVisibleChanged += OnIsVisibleChanged;
        }
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is FrameworkElement element && e.NewValue is true)
        {
            // After layout, so the page knows the element's real size.
            element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => element.BringIntoView());
        }
    }
}
