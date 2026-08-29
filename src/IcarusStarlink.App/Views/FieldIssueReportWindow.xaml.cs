using System.Windows;
using IcarusStarlink.App.ViewModels;

namespace IcarusStarlink.App.Views;

/// <summary>Read-only report of ExmodFieldValidityChecker findings across the current merge queue — nothing to pick or confirm, just a list pointing at what to go fix in the editor.</summary>
public partial class FieldIssueReportWindow : Window
{
    public FieldIssueReportWindow(IReadOnlyList<FieldIssueRowViewModel> rows)
    {
        InitializeComponent();

        HeaderText.Text = $"{rows.Count} field issue(s) found";
        FindingsGrid.ItemsSource = rows;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
