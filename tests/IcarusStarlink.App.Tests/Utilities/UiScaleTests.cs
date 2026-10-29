using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IcarusStarlink.App.Utilities;

namespace IcarusStarlink.App.Tests.Utilities;

public class UiScaleTests
{
    /// <summary>
    /// Context menus and tooltips have no visual parent, so the window's transform never reached
    /// them: at 150% the page grew and the Check ▾ menu stayed small. They get the scale as they open.
    /// </summary>
    [Fact]
    public void ContextMenusAndTooltips_GetTheScale_WhenTheyOpen()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                UiScale.Initialize(1.5);
                var menu = new ContextMenu();
                var tip = new ToolTip();

                menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
                tip.RaiseEvent(new RoutedEventArgs(ToolTip.OpenedEvent, tip));

                Assert.Equal(1.5, Assert.IsType<ScaleTransform>(menu.LayoutTransform).ScaleX);
                Assert.Equal(1.5, Assert.IsType<ScaleTransform>(tip.LayoutTransform).ScaleY);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                UiScale.Initialize(UiScale.Normal);
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

    /// <summary>
    /// A question box has a set width and takes its height from its text. At 150% it kept its
    /// 460 width while its content grew, so the text was squeezed and a row of three buttons ran
    /// off the edge. Its set width grows with the scale; its height is left to its content.
    /// </summary>
    [Fact]
    public void AWindowSizedToItsTextsHeight_GetsWider_WithTheScale()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                UiScale.Initialize(1.5);
                var window = new Window { SizeToContent = SizeToContent.Height, Width = 400, MinWidth = 200, Content = new Grid() };

                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent, window));

                Assert.Equal(Math.Min(600, SystemParameters.WorkArea.Width), window.Width);
                Assert.Equal(Math.Min(300, SystemParameters.WorkArea.Width), window.MinWidth);
                Assert.True(double.IsNaN(window.Height));
                Assert.Equal(SizeToContent.Height, window.SizeToContent);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                UiScale.Initialize(UiScale.Normal);
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

    [Fact]
    public void AChoice_ReadsAsItsLabel()
    {
        Assert.Equal("150% — big or 4K screens", UiScale.Choices[^1].ToString());
    }
}
