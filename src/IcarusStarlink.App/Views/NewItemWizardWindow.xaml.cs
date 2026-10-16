using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class NewItemWizardWindow : Window
{
    private readonly NewItemWizardViewModel _viewModel;

    public NewItemWizardWindow(NewItemWizardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.TryCreate())
        {
            DialogResult = true;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
