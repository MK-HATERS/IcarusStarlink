using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using IcarusStarlink.App.Views;

namespace IcarusStarlink.App.Tests.Views;

public class PageSearchTests
{
    /// <summary>Ctrl+F finds the page's search box by what it's called, skipping hidden ones and other text boxes.</summary>
    [Fact]
    public void Find_PicksTheFirstShowingSearchOrFilterBox()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var note = new TextBox { Name = "NoteBox" };
                var hidden = new TextBox { Tag = "Search talents…" };
                var hiddenPanel = new StackPanel { Visibility = Visibility.Collapsed, Children = { hidden } };
                var search = new TextBox();
                AutomationProperties.SetName(search, "Search the IMM Database");
                var filter = new TextBox();
                AutomationProperties.SetName(filter, "Filter log");
                var page = new StackPanel { Children = { note, hiddenPanel, search, filter } };

                Assert.Same(search, PageSearch.Find(page));

                page.Children.Remove(search);
                Assert.Same(filter, PageSearch.Find(page));

                page.Children.Remove(filter);
                Assert.Null(PageSearch.Find(page));
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
}
