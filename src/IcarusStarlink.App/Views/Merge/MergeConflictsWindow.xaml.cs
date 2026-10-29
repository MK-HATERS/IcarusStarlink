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

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_viewModel.BuildReportText());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program holds the clipboard; a second click works.
        }
    }

    /// <summary>Fix… on a mod: keeps the picks made so far and closes, and the Library opens that mod's fix window.</summary>
    private void Fix_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is QueuedModProblems mod)
        {
            _viewModel.FixFolderName = mod.FolderName;
            ResultWinners = _viewModel.BuildWinners();
            DialogResult = true;
        }
    }
}
