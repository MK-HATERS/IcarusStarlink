using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>
/// Shows what a mod's author changed between two versions — opened either straight after a
/// successful update ("see what changed?") or on demand from Library. Non-modal, like the
/// editor's own research windows: it's something to keep open and read alongside the app.
/// </summary>
public partial class ModVersionCompareWindow : Window
{
    public ModVersionCompareWindow(ModVersionCompareViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
