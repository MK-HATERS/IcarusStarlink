using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class MergeConflictsWindow : Window
{
    private readonly MergeConflictsViewModel _viewModel;

    public MergeConflictsWindow(MergeConflictsViewModel viewModel)
    {
        InitializeComponent();
        Utilities.WindowSizeMemory.Remember(this, "merge-conflicts");
        DataContext = _viewModel = viewModel;
    }

    /// <summary>The winners to save (by mod name), set when Save picks is clicked.</summary>
    public Dictionary<string, string>? ResultWinners { get; private set; }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ResultWinners = _viewModel.BuildWinners();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
