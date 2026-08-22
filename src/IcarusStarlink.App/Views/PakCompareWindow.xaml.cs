using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>
/// Pak-vs-pak comparison (big-plan item 8b) — non-modal by design, like SearchGameDataWindow:
/// a verification tool used side by side with the rest of the app (e.g. rebuilding a pak, then
/// comparing it against classic IMM's own installed one without closing anything).
/// </summary>
public partial class PakCompareWindow : Window
{
    public PakCompareWindow(PakCompareViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
