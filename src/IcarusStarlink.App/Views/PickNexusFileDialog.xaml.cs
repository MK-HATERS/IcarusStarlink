using System.Windows;
using System.Windows.Input;
using IcarusStarlink.Catalog.Nexus;

namespace IcarusStarlink.App.Views;

/// <summary>
/// A Nexus mod with more than one downloadable file (different versions, optional add-ons, or a
/// FOMOD-style set of variant packs) needs a real choice instead of silently grabbing whatever the
/// API happens to mark primary — this lists every file the API returned for the mod, in the order
/// Nexus itself returns them (which already puts current/primary files ahead of old ones).
/// </summary>
public partial class PickNexusFileDialog : Window
{
    private readonly List<NexusModFile> _allFilesSorted;

    public NexusModFile? SelectedFile { get; private set; }

    public PickNexusFileDialog(string modName, IEnumerable<NexusModFile> files)
    {
        InitializeComponent();

        _allFilesSorted = [.. files.OrderBy(SortPriority)];
        HeaderText.Text = $"'{modName}' has more than one file available. Pick the one you want:";

        var hasCurrentFiles = _allFilesSorted.Any(f => !IsOldOrArchived(f));
        ShowOldVersionsCheckBox.Visibility = hasCurrentFiles && _allFilesSorted.Any(IsOldOrArchived)
            ? Visibility.Visible
            : Visibility.Collapsed;

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        // Old/archived files are still real, downloadable files — nothing wrong with picking one
        // deliberately (e.g. compatibility with an older game version) — but they'd otherwise bury
        // the handful of files someone actually wants under a long tail of superseded versions
        // (a real mod checked live had 1 current file and 5 old ones). Hidden by default, one
        // checkbox away if genuinely needed.
        var showOld = ShowOldVersionsCheckBox.IsChecked == true;
        var visible = showOld || !_allFilesSorted.Any(f => !IsOldOrArchived(f))
            ? _allFilesSorted
            : [.. _allFilesSorted.Where(f => !IsOldOrArchived(f))];

        var previouslySelected = FilesListBox.SelectedItem as NexusModFile;
        FilesListBox.ItemsSource = visible;
        FilesListBox.SelectedItem = previouslySelected is not null && visible.Contains(previouslySelected)
            ? previouslySelected
            : visible.FirstOrDefault(f => f.IsPrimary) ?? visible.FirstOrDefault();
    }

    private void ShowOldVersionsCheckBox_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

    private static bool IsOldOrArchived(NexusModFile file)
    {
        var category = file.CategoryName?.ToUpperInvariant() ?? "";
        return category.Contains("OLD") || category.Contains("ARCHIV");
    }

    /// <summary>Nexus returns a mod's files in no particular order — the primary/current one needs
    /// to read as the obvious first choice, with old/archived files sinking to the bottom rather
    /// than competing for the top spot.</summary>
    private static int SortPriority(NexusModFile file)
    {
        if (file.IsPrimary)
        {
            return 0;
        }

        if (IsOldOrArchived(file))
        {
            return 4;
        }

        return file.CategoryName?.ToUpperInvariant() switch
        {
            "MAIN" => 1,
            "UPDATE" or "OPTIONAL" or "OPTION" => 2,
            _ => 3,
        };
    }

    private void Download_Click(object sender, RoutedEventArgs e)
    {
        if (FilesListBox.SelectedItem is not NexusModFile selected)
        {
            return;
        }

        SelectedFile = selected;
        DialogResult = true;
    }

    private void FilesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Download_Click(sender, e);

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
