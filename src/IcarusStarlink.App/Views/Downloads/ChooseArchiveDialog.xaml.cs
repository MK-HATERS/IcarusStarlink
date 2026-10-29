using System.Windows;
using System.Windows.Input;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Views;

/// <summary>"This download has several alternatives — which one?" (see ExtractedModClassifier's nested-archive rule).</summary>
public partial class ChooseArchiveDialog : Window
{
    public int? SelectedIndex { get; private set; }

    public ChooseArchiveDialog(string modName, IReadOnlyList<ArchiveChoice> choices)
    {
        InitializeComponent();
        HeaderText.Text = $"'{modName}' comes with {choices.Count} alternatives — the author means for you to install one of them. Which one?";
        ChoicesListBox.ItemsSource = choices;
        ChoicesListBox.SelectedIndex = 0;
    }

    /// <summary>The chooser the classifier's callers pass in: shows this dialog over the main window.</summary>
    public static int? Ask(string modName, IReadOnlyList<ArchiveChoice> choices)
    {
        var dialog = new ChooseArchiveDialog(modName, choices) { Owner = Application.Current?.MainWindow };
        return dialog.ShowDialog() == true ? dialog.SelectedIndex : null;
    }

    private void Install_Click(object sender, RoutedEventArgs e) => Accept();

    private void ChoicesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

    private void Accept()
    {
        if (ChoicesListBox.SelectedIndex < 0)
        {
            return;
        }

        SelectedIndex = ChoicesListBox.SelectedIndex;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
