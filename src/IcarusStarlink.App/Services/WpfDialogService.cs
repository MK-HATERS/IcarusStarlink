using System.Windows;
using IcarusStarlink.App.Views;
using IcarusStarlink.Catalog.Nexus;

namespace IcarusStarlink.App.Services;

/// <summary>Real, production implementation of IDialogService — the only place that actually constructs these WPF Windows.</summary>
public sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string message, string title, ThemedConfirmSeverity severity) =>
        ThemedMessageBox.Show(message, title, severity);

    public RenamePromptResult PromptRename(
        string currentName,
        string description = "Changes how this mod displays in your Library only — its real folder, file name, and mod content are never touched.",
        string? resetValue = null,
        string resetLabel = "Reset to default",
        string resetTooltip = "Clears the override — goes back to the mod's own declared name",
        string title = "Rename mod",
        string fieldLabel = "Display name")
    {
        var dialog = new RenameModDialog(currentName, description, resetValue, resetLabel, resetTooltip, title, fieldLabel)
        {
            Owner = Application.Current?.MainWindow,
        };

        return dialog.ShowDialog() == true
            ? new RenamePromptResult(Cancelled: false, dialog.NewDisplayName)
            : new RenamePromptResult(Cancelled: true, NewDisplayName: null);
    }

    public string? PickFile(string title, string filter, string? initialDirectory = null) =>
        PickFiles(title, filter, initialDirectory, multiselect: false).FirstOrDefault();

    public IReadOnlyList<string> PickFiles(string title, string filter, string? initialDirectory = null) =>
        PickFiles(title, filter, initialDirectory, multiselect: true);

    private static IReadOnlyList<string> PickFiles(string title, string filter, string? initialDirectory, bool multiselect)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter, Multiselect = multiselect };
        if (initialDirectory is { Length: > 0 } && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return Show(dialog) == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string title, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (initialDirectory is { Length: > 0 } && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return Show(dialog) == true ? dialog.FolderName : null;
    }

    public string? PickSaveFile(string title, string filter, string defaultFileName, string? initialDirectory = null)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = defaultFileName };
        if (initialDirectory is { Length: > 0 } && System.IO.Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return Show(dialog) == true ? dialog.FileName : null;
    }

    public NexusModFile? PickNexusFile(string modName, IReadOnlyList<NexusModFile> files, NexusModFile? preselect)
    {
        var dialog = new PickNexusFileDialog(modName, files, preselect) { Owner = OwnerWindow() };
        return dialog.ShowDialog() == true ? dialog.SelectedFile : null;
    }

    public void ShowNexusChangelog(string modName, IReadOnlyList<ChangelogVersionEntry> versions) =>
        new NexusChangelogWindow(modName, versions) { Owner = Application.Current?.MainWindow }.Show();

    private static bool? Show(Microsoft.Win32.CommonDialog dialog) => OwnerWindow() is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();

    /// <summary>The active window (an editor window over the main one), so a picker opens over what the user is looking at.</summary>
    private static Window? OwnerWindow() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
}
