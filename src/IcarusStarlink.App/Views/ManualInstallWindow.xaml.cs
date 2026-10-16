using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using IcarusStarlink.App.Services;

namespace IcarusStarlink.App.Views;

/// <summary>A file or folder of the download, for the tree.</summary>
public sealed partial class DownloadTreeNode : ObservableObject
{
    public required string FullPath { get; init; }
    public required string Display { get; init; }
    public ObservableCollection<DownloadTreeNode> Children { get; } = [];

    [ObservableProperty]
    private bool _isExpanded;
}

/// <summary>See ManualInstaller. Returns the edited plan; the caller installs it.</summary>
public partial class ManualInstallWindow : Window
{
    private readonly string _root;
    private readonly string _modName;
    private readonly ObservableCollection<ManualInstallItem> _plan;

    public IReadOnlyList<ManualInstallItem> Plan => _plan;

    public ManualInstallWindow(string root, string modName, string reason, IEnumerable<ManualInstallItem> suggestions)
    {
        InitializeComponent();
        _root = root;
        _modName = modName;
        _plan = new ObservableCollection<ManualInstallItem>(suggestions);
        HeaderText.Text = $"Starlink couldn't install '{modName}' by itself: {reason}\n\nChoose what to install and what each piece is. Starlink's best guesses are on the right — "
            + "leave out anything you don't want, or select something in the download and add it.";
        PlanList.ItemsSource = _plan;
        _plan.CollectionChanged += (_, _) => RefreshEmpty();
        RefreshEmpty();

        var top = Build(root, isRoot: true);
        top.IsExpanded = true;
        FilesTree.ItemsSource = new[] { top };
    }

    /// <summary>Shows the window over the main window; null when cancelled.</summary>
    public static IReadOnlyList<ManualInstallItem>? Ask(string root, string modName, string reason, IEnumerable<ManualInstallItem> suggestions)
    {
        var window = new ManualInstallWindow(root, modName, reason, suggestions) { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true ? window.Plan : null;
    }

    private static DownloadTreeNode Build(string path, bool isRoot = false)
    {
        var node = new DownloadTreeNode { FullPath = path, Display = isRoot ? "(the whole download)" : Path.GetFileName(path) + (Directory.Exists(path) ? "\\" : "") };
        if (Directory.Exists(path))
        {
            foreach (var dir in Directory.GetDirectories(path).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                node.Children.Add(Build(dir));
            }

            foreach (var file in Directory.GetFiles(path).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                node.Children.Add(new DownloadTreeNode { FullPath = file, Display = Path.GetFileName(file) });
            }
        }

        return node;
    }

    private void RefreshEmpty() => EmptyText.Visibility = _plan.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void AddSelected(ManualInstallKind kind)
    {
        if (FilesTree.SelectedItem is not DownloadTreeNode node)
        {
            ShowProblem("Select a file or folder in the download first.");
            return;
        }

        // The whole download is named after the mod, not the scratch folder it was unpacked into.
        var name = node.FullPath == _root ? _modName : Path.GetFileNameWithoutExtension(node.FullPath);
        var item = new ManualInstallItem(kind, node.FullPath, ManualInstaller.Relative(_root, node.FullPath), name);
        if (ManualInstaller.Problem(item) is { } problem)
        {
            ShowProblem(problem);
            return;
        }

        _plan.Add(item);
        ShowProblem(null);
    }

    private void AddPak_Click(object sender, RoutedEventArgs e) => AddSelected(ManualInstallKind.PakMod);

    private void AddExmod_Click(object sender, RoutedEventArgs e) => AddSelected(ManualInstallKind.ExmodMod);

    private void AddUe4ss_Click(object sender, RoutedEventArgs e) => AddSelected(ManualInstallKind.Ue4ssMod);

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ManualInstallItem item })
        {
            _plan.Remove(item);
        }
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_plan.Count == 0)
        {
            ShowProblem("Nothing to install — add something from the download, or Cancel.");
            return;
        }

        if (_plan.Select(ManualInstaller.Problem).FirstOrDefault(p => p is not null) is { } problem)
        {
            ShowProblem(problem);
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowProblem(string? text)
    {
        ProblemText.Text = text ?? "";
        ProblemText.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
