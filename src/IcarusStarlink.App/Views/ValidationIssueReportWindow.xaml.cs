using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>Read-only report combining ExmodFieldValidityChecker and ExmodReferenceChecker findings across the current merge queue — nothing to pick or confirm, just a list pointing at what to go fix in the editor.</summary>
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
