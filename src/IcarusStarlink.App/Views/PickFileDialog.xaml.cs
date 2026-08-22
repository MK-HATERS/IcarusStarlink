using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace IcarusStarlink.App.Views;

/// <summary>Lists every real DataTable file under the extracted game data folder, in EXMOD's own dash-flattened CurrentFile convention — "Insert file at location", classic IMM's own name for browsing rather than free-typing a file path.</summary>
public partial class PickFileDialog : Window
{
    private readonly List<string> _allCurrentFiles;

    public string? SelectedCurrentFile { get; private set; }

    public PickFileDialog(string dataFolder)
    {
        InitializeComponent();

        _allCurrentFiles = Directory.Exists(dataFolder)
            ? [.. Directory.EnumerateFiles(dataFolder, "*.json", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(dataFolder, f).Replace('\\', '-').Replace('/', '-'))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)]
            : [];

        FilesListBox.ItemsSource = _allCurrentFiles;
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var filter = FilterBox.Text.Trim();
        FilesListBox.ItemsSource = string.IsNullOrEmpty(filter)
            ? _allCurrentFiles
            : _allCurrentFiles.Where(f => f.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (FilesListBox.SelectedItem is not string selected)
        {
            return;
        }

        SelectedCurrentFile = selected;
        DialogResult = true;
    }

    private void FilesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Add_Click(sender, e);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
