using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>
/// A Library mod's version history (ModVersionHistoryViewModel): every backup Starlink kept of it,
/// with Put this version back and Compare with now. Non-modal, like the comparison it opens.
/// </summary>
public partial class ModVersionHistoryWindow : Window
{
    public ModVersionHistoryWindow()
    {
        InitializeComponent();
    }

    public ModVersionHistoryWindow(ModVersionHistoryViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}
