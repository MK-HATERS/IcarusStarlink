using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>Read-only report of Validate mods' findings across the current merge queue (fields, references, asset collisions, talent trees — see ValidationIssueRowViewModel) — nothing to pick or confirm, just a list pointing at what to go fix in the Mod editor.</summary>
public partial class ValidationIssueReportWindow : Window
{
    public ValidationIssueReportWindow(IReadOnlyList<ValidationIssueRowViewModel> rows)
    {
        InitializeComponent();

        HeaderText.Text = $"{rows.Count} issue(s) found";
        FindingsGrid.ItemsSource = rows;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
