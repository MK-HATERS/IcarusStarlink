using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>The Talents, Workshop, Missions and Mounts tabs' tree view, laid out like the game's menu. DataContext: a TalentTreeBrowserViewModel.</summary>
public partial class TalentTreeView : UserControl
{
    public TalentTreeView() => InitializeComponent();

    /// <summary>Fit needs the space the tree has, which only the view knows (less a little, so no scroll bar appears).</summary>
    private void OnFitClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is TalentTreeBrowserViewModel browser)
        {
            browser.FitTo(TreeScroller.ActualWidth - 24, TreeScroller.ActualHeight - 24);
        }
    }
}
