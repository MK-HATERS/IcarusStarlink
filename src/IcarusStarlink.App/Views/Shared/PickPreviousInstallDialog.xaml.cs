using System.Windows;
using System.Windows.Input;
using IcarusStarlink.PakIO.Install;

namespace IcarusStarlink.App.Views;

/// <summary>One kept build as the restore picker shows it: when it was backed up and which mods it had.</summary>
public sealed record PreviousInstallRow(PreviousInstall Install, string When, string Mods, string AllMods)
{
    /// <summary>Names shown on the row before "and N more"; the tooltip lists them all.</summary>
    private const int NamesShown = 5;

    public static PreviousInstallRow From(PreviousInstall install, bool isNewest)
    {
        var when = install.BackedUpAtUtc.ToLocalTime().ToString("g") + (isNewest ? " (newest)" : "");
        if (install.ModNames is not { } names)
        {
            return new PreviousInstallRow(install, when, "Mod list not saved", "Mod list not saved");
        }

        if (names.Count == 0)
        {
            return new PreviousInstallRow(install, when, "No mods", "No mods");
        }

        var count = names.Count == 1 ? "1 mod" : $"{names.Count} mods";
        var shown = string.Join(", ", names.Take(NamesShown));
        var more = names.Count > NamesShown ? $" and {names.Count - NamesShown} more" : "";
        return new PreviousInstallRow(install, when, $"{count}: {shown}{more}", string.Join("\n", names));
    }
}

/// <summary>
/// Restore previous install's picker: every kept merged pak backup, newest first, with its date
/// and mods, so the player can put back any of them, not only the last one.
/// </summary>
public partial class PickPreviousInstallDialog : Window
{
    public PreviousInstall? SelectedInstall { get; private set; }

    /// <param name="keptSetting">Settings › Maintenance › Backups to keep › Merged pak installs, for the note under the list.</param>
    public PickPreviousInstallDialog(IReadOnlyList<PreviousInstall> kept, int keptSetting)
    {
        InitializeComponent();
        InstallsListBox.ItemsSource = kept.Select((install, i) => PreviousInstallRow.From(install, i == 0)).ToList();
        InstallsListBox.SelectedIndex = 0;
        var builds = keptSetting == 1 ? "1 earlier install" : $"{keptSetting} earlier installs";
        KeptNoteText.Text = $"Starlink keeps {builds}. To keep more or fewer, change Backups to keep › Merged pak installs in Settings › Maintenance.";
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (InstallsListBox.SelectedItem is not PreviousInstallRow row)
        {
            return;
        }

        SelectedInstall = row.Install;
        DialogResult = true;
    }

    /// <summary>Only a double-click on a row restores it: one on the scrollbar or the blank space under the rows does nothing.</summary>
    private void InstallsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListDoubleClick.IsOnItem(InstallsListBox, e))
        {
            Restore_Click(sender, e);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
