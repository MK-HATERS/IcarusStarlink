using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>"Icarus updated — here's what changed" (GameUpdateReviewViewModel). Not modal: the Library stays usable while it's open.</summary>
public partial class GameUpdateWindow : Window
{
    public GameUpdateWindow(GameUpdateReviewViewModel viewModel)
    {
        InitializeComponent();
        Utilities.WindowSizeMemory.Remember(this, "game-update");
        DataContext = viewModel;
        viewModel.Close = Close;

        // Closing mid-run would hide the steps, not stop them: it waits for the run.
        Closing += (_, e) =>
        {
            if (viewModel.IsRunning)
            {
                e.Cancel = true;
                viewModel.StatusMessage = "Still working: close this once the steps are done.";
            }
        };
    }
}
