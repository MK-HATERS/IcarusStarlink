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

    /// <summary>The item list is read from game data the first time the page is shown (in the background), not at app start.</summary>
    private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible && DataContext is GameEditorViewModel viewModel)
        {
            await viewModel.EnsureLoadedAsync();
        }
    }
}
