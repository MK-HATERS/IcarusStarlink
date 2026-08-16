using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
