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

        // Unsaved edits ask Save / Don't save / Cancel instead of vanishing with the window.
        viewModel.AskSaveOnClose = message => ThemedConfirmDialog.ShowWithCancel(
            this, message, "Unsaved changes", ThemedConfirmSeverity.Question, "Save", "Don't save");
        Closing += (_, e) => e.Cancel = !viewModel.ConfirmClose();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
