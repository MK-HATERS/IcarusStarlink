using System.Windows;
using IcarusStarlink.App.Views;

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
}
