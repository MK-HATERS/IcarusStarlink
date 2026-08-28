using System.Windows;

namespace IcarusStarlink.App.Views;

/// <summary>Thin static entry point mirroring MessageBox.Show's own call shape for this app's 21 real Yes/No confirmation dialogs — see ThemedConfirmDialog for the actual themed window.</summary>
public static class ThemedMessageBox
{
    /// <summary>Defaults to the main window as owner (every real call site is a ViewModel with no Window reference of its own) — WindowStartupLocation.CenterOwner degrades to CenterScreen automatically if that's ever null (e.g. during a very early/late app lifecycle moment).</summary>
    public static bool Show(string message, string title, ThemedConfirmSeverity severity) =>
        ThemedConfirmDialog.Show(Application.Current?.MainWindow, message, title, severity);
}
