using System.Windows;

namespace IcarusStarlink.App.Views;

/// <summary>MessageBox.Show's call shape for a Yes/No question in the themed dialog (ThemedConfirmDialog).</summary>
public static class ThemedMessageBox
{
    /// <summary>Owned by the main window; with none (very early or late in the app's life) it centres on the screen.</summary>
    public static bool Show(string message, string title, ThemedConfirmSeverity severity) =>
        ThemedConfirmDialog.Show(Application.Current?.MainWindow, message, title, severity);
}
