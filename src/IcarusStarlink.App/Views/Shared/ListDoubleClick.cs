using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IcarusStarlink.App.Views;

/// <summary>
/// A list's MouseDoubleClick fires for a double-click anywhere on it: its scrollbar, the empty
/// space under the last row, or with the right button. A picker that accepts on a double-click
/// asks this first, so only a left double-click on one of its rows picks that row.
/// </summary>
public static class ListDoubleClick
{
    /// <summary>
    /// True for a left double-click on one of <paramref name="list"/>'s rows. ContainerFromElement
    /// walks visual and logical parents, so a Run inside a row's text (a ContentElement) counts;
    /// it throws on null, hence the type check.
    /// </summary>
    public static bool IsOnItem(ItemsControl list, MouseButtonEventArgs e) =>
        e.ChangedButton == MouseButton.Left
        && e.OriginalSource is DependencyObject source
        && ItemsControl.ContainerFromElement(list, source) is not null;
}
