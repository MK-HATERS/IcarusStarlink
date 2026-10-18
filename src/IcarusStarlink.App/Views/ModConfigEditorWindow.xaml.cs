using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class ModConfigEditorWindow : Window
{
    public ModConfigEditorWindow(ModConfigEditorViewModel viewModel)
    {
        InitializeComponent();
        Utilities.WindowSizeMemory.Remember(this, "mod-config");
        DataContext = viewModel;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
