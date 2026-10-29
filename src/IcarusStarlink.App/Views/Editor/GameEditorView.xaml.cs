using System.Windows;
using System.Windows.Controls;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class GameEditorView : UserControl
{
    public GameEditorView()
    {
        InitializeComponent();
    }

    /// <summary>The item list is read from game data the first time the page is shown, not at app start.</summary>
    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && DataContext is GameEditorViewModel viewModel)
        {
            viewModel.EnsureLoaded();
        }
    }
}
