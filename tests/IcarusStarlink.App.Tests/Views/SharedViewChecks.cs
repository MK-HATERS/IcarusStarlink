using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog.Nexus;

namespace IcarusStarlink.App.Tests.Views;

/// <summary>
/// Checks of the app-wide styles and shared views that only show with the styles in Application
/// resources, as in the app. WPF allows one Application per process, so ViewSmokeTests runs these
/// on its own Application thread. Each check returns null when fine, or one sentence saying what is
/// wrong. A "harness:" answer means the check didn't build what it meant to inspect.
/// </summary>
internal static class SharedViewChecks
{
    private const string Presentation = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";

    internal static IReadOnlyList<(string Name, Func<string?> Run)> All { get; } =
    [
        ("Element tooltip", ElementTooltip),
        ("Plain-text tooltip", PlainTextTooltip),
        ("Selected grid row", SelectedGridRow),
        ("Double-click off a row", DoubleClickOffARow),
        ("Busy bar", BusyBar),
    ];

    /// <summary>A tooltip set to an element (a TextBlock, a StackPanel) shows that element, not its type name.</summary>
    private static string? ElementTooltip()
    {
        var tip = (ToolTip)XamlReader.Parse(
            $"<ToolTip {Presentation}><TextBlock Text='EXMOD: a data mod you can edit; Merge and Install merges it with the others. Pak: a ready-made pak, added as it is.'/></ToolTip>");
        var inner = (TextBlock)tip.Content;
        LayOut(tip, 800, 600);

        if (FindDescendant<ContentPresenter>(tip) is not { } presenter || VisualTreeHelper.GetChildrenCount(presenter) == 0)
        {
            return "harness: the tooltip has no content presenter with a child";
        }

        if (!ReferenceEquals(VisualTreeHelper.GetChild(presenter, 0), inner))
        {
            var shown = (VisualTreeHelper.GetChild(presenter, 0) as TextBlock)?.Text ?? VisualTreeHelper.GetChild(presenter, 0).GetType().Name;
            return $"shows '{shown}' instead of the element";
        }

        return inner.TextWrapping == TextWrapping.Wrap ? null : "an element tooltip's text doesn't wrap";
    }

    /// <summary>A plain string tooltip still wraps inside the box.</summary>
    private static string? PlainTextTooltip()
    {
        var tip = (ToolTip)XamlReader.Parse(
            $"<ToolTip {Presentation}>A long hint that has to wrap onto a second line inside the tooltip box to fit</ToolTip>");
        LayOut(tip, 800, 600);

        if (FindDescendant<ContentPresenter>(tip) is not { } presenter || VisualTreeHelper.GetChildrenCount(presenter) == 0)
        {
            return "harness: the tooltip has no content presenter with a child";
        }

        return VisualTreeHelper.GetChild(presenter, 0) is TextBlock { TextWrapping: TextWrapping.Wrap }
            ? null
            : "a plain-text tooltip doesn't wrap";
    }

    /// <summary>A selected row's text keeps the row's colour once the grid has lost focus.</summary>
    private static string? SelectedGridRow()
    {
        var grid = (DataGrid)XamlReader.Parse(
            $"<DataGrid {Presentation}><DataGrid.Columns><DataGridTextColumn Binding='{{Binding Name}}'/></DataGrid.Columns></DataGrid>");
        grid.ItemsSource = new[] { new GridRow("Mod A"), new GridRow("Mod B") };
        LayOut(grid, 400, 200);
        grid.SelectedIndex = 0; // no keyboard focus here, so Selector.IsSelectionActive is false
        grid.UpdateLayout();

        if (grid.ItemContainerGenerator.ContainerFromIndex(0) is not DataGridRow row
            || grid.Columns[0].GetCellContent(row) is not FrameworkElement { Parent: DataGridCell cell })
        {
            return "harness: the grid built no row or cell";
        }

        if (cell.Foreground is not SolidColorBrush cellBrush || row.Foreground is not SolidColorBrush rowBrush)
        {
            return "harness: the row or cell has no solid colour";
        }

        return cellBrush.Color == rowBrush.Color
            ? null
            : $"a selected row's text is {cellBrush.Color} instead of the row's {rowBrush.Color}";
    }

    /// <summary>A double-click on the file list but off its rows (its scrollbar, the blank space) picks nothing.</summary>
    private static string? DoubleClickOffARow()
    {
        var dialog = new PickNexusFileDialog("Some mod",
        [
            new NexusModFile(1, "a.zip", "Main file", "1.0", "MAIN", true),
            new NexusModFile(2, "b.zip", "Optional file", "1.0", "OPTIONAL", false),
        ]);
        try
        {
            if (dialog.FilesListBox.SelectedItem is null)
            {
                return "harness: the picker didn't preselect a file";
            }

            try
            {
                dialog.FilesListBox.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
                {
                    RoutedEvent = Control.MouseDoubleClickEvent,
                    Source = dialog.FilesListBox,
                });
            }
            catch (InvalidOperationException)
            {
                // Accepting sets DialogResult on a window not shown as a dialog, which throws after SelectedFile is set.
            }

            return dialog.SelectedFile is { } picked ? $"a double-click off the rows picked '{picked.Name}'" : null;
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>A busy bar (IsIndeterminate) shows a moving part in its own colour.</summary>
    private static string? BusyBar()
    {
        var bar = (ProgressBar)XamlReader.Parse(
            $"<ProgressBar {Presentation} IsIndeterminate='True' Width='200' Height='8' Foreground='Red'/>");
        bar.ApplyTemplate();
        LayOut(bar, 200, 8);

        if (bar.Template is null)
        {
            return "harness: the bar has no template";
        }

        if (bar.Template.FindName("IndeterminateSegment", bar) is not Border { Visibility: Visibility.Visible } segment
            || !ReferenceEquals(segment.Background, bar.Foreground))
        {
            return "a busy bar has no moving part";
        }

        return bar.Template.FindName("ValueFill", bar) is FrameworkElement { Visibility: Visibility.Collapsed }
            ? null
            : "a busy bar still shows its value fill";
    }

    private static void LayOut(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private sealed record GridRow(string Name);
}
