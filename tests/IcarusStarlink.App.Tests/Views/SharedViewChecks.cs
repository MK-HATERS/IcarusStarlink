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
        ("Primary button label", PrimaryButtonLabel),
        ("Primary button with its own TextBlock", PrimaryButtonOwnTextBlock),
        ("Toggle button label", ToggleButtonLabel),
        ("Level pill text", LevelPillText),
        ("Tab header text", TabHeaderText),
        ("Type tag text", TypeTagText),
        ("Beta tag text", BetaTagText),
        ("Disabled pill button", DisabledPillButton),
        ("Long question box message", LongQuestionMessage),
        ("Question box Ctrl+C", QuestionBoxCopy),
    ];

    /// <summary>A question with a very long message scrolls inside the box, so Yes and No stay on screen.</summary>
    private static string? LongQuestionMessage()
    {
        var dialog = NewQuestionBox(string.Join("\n", Enumerable.Range(1, 80).Select(i => $"Mod number {i}")));
        try
        {
            if (dialog.FindName("MessageTextBlock") is not TextBlock message)
            {
                return "harness: the question box has no MessageTextBlock";
            }

            DependencyObject? parent = message;
            while (parent is not null and not ScrollViewer and not Window)
            {
                parent = LogicalTreeHelper.GetParent(parent) ?? VisualTreeHelper.GetParent(parent);
            }

            if (parent is not ScrollViewer scroller)
            {
                return "the message isn't in a scroll area, so a long one pushes Yes and No off the screen";
            }

            var screen = SystemParameters.WorkArea.Height;
            return scroller.MaxHeight < screen
                ? null
                : $"the message's scroll area can grow to {scroller.MaxHeight} on a {screen} high screen";
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>Ctrl+C in a question box copies it, as in the Windows message box.</summary>
    private static string? QuestionBoxCopy()
    {
        var dialog = NewQuestionBox("Delete this mod?");
        try
        {
            if (dialog.FindName("YesButton") is not Button yes)
            {
                return "harness: the question box has no YesButton";
            }

            return ApplicationCommands.Copy.CanExecute(null, yes)
                ? null
                : "Ctrl+C does nothing in a question box";
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>A Yes/No question box built but not shown (the constructor is private: the app only shows it through the static Show methods).</summary>
    private static ThemedConfirmDialog NewQuestionBox(string message) =>
        (ThemedConfirmDialog)Activator.CreateInstance(
            typeof(ThemedConfirmDialog),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null,
            args: [null, message, "Starlink", ThemedConfirmSeverity.Question],
            culture: null)!;

    /// <summary>A primary button's label is drawn in the theme's "text on accent" colour, not the page text colour.</summary>
    private static string? PrimaryButtonLabel()
    {
        var button = new Button { Style = (Style)Application.Current.FindResource("PrimaryButtonStyle"), Content = "Install" };
        return TextColourIs(button, "Install", "AccentForegroundBrush");
    }

    /// <summary>A TextBlock put inside a primary button (an icon beside a label) takes the button's colour too.</summary>
    private static string? PrimaryButtonOwnTextBlock()
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("PrimaryButtonStyle"),
            Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { new TextBlock { Text = "Install" } } },
        };
        return TextColourIs(button, "Install", "AccentForegroundBrush");
    }

    /// <summary>A toggle button's text follows the colour set on the toggle button.</summary>
    private static string? ToggleButtonLabel()
    {
        var toggle = new System.Windows.Controls.Primitives.ToggleButton { Content = "Pin", Foreground = (Brush)Application.Current.FindResource("DangerBrush") };
        return TextColourIs(toggle, "Pin", "DangerBrush");
    }

    /// <summary>An unselected level pill is faint; the selected one uses the "text on accent" colour.</summary>
    private static string? LevelPillText()
    {
        var pillStyle = (Style)Application.Current.FindResource("LevelPillItemStyle");
        var off = new ListBoxItem { Style = pillStyle, Content = "Off" };
        var low = new ListBoxItem { Style = pillStyle, Content = "Low", IsSelected = true };

        return TextColourIs(off, "Off", "FaintForegroundBrush")
            ?? TextColourIs(low, "Low", "AccentForegroundBrush");
    }

    /// <summary>An unselected tab's header is dimmed; the selected one uses the normal text colour.</summary>
    private static string? TabHeaderText()
    {
        var tabs = new TabControl
        {
            Items =
            {
                new TabItem { Header = "Nexus", Content = new Border() },
                new TabItem { Header = "IMM Database", Content = new Border() },
            },
            SelectedIndex = 0,
        };
        return TextColourIs(tabs, "IMM Database", "SecondaryForegroundBrush")
            ?? TextColourIs(tabs, "Nexus", "ForegroundBrush");
    }

    /// <summary>A mod's type tag writes its text in the type's colour, like its outline.</summary>
    private static string? TypeTagText()
    {
        var tag = new Border { Style = (Style)Application.Current.FindResource("TypeTagStyle"), Child = new TextBlock { Text = "EXMOD" } };
        return TextColourIs(tag, "EXMOD", "TagExmodBrush");
    }

    /// <summary>The Beta tag writes "Beta" in its warning colour, like its outline.</summary>
    private static string? BetaTagText()
    {
        var tag = new Border { Style = (Style)Application.Current.FindResource("BetaTagStyle"), Child = new TextBlock { Text = "Beta" } };
        return TextColourIs(tag, "Beta", "WarningBrush");
    }

    /// <summary>A pill button that can't be used right now looks greyed out, like every other button.</summary>
    private static string? DisabledPillButton()
    {
        var pill = new Button { Style = (Style)Application.Current.FindResource("PillButtonStyle"), Content = "Get update", IsEnabled = false };
        LayOut(pill, 200, 40);
        return pill.Opacity < 1 ? null : "a disabled pill button looks the same as one you can click";
    }

    /// <summary>Lays out <paramref name="root"/> and checks that the TextBlock showing <paramref name="text"/> is drawn in the brush <paramref name="brushKey"/>.</summary>
    private static string? TextColourIs(FrameworkElement root, string text, string brushKey)
    {
        LayOut(root, 400, 200);
        var expected = ((SolidColorBrush)Application.Current.FindResource(brushKey)).Color;
        if (FindText(root, text) is not { } block)
        {
            return $"harness: no TextBlock shows '{text}'";
        }

        return block.Foreground is SolidColorBrush { Color: var actual } && actual == expected
            ? null
            : $"'{text}' is drawn in {(block.Foreground as SolidColorBrush)?.Color.ToString() ?? "no solid colour"} instead of {brushKey} ({expected})";
    }

    private static TextBlock? FindText(DependencyObject parent, string text)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBlock block && block.Text == text)
            {
                return block;
            }

            if (FindText(child, text) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

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
