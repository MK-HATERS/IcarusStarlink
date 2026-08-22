using System.Windows;

namespace IcarusStarlink.App.Views;

/// <summary>
/// NewDisplayName is null when the dialog closes via Reset (clear the override, go back to the
/// default name) — distinct from Cancel (DialogResult stays false, caller does nothing at all).
/// </summary>
public partial class RenameModDialog : Window
{
    public string? NewDisplayName { get; private set; }

    public RenameModDialog(string currentDisplayName)
    {
        InitializeComponent();
        NameBox.Text = currentDisplayName;
        NameBox.SelectAll();
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        NewDisplayName = NameBox.Text.Trim();
        DialogResult = true;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        NewDisplayName = null;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
