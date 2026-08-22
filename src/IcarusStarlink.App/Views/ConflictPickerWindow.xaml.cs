using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

public partial class ConflictPickerWindow : Window
{
    private readonly ConflictPickerViewModel _viewModel;

    public ConflictPickerWindow(ConflictPickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>Only meaningful when DialogResult is true — the caller's own manualPicks dictionary for the Rebuild that follows.</summary>
    public IReadOnlyDictionary<(string CurrentFile, string ItemName, string FieldName), int>? ResultPicks { get; private set; }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        ResultPicks = _viewModel.BuildPicks();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
