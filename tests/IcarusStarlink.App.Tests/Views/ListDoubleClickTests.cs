using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using IcarusStarlink.App.Views;

namespace IcarusStarlink.App.Tests.Views;

public class ListDoubleClickTests
{
    /// <summary>
    /// Only a left double-click on a row picks it: one on the list itself (its blank space), on its
    /// scrollbar's ScrollViewer, or with the right button doesn't. A Run inside a row's text counts as the row.
    /// </summary>
    [Fact]
    public void IsOnItem_TrueOnlyForALeftDoubleClickOnARow()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var list = (ListBox)XamlReader.Parse("<ListBox xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Height='200'/>");
                var run = new Run("run");
                list.ItemsSource = new object[] { "a", "b", new TextBlock(run) };
                var host = new StackPanel { Children = { list } };
                host.Measure(new Size(300, 300));
                host.Arrange(new Rect(0, 0, 300, 300));
                host.UpdateLayout();

                Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
                var rowText = FindDescendant<TextBlock>((ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0));
                Assert.NotNull(rowText);
                var scrollViewer = FindDescendant<ScrollViewer>(list);
                Assert.NotNull(scrollViewer);

                Assert.True(ListDoubleClick.IsOnItem(list, Click(rowText, MouseButton.Left)));
                Assert.False(ListDoubleClick.IsOnItem(list, Click(list, MouseButton.Left)));
                Assert.False(ListDoubleClick.IsOnItem(list, Click(scrollViewer, MouseButton.Left)));
                Assert.False(ListDoubleClick.IsOnItem(list, Click(rowText, MouseButton.Right)));
                Assert.True(ListDoubleClick.IsOnItem(list, Click(run, MouseButton.Left)));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static MouseButtonEventArgs Click(object source, MouseButton button)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button) { RoutedEvent = Control.MouseDoubleClickEvent, Source = source };
        return args;
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
}
