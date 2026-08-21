using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class ExmodEditorWindow : Window
{
    public ExmodEditorWindow(ExmodEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
