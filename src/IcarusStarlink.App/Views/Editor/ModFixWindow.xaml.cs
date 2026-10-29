using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class ModFixWindow : Window
{
    public ModFixWindow(ModFixViewModel viewModel)
    {
        InitializeComponent();
        Utilities.WindowSizeMemory.Remember(this, "mod-fix");
        DataContext = viewModel;
        viewModel.Close = () =>
        {
            if (System.Windows.Interop.ComponentDispatcher.IsThreadModal)
            {
                DialogResult = true;
            }
            else
            {
                Close();
            }
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
